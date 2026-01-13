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

			using var process = new Process { StartInfo = psi };
			process.Start();

			Log($"[Runner] adb process started (PID={process.Id})");

			// Read stderr in a separate task - ADB progress uses \r to update in place
			var stderrTask = Task.Run(async () => {
				var buffer = new StringBuilder();
				var stream = process.StandardError;
				var charBuffer = new char[1];

				while(!stream.EndOfStream) {
					int read = await stream.ReadAsync(charBuffer, 0, 1);
					if(read == 0) break;

					char c = charBuffer[0];

					if(c == '\r' || c == '\n') {
						// Line complete - process it
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

				// Process any remaining content
				if(buffer.Length > 0) {
					string line = buffer.ToString();
					Log("[ADB stderr] " + line);

					int pct = ParseProgress(line);
					if(pct >= 0 && OnProgressReceived != null) {
						_ = Task.Run(() => OnProgressReceived(pct));
					}
				}
			});

			// Read stdout (usually empty for push/pull)
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

			Log("[Runner] adb operation finished.");
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
