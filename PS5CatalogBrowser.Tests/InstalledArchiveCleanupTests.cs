using System.Security.Cryptography;
using PS5CatalogBrowser.Core;

internal static class InstalledArchiveCleanupTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5ArchiveCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var mode in new[] { "delete", "retain", "changed-source", "changed-installed", "active", "unregistered", "save-before", "save-after", "cancelled" })
            {
                var local = Path.Combine(root, mode); var downloads = Path.Combine(local, "downloads"); Directory.CreateDirectory(downloads);
                var archive = Path.Combine(downloads, "fixture.rar"); File.WriteAllText(archive, "synthetic archive bytes");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
                var job = new DownloadJob { Method = "Browser", State = "Downloaded", Destination = archive, FileName = "fixture.rar", DownloadedBytes = new FileInfo(archive).Length, ActualSha256 = hash };
                var folder = Path.Combine(downloads, "extracted"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "eboot.bin"), "synthetic executable bytes");
                var item = new LocalLibraryEntry { Title = "Synthetic archive", ContentPath = folder, DownloadPath = archive, Extraction = "Extracted",
                    DownloadArtifacts = [new() { JobId = job.Id, Destination = archive, Sha256 = hash, Length = job.DownloadedBytes }] };
                var settings = new AppSettings { DownloadRoot = downloads, InstalledGamesRoot = Path.Combine(local, "installed"), RetainOriginalImage = mode == "retain", Downloads = [job], Library = [item] };
                var store = new SettingsStore(Path.Combine(local, "settings")); store.Save(settings);
                Check(await RunnableContentMove.MoveAsync(settings, item, store.Save, _ => false), "fixture installed before source cleanup");
                if (mode == "changed-source") { var changed = File.ReadAllBytes(archive); changed[0] ^= 0x20; File.WriteAllBytes(archive, changed); }
                if (mode == "changed-installed") { var path = Path.Combine(item.ContentPath, "eboot.bin"); var changed = File.ReadAllBytes(path); changed[0] ^= 0x20; File.WriteAllBytes(path, changed); }
                if (mode == "unregistered") store.Save(new() { Downloads = [job] });
                if (mode == "save-before") store.BeforeSave = _ => throw new IOException("fixture cleanup intent save failure");
                if (mode == "save-after") store.BeforeSave = candidate => { if (candidate.Downloads.Single().IsCleared) throw new IOException("fixture final save failure"); };
                using var cancellation = new CancellationTokenSource(); if (mode == "cancelled") cancellation.Cancel();
                var failed = false;
                try { await InstalledArchiveCleanup.CompleteAsync(settings, item, store, _ => mode == "active", cancellation.Token); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { failed = true; }
                Check(Directory.Exists(item.ContentPath), "installed content always preserved: " + mode);
                if (mode is "delete" or "retain")
                {
                    Check(!failed && File.Exists(archive) == (mode == "retain") && job.IsCleared == (mode != "retain") && job.State == "Installed", "retention choice and hidden completed download: " + mode);
                    var saved = store.LoadForStartup();
                    Check(saved.Library.Single().ContentPath == item.ContentPath && saved.Downloads.Single().IsCleared == (mode != "retain"), "restart retains library and hidden archive history");
                }
                else if (mode == "save-after")
                {
                    Check(failed && !File.Exists(archive) && !job.IsCleared && store.Load().Downloads.Single().ArchiveStatus.Contains("Removing"), "failed final save retains durable cleanup intent and visible recovery row");
                    Check(store.LoadForStartup().Downloads.Single().IsCleared, "restart reconciles absent archive history without resuming any deletion");
                    store.BeforeSave = null;
                    await InstalledArchiveCleanup.CompleteAsync(settings, item, store, _ => false, default);
                    Check(job.IsCleared, "explicit retry reconciles already absent archive without deleting anything else");
                }
                else Check(failed && File.Exists(archive) && !job.IsCleared, "cleanup failure preserves source and download row: " + mode);
            }
            Console.WriteLine("PASS installed archive retain/delete, exact source digest, installed tamper, active ownership, durable registration, save faults and cancellation");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
