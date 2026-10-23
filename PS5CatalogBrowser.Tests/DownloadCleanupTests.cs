using System.Diagnostics;
using PS5CatalogBrowser.Core;

internal static class DownloadCleanupTests
{
    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5Cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings"));
            DownloadJob Prepare(string method = "Browser")
            {
                var job = BrowserDownloadFiles.Prepare(new() { Id = "fixture", Title = "Fixture" },
                    new() { Artifact = new() { Id = "artifact" }, Url = "https://example.test/file" }, root, "fixture.pkg", []);
                job.Method = method; File.WriteAllText(job.StagingPath, "synthetic incomplete bytes");
                return job;
            }
            var interrupted = Prepare();
            store.Save(new() { Downloads = [interrupted] });
            var loaded = store.Load().Downloads.Single();
            Check(loaded.State == "Interrupted" && !loaded.IsCleared && File.Exists(loaded.StagingPath), "Interrupted job survives restart and keep preserves bytes");
            BrowserDownloadFiles.DiscardStaging(loaded);
            Check(!Directory.Exists(Path.GetDirectoryName(loaded.StagingPath)), "Explicit discard deletes only its owned staging");
            loaded.State = "Cleanup failed"; loaded.Error = "Staging ownership marker is missing.";
            store.Save(new() { Downloads = [loaded] });
            loaded = store.Load().Downloads.Single();
            Check(loaded.State == "Interrupted" && !loaded.CanResume && loaded.Error.Contains("fresh download link") &&
                !Directory.Exists(Path.GetDirectoryName(loaded.StagingPath)), "absent browser staging is an interrupted download needing a fresh link, without recreating or deleting files");

            var missingMarker = Prepare(); File.Delete(Path.Combine(Path.GetDirectoryName(missingMarker.StagingPath)!, ".owner"));
            store.Save(new() { Downloads = [missingMarker] });
            loaded = store.Load().Downloads.Single();
            Check(loaded.State == "Cleanup failed" && loaded.Error.Contains("marker is missing") && File.Exists(missingMarker.StagingPath), "existing partial without its ownership marker remains protected for manual attention");
            Reject(() => BrowserDownloadFiles.DiscardStaging(loaded));
            Check(File.Exists(missingMarker.StagingPath), "read-only missing-staging inspection cannot authorize deletion of unowned bytes");

            var locked = Prepare(); var sibling = Path.Combine(Path.GetDirectoryName(locked.StagingPath)!, "user-file.txt");
            File.WriteAllText(sibling, "user bytes");
            using (var handle = new FileStream(locked.StagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => BrowserDownloadFiles.DiscardStaging(locked));
            Check(File.Exists(locked.StagingPath), "Locked partial is recoverable");
            BrowserDownloadFiles.DiscardStaging(locked);
            Check(File.ReadAllText(sibling) == "user bytes", "Sibling is preserved");
            BrowserDownloadFiles.DiscardStaging(locked); // Marker remains when a sibling prevents directory removal.

            var unknown = Prepare(); unknown.StagingOwner = "";
            Reject(() => BrowserDownloadFiles.DiscardStaging(unknown));
            Check(File.Exists(unknown.StagingPath), "Unknown ownership prevents deletion");
            unknown.IsCleared = true; store.Save(new() { Downloads = [unknown] });
            loaded = store.Load().Downloads.Single();
            Check(!loaded.IsCleared && loaded.State == "Cleanup failed" && loaded.Error.Contains("unknown"), "Legacy hidden partial is recovered without deletion");
            var wrongOwner = Prepare(); File.WriteAllText(Path.Combine(Path.GetDirectoryName(wrongOwner.StagingPath)!, ".owner"), "another-job");
            Reject(() => BrowserDownloadFiles.DiscardStaging(wrongOwner));
            Check(File.Exists(wrongOwner.StagingPath), "Wrong job marker prevents deletion");
            var tampered = Prepare(); var original = tampered.StagingPath; tampered.StagingPath = sibling;
            Reject(() => BrowserDownloadFiles.DiscardStaging(tampered));
            Check(File.Exists(original) && File.Exists(sibling), "Tampered path cannot delete either file");

            var partial = Prepare("External import"); partial.CleanupPending = true;
            store.Save(new() { Downloads = [partial] });
            File.Delete(partial.StagingPath); // Crash after removing one exact partial, before saving cleared history.
            loaded = store.Load().Downloads.Single();
            Check(!loaded.IsCleared && loaded.Error.Contains("missing"), "Restart reports missing partial accurately");
            BrowserDownloadFiles.DiscardStaging(loaded);
            loaded = store.Load().Downloads.Single();
            Check(!loaded.IsCleared && loaded.Error.Contains("missing"), "Restart after all filesystem cleanup remains recoverable");
            BrowserDownloadFiles.DiscardStaging(loaded);

            var nativePartial = Prepare(); File.WriteAllText(nativePartial.StagingPath + ".crdownload", "native bytes");
            using (var handle = new FileStream(nativePartial.StagingPath + ".crdownload", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => BrowserDownloadFiles.DiscardStaging(nativePartial));
            Check(!File.Exists(nativePartial.StagingPath) && File.Exists(nativePartial.StagingPath + ".crdownload"), "Partial cleanup retains native file and marker for retry");
            BrowserDownloadFiles.DiscardStaging(nativePartial);

            var linked = Prepare(); var directory = Path.GetDirectoryName(linked.StagingPath)!;
            var moved = directory + "-real"; Directory.Move(directory, moved);
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(directory); start.ArgumentList.Add(moved);
            using (var process = Process.Start(start)!) { process.WaitForExit(); Check(process.ExitCode == 0, "Synthetic junction created"); }
            try { Reject(() => BrowserDownloadFiles.DiscardStaging(linked)); Check(File.Exists(Path.Combine(moved, "payload.part")), "Reparse escape preserves target bytes"); }
            finally { Directory.Delete(directory); }
            Console.WriteLine("PASS restart, keep/discard, locks, partial cleanup, ownership, tampering, junction and sibling safety");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception("Unsafe or locked cleanup was accepted.");
    }
}
