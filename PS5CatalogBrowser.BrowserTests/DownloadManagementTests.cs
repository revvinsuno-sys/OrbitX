using System.IO;
using System.Security.Cryptography;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class DownloadManagementTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5DownloadManagement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var startupRoot = Path.Combine(root, "restart");
            var startupDownloads = Path.Combine(startupRoot, "downloads"); Directory.CreateDirectory(startupDownloads);
            var startupInstalled = Path.Combine(startupRoot, "installed", "One game folder"); Directory.CreateDirectory(startupInstalled);
            File.WriteAllText(Path.Combine(startupInstalled, "eboot.bin"), "preserved installed content");
            var rootFile = Path.Combine(startupDownloads, "pending.pkg"); File.WriteAllText(rootFile, "pending");
            var unsupportedFile = Path.Combine(startupDownloads, "asset.bin"); File.WriteAllText(unsupportedFile, "preserved asset");
            var obsoleteNested = new DownloadJob { Method = "Scan", State = "Missing", Destination = Path.Combine(startupDownloads, "old-folder", "nested.pkg") };
            var obsoleteRoot = new DownloadJob { Method = "Scan", State = "Changed", Destination = unsupportedFile };
            var pending = new DownloadJob { Method = "Scan", State = "Imported", Destination = rootFile, DownloadedBytes = new FileInfo(rootFile).Length };
            var removedPackage = new DownloadJob { Method = "Scan", State = "Missing", IsCleared = true, Destination = Path.Combine(startupDownloads, "installed.pkg") };
            var installedItem = new LocalLibraryEntry { Title = "One game folder", ContentPath = startupInstalled };
            var container = new LocalLibraryEntry { Title = "PS5", ContentPath = startupRoot };
            LocalLibraryEntry ScanItem(DownloadJob job) => new() { ContentPath = job.Destination, DownloadPath = job.Destination,
                DownloadRegistrationId = job.Id, DownloadArtifacts = [new() { JobId = job.Id, Destination = job.Destination }] };
            var startupStore = new SettingsStore(Path.Combine(startupRoot, "settings"));
            startupStore.Save(new() { Sources = [], DownloadRoot = startupDownloads, InstalledGamesRoot = Path.GetDirectoryName(startupInstalled)!,
                Downloads = [obsoleteNested, obsoleteRoot, pending, removedPackage],
                Library = [installedItem, container, ScanItem(obsoleteNested), ScanItem(obsoleteRoot), ScanItem(removedPackage), ScanItem(pending)] });
            for (var restart = 0; restart < 2; restart++)
            {
                using var restarted = new MainViewModel(startupStore);
                Check(restarted.Downloads.Count == 2 && restarted.Downloads.Any(job => job.Id == pending.Id) && restarted.LibraryView.Cast<LocalLibraryEntry>().Count() == 1,
                    "startup retires obsolete missing/changed scan rows and preserves one installed folder across repeated restart");
            }
            var repaired = startupStore.Load();
            Check(repaired.Library.Count == 3 && repaired.Library.Any(item => item.DownloadRegistrationId == pending.Id),
                "startup retires container and cleared obsolete scan metadata but preserves pending root package registration");
            Check(LibraryDeletion.Prepare(repaired, repaired.Library.Single(item => item.Id == installedItem.Id), [], _ => false).Count == 1,
                "repaired installed folder can prepare exact-path deletion without deleting any files");
            var competing = new LocalLibraryEntry { Title = "Real shared entry", ContentPath = startupInstalled }; repaired.Library.Add(competing);
            try { LibraryDeletion.Prepare(repaired, repaired.Library.Single(item => item.Id == installedItem.Id), [], _ => false); throw new Exception("Shared deletion accepted"); }
            catch (IOException ex) { Check(ex.Message.Contains(competing.Title) && ex.Message.Contains(startupInstalled), "real sharing stays protected and identifies conflicting entry and path"); }
            repaired.Library.Add(container);
            try { InstalledLibrary.RetireObsoleteReferences(repaired, _ => throw new IOException("fixture save failure")); throw new Exception("Failed save accepted"); }
            catch (IOException) { Check(repaired.Library.Contains(container), "failed reconciliation save preserves live metadata"); }
            Check(File.ReadAllText(unsupportedFile) == "preserved asset" && File.Exists(Path.Combine(startupInstalled, "eboot.bin")), "startup reconciliation never deletes scan assets or installed files");
            var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Sources = [new() { Id = "fixture", Name = "Fixture" }], DownloadRoot = downloads, InstalledGamesRoot = Path.Combine(root, "installed"), RetainOriginalImage = true });
            using var model = new MainViewModel(store);
            var bytes = "synthetic archive content"u8.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            CatalogEntry Entry(string id, string filename, string title) => new() { SourceId = "fixture", Id = id, Title = title, SourceName = "Fixture", Artifacts = [new() { Id = id, FileName = filename, Size = bytes.Length, Sha256 = digest, Url = "https://fixture.invalid/" + filename }] };
            model.Entries.Add(Entry("matched", "matched.rar", "Matched catalogue title"));
            model.Entries.Add(Entry("ambiguous1", "ambiguous.rar", "First release"));
            model.Entries.Add(Entry("ambiguous2", "ambiguous.rar", "Second release"));
            foreach (var name in new[] { "matched.rar", "ambiguous.rar", "unknown.zip", "incomplete.part", "readme.txt", "asset.bin", "unsupported.7z" }) File.WriteAllBytes(Path.Combine(downloads, name), bytes);
            var nested = Path.Combine(downloads, "nested"); Directory.CreateDirectory(nested); File.WriteAllBytes(Path.Combine(nested, "nested.pkg"), bytes);
            foreach (var generated in new[] { "game-extracted (1)", ".ps5catalog-staging" })
            { var directory = Path.Combine(downloads, generated); Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "preserved.pkg"), bytes); }
            // Simulate old versions that recursively registered extracted assets as downloads.
            var legacyScanJob = new DownloadJob { Method = "Scan", State = "Imported", Destination = Path.Combine(nested, "nested.pkg"), FileName = "nested.pkg", ActualSha256 = digest, DownloadedBytes = bytes.Length };
            var legacyItem = new LocalLibraryEntry { DownloadRegistrationId = legacyScanJob.Id, ContentPath = legacyScanJob.Destination, DownloadPath = legacyScanJob.Destination,
                DownloadArtifacts = [new() { JobId = legacyScanJob.Id, Destination = legacyScanJob.Destination }] };
            model.Settings.Downloads.Add(legacyScanJob); model.Downloads.Add(legacyScanJob); model.Settings.Library.Add(legacyItem); model.Library.Add(legacyItem); store.Save(model.Settings);
            var orphanScan = new DownloadJob { Method = "Scan", State = "Imported", Destination = Path.Combine(downloads, "missing-folder", "old.pak"), FileName = "old.pak" };
            model.Settings.Downloads.Add(orphanScan); model.Downloads.Add(orphanScan); store.Save(model.Settings);
            await model.ScanDownloadsAsync();
            Check(model.Downloads.Count == 9 && model.Settings.Library.Count == 9, "all root files and extracted folders are inventoried as Downloads");
            Check(legacyScanJob.IsCleared && legacyScanJob.State == "Imported" && !model.Settings.Library.Contains(legacyItem) && !model.Library.Contains(legacyItem), "obsolete nested scan rows and registrations retired without deleting content");
            Check(orphanScan.IsCleared && !model.Downloads.Contains(orphanScan), "old nested scan rows are hidden even when their original folder is already gone");
            Check(!model.LibraryView.Cast<LocalLibraryEntry>().Any() && Directory.Exists(nested) &&
                File.ReadAllBytes(Path.Combine(nested, "nested.pkg")).SequenceEqual(bytes), "scan inventories folders without installing or moving their content");
            Check(!model.IsScanningDownloads && model.CanScanDownloads && model.ScanStatus.StartsWith("Scan complete:") && model.ScanDownloadsLabel == "Scan downloads", "scan completion restores controls and clears busy state");
            var matched = model.Downloads.Single(job => job.FileName == "matched.rar");
            Check(matched.Title == "matched.rar" && matched.ActualSha256.Length == 0 && matched.ArchiveStatus == "Unverified metadata", "metadata inventory defers hash-based catalogue identity until verification");
            Check(model.Downloads.Single(job => job.FileName == "ambiguous.rar").ArchiveStatus == "Ambiguous metadata" && model.Downloads.Single(job => job.FileName == "unknown.zip").ArchiveStatus == "Unmatched metadata", "ambiguous and unknown files never acquire guessed metadata");
            Check(Directory.EnumerateFiles(downloads, "*", SearchOption.AllDirectories).Count() == 10 && File.ReadAllBytes(matched.Destination).SequenceEqual(bytes), "root assets, unsupported archives and private staging remain untouched");
            model.SelectedJob = matched;
            Check(!model.CanClearDownload(matched), "history clear cannot hide a source file that still exists");
            matched.IsCleared = true; matched.State = "Missing"; model.Downloads.Remove(matched); store.Save(model.Settings);
            await model.ScanDownloadsAsync(); await model.ScanDownloadsAsync();
            Check(model.Downloads.Count == 9 && store.Load().Downloads.Count == 11 && !matched.IsCleared && matched.State == "Missing" && !model.LibraryView.Cast<LocalLibraryEntry>().Any(), "scan restores root history and is idempotent without duplicating folder items");
            model.SelectedJob = model.Downloads.Single(job => job.FileName == "unknown.zip");
            Check(model.CanRunDownloadAction, "completed scan restores Install/extract actions on imported root files");
            var failingFolder = Path.Combine(downloads, "save-failure"); Directory.CreateDirectory(failingFolder); File.WriteAllBytes(Path.Combine(failingFolder, "asset.dat"), bytes);
            store.BeforeSave = _ => throw new IOException("fixture registration save failure");
            await model.ScanDownloadsAsync(); store.BeforeSave = null;
            Check(Directory.Exists(failingFolder) && !model.Settings.Library.Any(item => item.ContentPath == failingFolder) &&
                !model.IsScanningDownloads && model.CanScanDownloads && model.ScanStatus.Contains("fixture registration save failure"), "failed folder registration preserves files, publishes no library row and returns scan control with a final explanation");
            Directory.Delete(failingFolder, true);
            var failed = BrowserDownloadFiles.Prepare(new() { Id = "failed", Title = "Failed fixture" }, new() { Url = "https://fixture.invalid/fail", Artifact = new() }, downloads, "failed.pkg", model.Downloads);
            failed.State = "Failed"; File.WriteAllBytes(failed.StagingPath, bytes); model.Settings.Downloads.Add(failed); model.Downloads.Add(failed); store.Save(model.Settings);
            model.SelectedJob = failed;
            Check(model.CanDeleteDownload(failed), "failed inactive download exposes Delete");
            await model.DiscardSelectedDownloadAsync();
            Check(!model.Downloads.Contains(failed) && !File.Exists(failed.StagingPath) && File.Exists(matched.Destination), "Delete discards only owned failed partial and removes visible record");
            var archivePath = Path.Combine(root, "protected.zip");
            File.WriteAllBytes(archivePath, Convert.FromBase64String(ArchivePasswordTests.Fixtures.Single(fixture => fixture.Name == "encrypted.zip").Bytes));
            var archiveEntry = new CatalogEntry { Id = "protected", Title = "Protected fixture", Artifacts = [new() { Id = "protected", FileName = "protected.zip", Url = "https://fixture.invalid/protected.zip" }] };
            await model.BrowserDownloads.ImportAsync(archiveEntry, DownloadMirror.ForEntry(archiveEntry).Single(), archivePath);
            var protectedJob = model.Downloads.Single(job => job.FileName == "protected.zip"); model.SelectedJob = protectedJob;
            var directoriesBefore = Directory.EnumerateDirectories(downloads).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await model.ExtractSelectedDownloadAsync();
            Check(!model.IsExtracting && protectedJob.ArchiveStatus == "Password protected" && store.Load().Downloads.Single(job => job.Id == protectedJob.Id).ArchiveStatus == "Password protected", "encrypted ZIP returns normally and persists badge");
            Check(File.Exists(protectedJob.Destination) && directoriesBefore.SetEquals(Directory.EnumerateDirectories(downloads)), "password failure preserves archive and publishes no extraction");
            try { model.ArchivePasswordsText = new string('x', 1025); throw new Exception("Invalid password list accepted"); }
            catch (ArgumentException) { Check(store.Load().ArchivePasswords.SequenceEqual(ArchiveExtraction.DefaultPasswords()), "invalid settings leave saved candidate list unchanged"); }
            model.ArchivePasswordsText = "wrong\nsyntheticarchivepassword\n";
            await model.ExtractSelectedDownloadAsync();
            Check(protectedJob.ArchiveStatus == "Password protected" && !model.IsExtracting, "all configured passwords fail without popup or leaked extraction ownership");
            model.ArchivePasswordsText = "wrong\n" + ArchivePasswordTests.Password;
            Check(store.Load().ArchivePasswords.SequenceEqual(new[] { "wrong", ArchivePasswordTests.Password }), "edited password list persists in order");
            await model.ExtractSelectedDownloadAsync();
            Check(protectedJob.ArchiveStatus == "" && model.Settings.Library.Any(item => item.Title == archiveEntry.Title && item.ContentMove is { Phase: "Completed" }), "retry existing archive with edited list installs and clears password badge");
            Check(File.Exists(protectedJob.Destination), "password retry retains original downloaded archive");
            Check(!protectedJob.IsCleared && model.Downloads.Contains(protectedJob), "retained archive stays in Downloads after verified installation");
            await model.ScanDownloadsAsync();
            Check(model.Downloads.Contains(protectedJob), "Scan preserves the retained source beside its My Library installation");
            var packageOnlyArchive = Path.Combine(root, "package-only.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(packageOnlyArchive, System.IO.Compression.ZipArchiveMode.Create))
            using (var output = zip.CreateEntry("nested.pkg").Open()) output.Write(bytes);
            var packageOnlyEntry = new CatalogEntry { Id = "package-only", Title = "Package-only extracted folder", Artifacts = [new() { Id = "package-only", FileName = "package-only.zip", Url = "https://fixture.invalid/package-only.zip" }] };
            await model.BrowserDownloads.ImportAsync(packageOnlyEntry, DownloadMirror.ForEntry(packageOnlyEntry).Single(), packageOnlyArchive);
            model.SelectedJob = model.Downloads.Single(job => job.FileName == "package-only.zip");
            await model.ExtractSelectedDownloadAsync();
            var packageFolder = model.LibraryView.Cast<LocalLibraryEntry>().Single(item => item.Title == packageOnlyEntry.Title);
            Check(packageFolder.ContentMove?.Phase == "Completed" && packageFolder.Classification.Contains("Extracted folder") &&
                File.ReadAllBytes(Path.Combine(packageFolder.ContentPath, "nested.pkg")).SequenceEqual(bytes), "package-only extraction publishes one verified folder to My Library without requiring an eboot");

            var suppliedPath = Path.Combine(root, "supplied.rar");
            File.WriteAllBytes(suppliedPath, Convert.FromBase64String(ArchivePasswordTests.Fixtures.Single(fixture => fixture.Name == "encrypted-headers.rar").Bytes));
            var suppliedEntry = new CatalogEntry { SourceId = "fixture", Id = "supplied", Title = "Supplied password fixture", Artifacts = [new() { Id = "supplied", FileName = "supplied.rar", Url = "https://fixture.invalid/supplied.rar", ArchivePassword = ArchivePasswordTests.Password }] };
            model.Entries.Add(suppliedEntry);
            await model.BrowserDownloads.ImportAsync(suppliedEntry, DownloadMirror.ForEntry(suppliedEntry).Single(), suppliedPath);
            model.ArchivePasswordsText = "wrong";
            model.SelectedJob = model.Downloads.Single(job => job.FileName == "supplied.rar");
            await model.ExtractSelectedDownloadAsync();
            Check(model.Settings.Library.Any(item => item.Title == suppliedEntry.Title && item.ContentMove is { Phase: "Completed" }), "release-supplied password resolved by recorded identities and used before configured list");
            Check(!File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")).Contains(ArchivePasswordTests.Password), "source password is not serialized to job or library settings");
            var legacyPath = Path.Combine(root, "legacy-protected.rar");
            File.WriteAllBytes(legacyPath, Convert.FromBase64String(ArchivePasswordTests.Fixtures.Single(fixture => fixture.Name == "rar4-encrypted-compressed.rar").Bytes));
            var legacyEntry = new CatalogEntry { Id = "legacy", Title = "Legacy password fixture", Artifacts = [new() { Id = "legacy", FileName = "legacy-protected.rar", Url = "https://fixture.invalid/legacy.rar" }] };
            await model.BrowserDownloads.ImportAsync(legacyEntry, DownloadMirror.ForEntry(legacyEntry).Single(), legacyPath);
            var legacyJob = model.Downloads.Single(job => job.FileName == "legacy-protected.rar"); model.SelectedJob = legacyJob;
            await model.ExtractSelectedDownloadAsync();
            Check(legacyJob.ArchiveStatus == "Password protected" && !model.IsExtracting && File.Exists(legacyJob.Destination), "legacy RAR wrong-password decoding failure returns a badge without a popup or lost source");
            model.ArchivePasswordsText = "wrong\n" + ArchivePasswordTests.Password;
            await model.ExtractSelectedDownloadAsync();
            Check(legacyJob.ArchiveStatus == "" && model.Settings.Library.Any(item => item.Title == legacyEntry.Title && item.ContentMove is { Phase: "Completed" }), "legacy RAR retries continue to the correct password and install verified bytes");
            try { KytyLauncher.BuildStartInfo("", matched.Destination); throw new Exception("Empty emulator accepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("Windows Settings"), "empty emulator path gives actionable error"); }
            Console.WriteLine("PASS scan metadata matching/ambiguity, in-place registration, repeated scan/history restore, owned failed deletion, password badge/control return and empty emulator guidance");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
