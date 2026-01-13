using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AdbFileManager {
	public static class AdbProgressRunner {
		public static Func<int, Task>? OnProgressReceived;
		private static int _currentProcessId;
		private static readonly object _processLock = new object();

		/// <summary>
		/// Cancels the currently running ADB process, if any.
		/// </summary>
		public static void Cancel() {
			int pid;
			lock(_processLock) {
				pid = _currentProcessId;
				_currentProcessId = 0;
			}

			if(pid > 0) {
				Log($"[Runner] Cancelling process (PID={pid})");
				try {
					using var process = Process.GetProcessById(pid);
					process.Kill(entireProcessTree: true);
					Log($"[Runner] Process killed successfully");
				}
				catch(ArgumentException) {
					// Process already exited
					Log($"[Runner] Process {pid} already exited");
				}
				catch(Exception ex) {
					Log($"[Runner] Failed to kill process: {ex.Message}");
					// Try taskkill as fallback
					try {
						using var taskkill = Process.Start(new ProcessStartInfo {
							FileName = "taskkill",
							Arguments = $"/F /T /PID {pid}",
							UseShellExecute = false,
							CreateNoWindow = true
						});
						taskkill?.WaitForExit(3000);
					}
					catch { }
				}
			}
		}

		public static async Task RunAsync(string adbPath, string adbArgsString) {
			Log($"[Runner] Starting ADB. Path='{adbPath}' Args='{adbArgsString}'");

			if(string.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("adbPath is required", nameof(adbPath));
			if(!System.IO.File.Exists(adbPath)) throw new FileNotFoundException("adb not found", adbPath);

			adbArgsString ??= string.Empty;

			// Try ConPTY on Windows 10 1809+, fall back to regular Process if unavailable
			if(ConPtySupported()) {
				Log("[Runner] Using ConPTY for terminal emulation");
				await RunWithConPtyAsync(adbPath, adbArgsString);
			}
			else {
				Log("[Runner] ConPTY not available, using standard process");
				await RunWithProcessAsync(adbPath, adbArgsString);
			}
		}

		private static bool ConPtySupported() {
			// ConPTY requires Windows 10 1809 (build 17763) or later
			if(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
			var version = Environment.OSVersion.Version;
			return version.Major > 10 || (version.Major == 10 && version.Build >= 17763);
		}

		private static async Task RunWithConPtyAsync(string adbPath, string adbArgsString) {
			IntPtr inputReadSide = IntPtr.Zero, inputWriteSide = IntPtr.Zero;
			IntPtr outputReadSide = IntPtr.Zero, outputWriteSide = IntPtr.Zero;
			IntPtr hPC = IntPtr.Zero;

			try {
				// Create pipes for communication
				var sa = new SECURITY_ATTRIBUTES { bInheritHandle = true };
				sa.nLength = Marshal.SizeOf(sa);

				if(!CreatePipe(out inputReadSide, out inputWriteSide, ref sa, 0))
					throw new InvalidOperationException("Failed to create input pipe");
				if(!CreatePipe(out outputReadSide, out outputWriteSide, ref sa, 0))
					throw new InvalidOperationException("Failed to create output pipe");

				// Create pseudo console
				var size = new COORD { X = 120, Y = 30 };
				int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out hPC);
				if(hr != 0)
					throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

				Log("[Runner] Pseudo console created");

				// Prepare startup info
				var siEx = new STARTUPINFOEX();
				siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

				IntPtr lpSize = IntPtr.Zero;
				InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
				siEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);

				if(!InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref lpSize))
					throw new InvalidOperationException("InitializeProcThreadAttributeList failed");

				if(!UpdateProcThreadAttribute(siEx.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
					hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
					throw new InvalidOperationException("UpdateProcThreadAttribute failed");

				// Create process
				string cmdLine = $"\"{adbPath}\" {adbArgsString}";
				var pi = new PROCESS_INFORMATION();

				bool success = CreateProcessEx(
					null,
					cmdLine,
					IntPtr.Zero,
					IntPtr.Zero,
					false,
					EXTENDED_STARTUPINFO_PRESENT,
					IntPtr.Zero,
					Path.GetDirectoryName(adbPath),
					ref siEx,  // Pass full STARTUPINFOEX with lpAttributeList
					out pi);

				if(!success)
					throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

				Log($"[Runner] Process created (PID={pi.dwProcessId})");

				// Close handles we don't need
				CloseHandle(pi.hThread);
				CloseHandle(inputReadSide);
				CloseHandle(outputWriteSide);
				inputReadSide = IntPtr.Zero;
				outputWriteSide = IntPtr.Zero;

				// Track the process for cancellation
				lock(_processLock) {
					_currentProcessId = (int)pi.dwProcessId;
				}

				using var process = Process.GetProcessById((int)pi.dwProcessId);

				// Read output from pseudo console
				var outputHandle = outputReadSide;
				outputReadSide = IntPtr.Zero; // Transfer ownership to read task

				var readTask = Task.Run(() => {
					Log("[Runner] Read task started");
					var buffer = new byte[1024];
					int lastProgress = -1;
					int totalBytesRead = 0;

					using var safeHandle = new SafeFileHandle(outputHandle, true); // owns handle
					using var outputStream = new FileStream(safeHandle, FileAccess.Read);

					try {
						// Regex to find progress patterns like "[ 42%]" or "[100%]"
						var progressRegex = new System.Text.RegularExpressions.Regex(@"\[\s*(\d+)%\]");

						int bytesRead;
						while((bytesRead = outputStream.Read(buffer, 0, buffer.Length)) > 0) {
							totalBytesRead += bytesRead;
							string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

							// Extract progress directly from the chunk (don't wait for line delimiters)
							var matches = progressRegex.Matches(text);
							foreach(System.Text.RegularExpressions.Match match in matches) {
								if(int.TryParse(match.Groups[1].Value, out int pct)) {
									if(pct >= 0 && pct <= 100 && pct != lastProgress) {
										lastProgress = pct;
										Log($"[Runner] Progress: {pct}%");
										if(OnProgressReceived != null) {
											_ = Task.Run(() => OnProgressReceived(pct));
										}
									}
								}
							}
						}
						Log($"[Runner] Read loop ended, total bytes: {totalBytesRead}");
					}
					catch(Exception ex) {
						Log($"[Runner] Read error: {ex.Message}");
					}
				});

				// Wait for process to exit
				await process.WaitForExitAsync();

				Log($"[Runner] Process exited with code {process.ExitCode}");

				// Clear process reference
				lock(_processLock) {
					_currentProcessId = 0;
				}

				// Close pseudo console first - this signals EOF to the reader
				if(hPC != IntPtr.Zero) {
					ClosePseudoConsole(hPC);
					hPC = IntPtr.Zero;
				}

				// Now wait for reader to finish (with timeout)
				Log("[Runner] Waiting for read task to complete...");
				if(await Task.WhenAny(readTask, Task.Delay(3000)) != readTask) {
					Log("[Runner] Read task timed out");
				}

				CloseHandle(pi.hProcess);
			}
			finally {
				lock(_processLock) {
					_currentProcessId = 0;
				}
				if(inputReadSide != IntPtr.Zero) CloseHandle(inputReadSide);
				if(inputWriteSide != IntPtr.Zero) CloseHandle(inputWriteSide);
				if(outputReadSide != IntPtr.Zero) CloseHandle(outputReadSide);
				if(outputWriteSide != IntPtr.Zero) CloseHandle(outputWriteSide);
				if(hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
			}
		}

		private static async Task RunWithProcessAsync(string adbPath, string adbArgsString) {
			// Fallback for non-ConPTY systems
			var psi = new ProcessStartInfo {
				FileName = adbPath,
				Arguments = adbArgsString,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(adbPath) ?? AppContext.BaseDirectory
			};

			using var process = new Process { StartInfo = psi };
			process.Start();

			Log($"[Runner] Process started (PID={process.Id})");

			// Track the process for cancellation
			lock(_processLock) {
				_currentProcessId = process.Id;
			}

			var stderrTask = Task.Run(async () => {
				var buffer = new StringBuilder();
				var stream = process.StandardError.BaseStream;
				var byteBuffer = new byte[1];

				while(true) {
					int bytesRead = await stream.ReadAsync(byteBuffer, 0, 1);
					if(bytesRead == 0) break;

					char c = (char)byteBuffer[0];
					if(c == '\r' || c == '\n') {
						if(buffer.Length > 0) {
							string line = buffer.ToString();
							Log("[ADB stderr] " + line);
							int pct = ParseProgress(line);
							if(pct >= 0 && OnProgressReceived != null) {
								_ = Task.Run(() => OnProgressReceived(pct));
							}
							buffer.Clear();
						}
					}
					else {
						buffer.Append(c);
					}
				}
			});

			var stdoutTask = process.StandardOutput.ReadToEndAsync();

			await Task.WhenAll(stderrTask, stdoutTask);
			await process.WaitForExitAsync();

			// Clear process reference
			lock(_processLock) {
				_currentProcessId = 0;
			}

			Log($"[Runner] Process exited with code {process.ExitCode}");
		}

		private static int ParseProgress(string line) {
			if(string.IsNullOrEmpty(line)) return -1;
			int start = line.IndexOf('[');
			int end = line.IndexOf('%');
			if(start >= 0 && end > start) {
				string number = line.Substring(start + 1, end - start - 1).Trim();
				if(int.TryParse(number, out int pct)) return pct;
			}
			return -1;
		}

		private static void Log(string msg) {
			Console.WriteLine($"[DBG] {DateTime.Now:HH:mm:ss.fff} {msg}");
		}

		#region Native Methods

		private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
		private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

		[StructLayout(LayoutKind.Sequential)]
		private struct COORD {
			public short X;
			public short Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SECURITY_ATTRIBUTES {
			public int nLength;
			public IntPtr lpSecurityDescriptor;
			[MarshalAs(UnmanagedType.Bool)]
			public bool bInheritHandle;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct STARTUPINFO {
			public int cb;
			public string lpReserved;
			public string lpDesktop;
			public string lpTitle;
			public int dwX;
			public int dwY;
			public int dwXSize;
			public int dwYSize;
			public int dwXCountChars;
			public int dwYCountChars;
			public int dwFillAttribute;
			public int dwFlags;
			public short wShowWindow;
			public short cbReserved2;
			public IntPtr lpReserved2;
			public IntPtr hStdInput;
			public IntPtr hStdOutput;
			public IntPtr hStdError;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct STARTUPINFOEX {
			public STARTUPINFO StartupInfo;
			public IntPtr lpAttributeList;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct PROCESS_INFORMATION {
			public IntPtr hProcess;
			public IntPtr hThread;
			public uint dwProcessId;
			public uint dwThreadId;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern void ClosePseudoConsole(IntPtr hPC);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool CreateProcess(
			string? lpApplicationName,
			string lpCommandLine,
			IntPtr lpProcessAttributes,
			IntPtr lpThreadAttributes,
			bool bInheritHandles,
			uint dwCreationFlags,
			IntPtr lpEnvironment,
			string? lpCurrentDirectory,
			ref STARTUPINFO lpStartupInfo,
			out PROCESS_INFORMATION lpProcessInformation);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
		private static extern bool CreateProcessEx(
			string? lpApplicationName,
			string lpCommandLine,
			IntPtr lpProcessAttributes,
			IntPtr lpThreadAttributes,
			bool bInheritHandles,
			uint dwCreationFlags,
			IntPtr lpEnvironment,
			string? lpCurrentDirectory,
			ref STARTUPINFOEX lpStartupInfo,
			out PROCESS_INFORMATION lpProcessInformation);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr hObject);

		#endregion
	}
}
