using System.IO;
using System.IO.Compression;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class InstallationLifecycleTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5Lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var retain in new[] { false, true })
            {
                var local = Path.Combine(root, retain.ToString());
                var downloads = Path.Combine(local, "downloads"); Directory.CreateDirectory(downloads);
                var archive = Path.Combine(downloads, "fixture.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                    foreach (var (name, content) in new[] { ("wrapper/game/eboot.bin", "synthetic executable"),
                        ("wrapper/game/sce_sys/param.json", "{}"), ("wrapper/companion.txt", "preserve this companion") })
                    { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
                File.WriteAllText(Path.Combine(downloads, "unknown.bin"), "unrecognized source");
                Directory.CreateDirectory(Path.Combine(downloads, "empty"));
                File.WriteAllBytes(Path.Combine(downloads, "empty.pkg"), []);
                var store = new SettingsStore(Path.Combine(local, "settings"));
                store.Save(new() { Sources = [], DownloadRoot = downloads, InstalledGamesRoot = Path.Combine(local, "installed"), RetainOriginalImage = retain });
                var model = new MainViewModel(store);
                using (var locked = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    await model.RefreshDownloadsOnOpenAsync();
                Check(model.Downloads.Single(job => job.Destination == archive).ActualSha256.Length == 0,
                    "first-open inventory reads metadata without opening or hashing the archive");
                Check(model.DownloadView.Cast<DownloadJob>().Count() == 4 && !Directory.Exists(model.InstalledGamesRoot), "scan inventories every source and folder without installing");
                var source = model.Downloads.Single(job => job.Destination == archive);
                var item = model.Library.Single(item => item.DownloadArtifacts.Any(a => a.JobId == source.Id));
                await model.ExtractAsync(item);
                Check(source.ActualSha256.Length == 64, "archive hash is established at installation before source cleanup");
                Check(item.ContentMove is { Phase: "Completed" } && model.LibraryView.Contains(item), "successful extraction enters My Library");
                Check(File.Exists(Path.Combine(item.ContentPath, "eboot.bin")) && Directory.GetParent(item.ContentPath)!.FullName == model.InstalledGamesRoot,
                    "game executable is directly inside a top-level Installed game folder");
                Check(File.ReadAllText(Path.Combine(item.ContentPath, "Archive extras/wrapper/companion.txt")) == "preserve this companion", "archive wrapper companion files are preserved separately from the game root");
                Check(!Directory.EnumerateDirectories(downloads).Any(path => Path.GetFileName(path).Contains("extracted")), "no extracted wrapper remains in Downloads");
                Check(File.Exists(archive) == retain && model.DownloadView.Contains(source) == retain && !model.VisibleInstallations.Any(), "retention controls source and card after success");
                await model.ScanDownloadsAsync();
                Check(model.DownloadView.Cast<DownloadJob>().Count() == (retain ? 4 : 3), "rescan does not resurrect deleted sources or install folders");
                var pkg = Path.Combine(downloads, "failure.pkg");
                var bytes = new byte[4096]; bytes[0] = 0x7f; bytes[1] = 0x46; bytes[2] = 0x49; bytes[3] = 0x48; File.WriteAllBytes(pkg, bytes);
                await model.ScanDownloadsAsync();
                var failedSource = model.Downloads.Single(job => job.Destination == pkg);
                await model.InstallImageAsync(pkg);
                var installation = model.Installations.Last();
                Check(installation.State == InstallationState.Failed && !model.DownloadView.Contains(failedSource) && model.VisibleInstallations.Contains(installation), "failed source moves to Installations only");
                Check(model.CanRetryInstallation(installation) && model.CanDeleteInstallation(installation), "failed installation exposes local retry and delete");
                await model.RetryInstallationAsync(installation);
                Check(model.Installations.Count(job => job.SourcePath == pkg) == 1 && model.SelectedInstallation?.Id == installation.Id, "retry reuses installation record");
                model.ClearAllInstallations();
                Check(model.VisibleInstallations.Contains(installation), "clear history cannot hide a failed installation with files");
                model.Dispose();
                using var restarted = new MainViewModel(store);
                var persisted = restarted.VisibleInstallations.Single(job => job.SourcePath == pkg);
                Check(restarted.CanRetryInstallation(persisted) && restarted.CanDeleteInstallation(persisted) &&
                    !restarted.DownloadView.Cast<DownloadJob>().Any(job => job.Destination == pkg), "failed installation stays retryable after restart");
                Check(restarted.Downloads.Single(job => job.Method == "Folder scan").State == "Imported", "folder scan remains usable after restart");
                var failedItem = restarted.InstallationItem(persisted)!;
                restarted.DeleteLibraryEntry(failedItem, restarted.PrepareLibraryDeletion(failedItem));
                Check(!File.Exists(pkg) && !restarted.VisibleInstallations.Contains(persisted), "delete removes failed source and its installation card");
            }
            Console.WriteLine("PASS download inventory, whole extraction, retain/delete, installation transitions, local retry and deletion");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
