using System.IO;
using System.Text.Json;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class DownloadMetadataTests
{
    internal static async Task AuditAsync()
    {
        // Read production settings/catalogues only; the view model owns an isolated empty store.
        var actual = new SettingsStore(); var settings = actual.Load();
        var root = Path.Combine(Path.GetTempPath(), "PS5DownloadAudit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            store.Save(new() { Sources = settings.Sources, DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = Path.Combine(root, "installed") });
            using var model = new MainViewModel(store);
            using var transport = new HttpTransfer();
            foreach (var source in settings.Sources.Where(source => source.Enabled))
            {
                try
                {
                    var entries = source.SourceType == CatalogSourceType.OrbitLive ? (await LiveOrbitCatalog.LoadAsync(source, transport)).Entries :
                        source.SourceType == CatalogSourceType.OrbitLocal ? (await LocalOrbitCatalog.LoadAsync(source)).Entries :
                        (await new CatalogCache().LoadAsync(source, CancellationToken.None))?.Entries ?? [];
                    foreach (var entry in entries) model.Entries.Add(entry);
                }
                catch (Exception ex) { Console.WriteLine("Catalogue unavailable: " + source.Name + " · " + ex.Message); }
            }
            var installations = new InstallationJournal(Path.Combine(actual.DirectoryPath, "installations")).Load();
            foreach (var job in settings.Downloads.Where(job => !job.IsCleared))
            {
                var installation = installations.LastOrDefault(item => !item.IsCleared && ImageFileSystem.SamePath(item.SourcePath, job.Destination));
                Console.WriteLine(JsonSerializer.Serialize(new { id = MainViewModel.DownloadTitleId(job), title = model.DownloadMetadata(job).Title,
                    file = Path.GetFileName(job.Destination), state = job.State, metadata = model.DownloadMetadata(job).Status,
                    exists = File.Exists(job.Destination) || Directory.Exists(job.Destination), installation = installation?.StateLabel,
                    installationError = installation?.Error }));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5DownloadMetadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var source = new CatalogSource { Id = "source", Name = "Fixture", Enabled = true };
            store.Save(new() { Sources = [source], DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = Path.Combine(root, "installed") });
            using var model = new MainViewModel(store);
            Directory.CreateDirectory(model.DownloadRoot);
            var file = Path.Combine(model.DownloadRoot, "[Fixture]-PPSA12345 (1).rar");
            File.WriteAllText(file, "synthetic archive");
            var job = new DownloadJob { Method = "Scan", Title = Path.GetFileName(file), FileName = Path.GetFileName(file), Destination = file,
                DownloadedBytes = new FileInfo(file).Length, ArchiveStatus = "Ambiguous metadata" };
            DownloadCompletion.RegisterScanned(model.Settings, job, store.Save);
            model.Downloads.Add(job);
            model.Library.Add(model.Settings.Library.Single());
            var entry = new CatalogEntry { Id = "game", SourceId = source.Id, Title = "Resolved game", GameId = "resolved", Platform = "PS5", TitleId = "PPSA12345",
                Artifacts = [new() { Id = "release-a", TitleId = "PPSA12345", FileName = "PPSA12345.rar", Url = "https://fixture.invalid/a", Size = 17 },
                    new() { Id = "release-b", TitleId = "PPSA12345", FileName = "PPSA12345.rar", Url = "https://fixture.invalid/b", Size = 17 }] };
            model.Entries.Add(entry);
            using var api = new OrbitFrontendApi(model);
            var before = JsonSerializer.Serialize(model.Settings);
            Check(model.DownloadMetadata(job).Title == "Resolved game" && model.DownloadMetadata(job).Status.Contains("release unverified"), "duplicate releases resolve one game without asserting a release");
            Check(api.StoreGameId(job) == api.Catalog.Releases.First().GameId, "title ID opens exact game page");
            Check(api.DownloadActions(job).Contains("findDownloadGame") && api.DownloadActions(job).Contains("deleteDownload") && api.DownloadActions(job).Contains("installDownload"), "scanned ambiguous archives expose search, confirmed delete and installation");
            Check(model.PrepareLibraryDeletion(model.DownloadDeletionItem(job)!).Single().Path == file, "delete uses existing exact-path ownership confirmation");
            Check(JsonSerializer.Serialize(model.Settings) == before && job.ArtifactId.Length == 0 && job.ExpectedSha256.Length == 0, "presentation does not change completion evidence or invent a selected release");
            model.Entries.Add(new CatalogEntry { Id = "conflict", SourceId = source.Id, GameId = "other", Platform = "PS5", Title = "Another game", TitleId = "PPSA12345", Artifacts = [new() { Id = "c", Url = "https://fixture.invalid/c" }] });
            Check(api.StoreGameId(job) is null && model.DownloadMetadata(job).Title == job.Title, "conflicting games remain ambiguous");
            model.Entries.RemoveAt(1);
            job.Title = "PPSA12345 and PPSA54321";
            Check(MainViewModel.DownloadTitleId(job).Length == 0 && api.StoreGameId(job) is null, "multiple title IDs do not select an arbitrary game");
            job.Title = "PPSA12345";
            model.Sources.Single().Enabled = false;
            Check(model.DownloadCatalogEntries(job).Length == 0, "disabled sources cannot supply a title-ID match");
            model.Sources.Single().Enabled = true;
            // Missing completed sources request the store without recreating or deleting anything.
            var missing = new DownloadJob { Title = entry.Title, SourceId = source.Id, EntryId = entry.Id, State = "Missing",
                Error = "Downloaded file is missing.", Destination = Path.Combine(model.DownloadRoot, "missing.rar"),
                Completion = new() { Phase = "Completed" } };
            model.Settings.Downloads.Add(missing); model.Downloads.Add(missing);
            Check(model.CanRetryDownload(missing) && await model.RetryDownloadAsync(missing), "missing completed download exposes Retry and requests its game page");
            Check(!File.Exists(missing.Destination) && File.ReadAllText(file) == "synthetic archive" && missing.State == "Missing", "retry only navigates and preserves source evidence");
            Check(api.DownloadActions(missing).Contains("retryDownload") && api.StoreGameId(missing) == api.StoreGameId(job), "Retry targets the original game page");
            var empty = Path.Combine(model.DownloadRoot, "PPSA12345-extracted"); Directory.CreateDirectory(empty);
            var folder = DownloadCompletion.RegisterScannedFolder(model.Settings, empty, store.Save);
            model.Library.Add(folder);
            var folderJob = model.Settings.Downloads.Single(item => item.Method == "Folder scan"); model.Downloads.Add(folderJob);
            Check(model.DownloadMetadata(folderJob).Status == "Empty extracted folder" && !model.CanRunDownload(folderJob) && model.CanDeleteDownload(folderJob), "empty wrappers offer deletion instead of installation");
            Console.WriteLine("PASS download metadata: title-ID fallback, ambiguous releases/games, disabled sources, delete ownership, missing-file Retry and unchanged evidence");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
