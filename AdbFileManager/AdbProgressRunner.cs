using System;
using System.IO;
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

			// Use standard process output redirection to capture ADB progress
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

			// ADB progress output (with -p flag) goes to stderr
			process.ErrorDataReceived += (sender, e) => {
				if(!string.IsNullOrEmpty(e.Data)) {
					Log("[ADB stderr] " + e.Data);
					int pct = ParseProgress(e.Data);
					if(pct >= 0 && OnProgressReceived != null) {
						Task.Run(() => OnProgressReceived(pct));
					}
				}
			};

			process.OutputDataReceived += (sender, e) => {
				if(!string.IsNullOrEmpty(e.Data)) {
					Log("[ADB stdout] " + e.Data);
				}
			};

			process.Start();
			process.BeginErrorReadLine();
			process.BeginOutputReadLine();

			Log($"[Runner] adb process started (PID={process.Id})");
			await process.WaitForExitAsync();
			Log("[Runner] adb operation finished.");
		}

		// Parse progress from ADB output: "[ 42%] filename"
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
