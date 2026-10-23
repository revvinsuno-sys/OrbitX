using System.Diagnostics;
using PS5CatalogBrowser.Core;

internal static class LaunchLogTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5LaunchLogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ProcessStartInfo Worker(string mode)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!);
                if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(LaunchLogTests).Assembly.Location);
                info.ArgumentList.Add(mode); return info;
            }
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var settings = new AppSettings { Library = [new() { Title = "Synthetic log fixture" }] };
            var logDirectory = Path.Combine(store.DirectoryPath, "launch-logs");
            var launch = KytyLaunchLog.Start(Worker("--launch-log-worker"), logDirectory, id => { settings.Library[0].LastLaunchLogId = id; store.Save(settings); });
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!KytyLaunchLog.Read(logDirectory, launch.LogId).Contains("OUTPUT_READY") && DateTime.UtcNow < deadline) await Task.Delay(25);
            Check(!launch.Completion.IsCompleted && KytyLaunchLog.Read(logDirectory, launch.LogId).Contains("OUTPUT_READY"), "console output is flushed to disk before process exit");
            Check(await launch.Completion.WaitAsync(TimeSpan.FromSeconds(15)) == 37, "failed process exit code is retained");
            var restored = store.Load().Library.Single();
            var text = KytyLaunchLog.Read(logDirectory, restored.LastLaunchLogId);
            Check(text.Contains("STDOUT_BEGIN") && text.Contains("STDERR_BEGIN") && text.Contains("CRASH_TAIL_WITHOUT_NEWLINE") && text.Contains("Exit code: 37 (0x00000025)"), "both busy pipes, partial final line and exit status survive settings reload");
            var quick = KytyLaunchLog.Start(Worker("--launch-log-quick"), logDirectory, _ => { });
            Check(await quick.Completion.WaitAsync(TimeSpan.FromSeconds(15)) == 7 && KytyLaunchLog.Read(logDirectory, quick.LogId).Contains("QUICK_EXIT"), "immediate exit is captured without an event subscription race");
            string failedId = "";
            try { KytyLaunchLog.Start(new(Path.Combine(root, "missing.exe")), logDirectory, id => failedId = id); throw new Exception("Missing executable started"); }
            catch (System.ComponentModel.Win32Exception) { Check(KytyLaunchLog.Read(logDirectory, failedId).Contains("Launch failed:"), "process start failure leaves a reviewable log"); }
            Check(!KytyLaunchLog.Exists(logDirectory, "../../settings"), "log identifiers cannot select arbitrary files");
            var largeId = Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(logDirectory, largeId + ".log"), new string('x', 3 * 1024 * 1024) + "LOG_END");
            var tail = KytyLaunchLog.Read(logDirectory, largeId);
            Check(tail.StartsWith("Showing the last 2 MiB") && tail.EndsWith("LOG_END") && tail.Length < 2 * 1024 * 1024 + 200, "viewer bounds large logs while keeping the latest output");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
