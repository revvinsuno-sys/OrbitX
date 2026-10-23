using System.Text.Json;

namespace PS5CatalogBrowser.Core;

public sealed class SettingsStore(string? directory = null)
{
    public string DirectoryPath { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS5CatalogBrowser");
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public Action<AppSettings>? BeforeSave { get; set; }
    private readonly SemaphoreSlim saveGate = new(1, 1);
    internal static AppSettings Snapshot(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Options), Options)!;

    public AppSettings LoadForStartup()
    {
        var settings = Load();
        foreach (var job in settings.Downloads.Where(DownloadCompletion.Unresolved))
            Task.Run(() => DownloadCompletion.RecoverAsync(job, settings, Save)).GetAwaiter().GetResult();
        foreach (var item in settings.Library.Where(item => item.ContentMove is { Phase: "Pending" or "Normalizing" }).ToArray())
            Task.Run(() => RunnableContentMove.RecoverAsync(settings, item, Save)).GetAwaiter().GetResult();
        InstalledArchiveCleanup.ReconcileAbsentSources(settings);
        DownloadCompletion.RetireUnsupportedScanHistory(settings, Save);
        InstalledLibrary.RetireObsoleteReferences(settings, Save);
        return settings;
    }

    public IDisposable AcquireApplicationOwnership()
    {
        ImageFileSystem.EnsureNoLinks(DirectoryPath); System.IO.Directory.CreateDirectory(DirectoryPath);
        try { return new FileStream(Path.Combine(DirectoryPath, ".application-owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException ex) { throw new IOException("Another OrbitX instance owns these settings. Close it before opening another instance; library registration and image deletion require one writer.", ex); }
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return AppSettings.CreateDefault();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? throw new InvalidDataException("Settings file is empty.");
        foreach (var job in settings.Downloads)
        {
            if (DownloadCompletion.Unresolved(job)) { job.IsCleared = false; continue; }
            if (job.Method == "Direct" && job.HttpCheckpoint is not null && job.Completion is null && !job.IsCleared && !job.CleanupPending)
            {
                HttpResume.Inspect(job);
                if (settings.Downloads.Count(other => other.Id == job.Id || job.SelectionIdentity.Length > 0 && other.SelectionIdentity == job.SelectionIdentity && HttpResume.Reserves(other)) > 1)
                { job.State = "Needs attention"; job.CanResume = false; job.Error = "Competing saved download identities. Keep the files for manual attention."; }
                continue;
            }
            if (job.Completion is null && job.StagingPath.Length > 0 && job.State is not ("Downloaded" or "Imported" or "Missing" or "Changed"))
            {
                if (job.IsCleared && job.CleanupPending)
                {
                    try { if (!BrowserDownloadFiles.HasOwnedPartial(job)) continue; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* Unproven cleanup must remain visible. */ }
                }
                job.IsCleared = false;
            }
            if (job.State is "Queued" or "Downloading" or "Paused" or "Checking integrity" or "Verifying download" or "Verified" or "Cancelling")
            {
                job.State = "Interrupted";
                job.Error = job.Method == "Browser" ? "Browser transfer ended with the previous session. Open the mirror again; automatic resume is unavailable." :
                    job.Method == "External import" ? "File import was interrupted. Select the completed external file again." : "Download interrupted; retry to start again.";
            }
            if (job.Completion is null && job.StagingPath.Length > 0 && job.State is not ("Downloaded" or "Imported" or "Missing" or "Changed"))
            {
                try
                {
                    job.Error = BrowserDownloadFiles.InspectStaging(job);
                    if (job.Method == "Browser" && BrowserDownloadFiles.StagingIsMissing(job))
                    { job.State = "Interrupted"; job.CanResume = job.CanPause = false; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { job.State = "Cleanup failed"; job.Error = ex.Message; }
            }
            if (job.State is "Downloaded" or "Imported" && !File.Exists(job.Destination) && !System.IO.Directory.Exists(job.Destination)) { job.State = "Missing"; job.Error = "Downloaded file is missing."; }
            else if (job.State is "Downloaded" or "Imported" && !System.IO.Directory.Exists(job.Destination) && new FileInfo(job.Destination).Length != job.DownloadedBytes)
            { job.State = "Changed"; job.Error = "Downloaded file size changed; reinspect it."; }
        }
        foreach (var item in settings.Library)
        {
            if (item.ContentPath.Length > 0 && !File.Exists(item.ContentPath) && !System.IO.Directory.Exists(item.ContentPath))
                item.Classification = "Missing local content";
            if (item.DownloadPath.Length > 0 && !File.Exists(item.DownloadPath) && !System.IO.Directory.Exists(item.DownloadPath))
                item.Integrity = "Missing download";
            if (item.AdditionalPaths.Any(path => !File.Exists(path)))
            { item.Integrity = "Missing download part"; item.Classification = "Missing local content"; }
            if (settings.Downloads.Any(job => (job.Destination == item.DownloadPath || item.AdditionalPaths.Contains(job.Destination)) && job.State == "Changed"))
            { item.Integrity = "Needs reinspection"; item.Classification = "Integrity unverified"; }
            if (item.DeletionTargets is not null) item.Classification = "Deletion needs attention — retry Delete; files are never deleted automatically";
        }
        return settings;
    }

    public void Save(AppSettings settings)
    {
        saveGate.Wait();
        try { SaveCore(settings); }
        finally { saveGate.Release(); }
    }

    // Capture on the caller's state-owning thread, then reserve the next save before dispatching disk I/O.
    // Synchronous and asynchronous saves share one gate, so an older checkpoint cannot replace newer settings.
    public Task SaveAsync(AppSettings settings)
    {
        var snapshot = Snapshot(settings);
        var turn = saveGate.WaitAsync();
        return SaveSnapshotAsync();
        async Task SaveSnapshotAsync()
        {
            await turn.ConfigureAwait(false);
            try { await Task.Run(() => SaveCore(snapshot)).ConfigureAwait(false); }
            finally { saveGate.Release(); }
        }
    }

    private void SaveCore(AppSettings settings)
    {
        BeforeSave?.Invoke(settings);
        System.IO.Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, Options);
            stream.Flush(true);
        }
        if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
        else File.Move(temp, FilePath);
    }
}
