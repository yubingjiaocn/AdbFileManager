using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace AdbFileManager {
	public static class AdbProgressRunner {
		public static Func<int, Task>? OnProgressReceived;

		public static async Task RunAsync(string adbPath, string adbArgsString) {
			Log($"[Runner] Starting ADB. Path='{adbPath}' Args='{adbArgsString}'");

			if(string.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("adbPath is required", nameof(adbPath));
			if(!System.IO.File.Exists(adbPath)) throw new FileNotFoundException("adb not found", adbPath);

			adbArgsString ??= string.Empty;

			var psi = new ProcessStartInfo {
				FileName = adbPath,
				Arguments = adbArgsString,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(adbPath) ?? AppContext.BaseDirectory
			};

			// Try to force unbuffered output
			psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
			psi.EnvironmentVariables["ADB_TRACE"] = "adb";

			using var process = new Process { StartInfo = psi };
			process.Start();

			Log($"[Runner] adb process started (PID={process.Id})");

			// Read stderr byte-by-byte to catch \r-terminated progress updates
			var stderrTask = Task.Run(async () => {
				var buffer = new StringBuilder();
				var stream = process.StandardError.BaseStream;
				var byteBuffer = new byte[1];
				int lastProgress = -1;

				Log("[Runner] Starting stderr read loop");

				try {
					while(true) {
						int bytesRead = await stream.ReadAsync(byteBuffer, 0, 1);
						if(bytesRead == 0) {
							Log("[Runner] stderr EOF reached");
							break;
						}

						char c = (char)byteBuffer[0];

						if(c == '\r' || c == '\n') {
							if(buffer.Length > 0) {
								string line = buffer.ToString();
								Log("[ADB stderr] " + line);

								int pct = ParseProgress(line);
								if(pct >= 0 && pct != lastProgress) {
									lastProgress = pct;
									Log($"[Runner] Progress parsed: {pct}%");
									if(OnProgressReceived != null) {
										_ = Task.Run(() => OnProgressReceived(pct));
									}
								}

								buffer.Clear();
							}
						}
						else {
							buffer.Append(c);
						}
					}
				}
				catch(Exception ex) {
					Log($"[Runner] stderr read error: {ex.Message}");
				}

				// Process remaining buffer
				if(buffer.Length > 0) {
					string line = buffer.ToString();
					Log("[ADB stderr final] " + line);

					int pct = ParseProgress(line);
					if(pct >= 0 && OnProgressReceived != null) {
						_ = Task.Run(() => OnProgressReceived(pct));
					}
				}

				Log("[Runner] stderr read loop finished");
			});

			// Read stdout
			var stdoutTask = Task.Run(async () => {
				string? line;
				while((line = await process.StandardOutput.ReadLineAsync()) != null) {
					if(!string.IsNullOrEmpty(line)) {
						Log("[ADB stdout] " + line);
					}
				}
			});

			await Task.WhenAll(stderrTask, stdoutTask);
			await process.WaitForExitAsync();

			Log($"[Runner] adb process exited with code {process.ExitCode}");
		}

		// Parse progress from ADB output: "[ 42%] filename" or "[100%] filename"
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
	}
}
