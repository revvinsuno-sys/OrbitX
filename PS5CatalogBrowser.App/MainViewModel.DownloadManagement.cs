using System.IO;
using System.Text.RegularExpressions;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    internal LocalLibraryEntry? DownloadDeletionItem(DownloadJob job) =>
        job.State is "Downloaded" or "Imported" or "Changed" && !WriterActive(job) && !DownloadCompletion.Unresolved(job) &&
        LibraryItemFor(job) is { } item && !IsInstalledLibraryItem(item) && CanDeleteItem(item) ? item : null;
    internal bool CanDeleteDownload(DownloadJob job) => DownloadDeletionItem(job) is not null || CanDiscardDownload(job) &&
        job.State is "Failed" or "Interrupted" or "Cancelled" or "Cleanup failed";

    // A title ID identifies a game, not an archive/release or its expected checksum.
    internal static string DownloadTitleId(DownloadJob job)
    {
        var ids = Regex.Matches(job.Title + " " + job.FileName + " " + Path.GetFileName(job.Destination),
            @"(?<![A-Za-z0-9])(?:PPSA|CUSA)-?[0-9]{5}(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Value.Replace("-", "").ToUpperInvariant()).Distinct().Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : "";
    }
    internal CatalogEntry[] DownloadCatalogEntries(DownloadJob job)
    {
        var available = Entries.Where(entry => Sources.Any(source => source.Id == entry.SourceId && source.Enabled)).ToArray();
        var original = available.Where(entry => entry.SourceId == job.SourceId && entry.Id == job.EntryId).ToArray();
        if (original.Length > 0) return original;
        var id = DownloadTitleId(job);
        if (id.Length == 0) return [];
        return available.Where(entry => (entry.TitleId + " · " + entry.BrowseTitleId).Split(" · ")
            .Any(value => value.Replace("-", "").Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    internal (string Title, string Status) DownloadMetadata(DownloadJob job)
    {
        var entries = DownloadCatalogEntries(job);
        var games = entries.GroupBy(entry => (entry.Platform, Key: entry.GameId.Length > 0 ? entry.GameId : entry.Title)).ToArray();
        var status = EmptyDownloadFolder(job) ? "Empty extracted folder" : job.ArchiveStatus;
        if (games.Length == 1)
            return (games[0].First().Title, status is "Ambiguous metadata" or "Unmatched metadata" or "Unverified metadata"
                ? $"Game matched · {DownloadTitleId(job)} · release unverified" : status);
        return (job.Title, status);
    }
    private static bool EmptyDownloadFolder(DownloadJob job)
    {
        try { return job.Method == "Folder scan" && Directory.Exists(job.Destination) && !Directory.EnumerateFileSystemEntries(job.Destination).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public bool CanDeleteSelectedDownload => SelectedJob is { } job && CanDeleteDownload(job);
    public bool CanScanDownloads => !IsClearingDownloads && !HasDirectTransfers && !BrowserDownloads.HasActive && !IsExtracting && !IsMovingContent && !IsInstalling && !IsDeletingLibraryEntry;
    public bool IsScanningDownloads { get; private set; }
    public string ScanDownloadsLabel => IsScanningDownloads ? "Scanning…" : "Scan downloads";
    public string ScanStatus { get; private set; } = "";
    private void ReportScan(string message) { ScanStatus = Status = message; Changed(nameof(ScanStatus)); }

    private bool downloadsInventoryOpened;
    internal async Task RefreshDownloadsOnOpenAsync()
    {
        if (downloadsInventoryOpened || !CanScanDownloads) return;
        downloadsInventoryOpened = true;
        try { await ScanDownloadsAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { downloadsInventoryOpened = false; Status = "Downloads inventory needs attention: " + ex.Message; }
    }

    public async Task ScanDownloadsAsync()
    {
        if (!CanScanDownloads) { Status = "Finish active file operations before scanning Downloads."; return; }
        var root = Path.GetFullPath(DownloadRoot);
        if (!Directory.Exists(root)) { Status = "Downloads folder does not exist yet."; return; }
        ImageFileSystem.EnsureNoLinks(root);
        var catalogue = Entries.Where(entry => Sources.Any(source => source.Id == entry.SourceId && source.Enabled))
            .SelectMany(entry => DownloadMirror.ForEntry(entry).Select(mirror => (Entry: entry, Mirror: mirror)))
            .Where(choice => choice.Mirror.ExpectedFileName.Length > 0)
            .GroupBy(choice => choice.Mirror.ExpectedFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.DistinctBy(choice => (choice.Entry.SourceId, choice.Entry.Id, choice.Mirror.Artifact.Id)).ToArray(), StringComparer.OrdinalIgnoreCase);
        IsClearingDownloads = IsScanningDownloads = true; Changed(nameof(IsScanningDownloads)); Changed(nameof(ScanDownloadsLabel));
        var imported = 0; var folders = 0; var unmatched = 0; var skipped = 0;
        var issues = new List<string>();
        try
        {
            DownloadCompletion.RetireUnsupportedScanHistory(Settings, store.Save); SyncDownloadLibrary();
            var inventory = await Task.Run(() => (Folders: Directory.GetDirectories(root), Files: Directory.GetFiles(root)), catalogueLifetime.Token);
            foreach (var folder in inventory.Folders)
            {
                catalogueLifetime.Token.ThrowIfCancellationRequested();
                if (Path.GetFileName(folder).StartsWith('.') || ImageFileSystem.SamePath(folder, InstalledGamesRoot) ||
                    ImageFileSystem.IsWithin(folder, InstalledGamesRoot) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                ReportScan("Checking extracted folder " + Path.GetFileName(folder));
                try
                {
                    if (Settings.Downloads.Any(job => !job.IsCleared && ImageFileSystem.SamePath(job.Destination, folder))) { skipped++; continue; }
                    var item = DownloadCompletion.RegisterScannedFolder(Settings, folder, store.Save);
                    foreach (var obsolete in Library.Where(local => !Settings.Library.Any(record => record.Id == local.Id)).ToArray()) Library.Remove(obsolete);
                    if (!Library.Contains(item)) Library.Add(item);
                    SyncDownloadLibrary();
                    var folderJob = Settings.Downloads.Single(job => job.Id == item.DownloadRegistrationId);
                    if (!Downloads.Contains(folderJob)) Downloads.Add(folderJob);
                    Save(); folders++;
                    SyncDownloadLibrary(); Changed(nameof(Library));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                { skipped++; issues.Add(Path.GetFileName(folder) + ": " + ex.Message); }
            }
            foreach (var path in inventory.Files)
            {
                catalogueLifetime.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || !DownloadCompletion.SupportedScanFile(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                var existing = Settings.Downloads.LastOrDefault(job => ImageFileSystem.SamePath(job.Destination, path));
                if (existing is not null)
                {
                    if (existing.IsCleared && !DownloadCompletion.Unresolved(existing) && !existing.CleanupPending)
                    {
                        existing.IsCleared = false;
                        try { Save(); } catch { existing.IsCleared = true; throw; }
                        if (!Downloads.Contains(existing)) Downloads.Add(existing);
                        imported++;
                    }
                    else skipped++;
                    continue;
                }
                if (SourceInUse(path) || Settings.Library.Any(item => ImageFileSystem.SamePath(item.DownloadPath, path) || ImageFileSystem.SamePath(item.ContentPath, path))) { skipped++; continue; }
                ReportScan("Scanning " + name);
                try
                {
                    ImageFileSystem.EnsureNoLinks(path);
                    // Inventory metadata only. Full source hashes are established when installation starts.
                    var file = await Task.Run(() => { var info = new FileInfo(path); info.Refresh(); return info; }, catalogueLifetime.Token);
                    var hash = "";
                    var candidates = catalogue.GetValueOrDefault(name, []).Where(choice =>
                        (choice.Mirror.Artifact.Size is not > 0 || choice.Mirror.Artifact.Size == file.Length)).ToArray();
                    var matched = candidates.Length == 1 && candidates[0].Mirror.Artifact.Sha256.Length == 0;
                    var entry = matched ? candidates[0].Entry : new CatalogEntry { Title = name };
                    var artifact = matched ? candidates[0].Mirror.Artifact : new CatalogArtifact();
                    var job = new DownloadJob { Method = "Scan", Destination = path, FileName = name, Title = entry.Title, Source = matched ? entry.SourceName : "Scanned Downloads",
                        SourceId = entry.SourceId, EntryId = entry.Id, ArtifactId = artifact.Id, Version = artifact.Version,
                        CoverUrl = entry.CoverUrl, CoverFallbackUrl = entry.CoverFallbackUrl, ExpectedSize = artifact.Size, ExpectedSha256 = artifact.Sha256,
                        ExpectedFileName = matched ? candidates[0].Mirror.ExpectedFileName : "", DownloadedBytes = file.Length, TotalBytes = file.Length, ActualSha256 = hash,
                        OrbitRelease = matched ? OrbitReleaseSelection.Capture(entry, artifact) : null,
                        SelectionIdentity = matched ? DownloadAdmission.Capture(entry, artifact) : "scan:" + Guid.NewGuid().ToString("N"),
                        ArchiveStatus = matched ? "" : candidates.Length > 1 ? "Ambiguous metadata" : candidates.Length == 1 ? "Unverified metadata" : "Unmatched metadata" };
                    DownloadCompletion.RegisterScanned(Settings, job, store.Save);
                    Downloads.Add(job); SyncDownloadLibrary(); imported++;
                    if (!matched) unmatched++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { skipped++; issues.Add(name + ": " + ex.Message); }
            }

            ReportScan($"Scan complete: {imported} files imported/restored, {folders} extracted folders identified in Downloads, {unmatched} unmatched or ambiguous, {skipped} existing, unsupported or unavailable items skipped." +
                (issues.Count == 0 ? "" : " Preserved: " + string.Join("; ", issues.Take(5))));
        }
        catch
        {
            ReportScan("Scan stopped before completion. Existing files were preserved; scan again to continue."); throw;
        }
        finally { IsClearingDownloads = IsScanningDownloads = false; Changed(nameof(IsScanningDownloads)); Changed(nameof(ScanDownloadsLabel)); Changed(nameof(Downloads)); Changed(nameof(Library)); }
        await RefreshFolderActionsAsync();
    }
}
