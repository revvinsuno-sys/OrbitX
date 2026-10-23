using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    private readonly HashSet<string> retryingDownloads = [];
    private readonly HashSet<string> downloadLinkFailures = [];
    private readonly HashSet<string> storeRetryDownloads = [];
    private bool CanReopenInterruptedBrowserJob(DownloadJob job) => job.Method == "Browser" && job.State == "Interrupted" &&
        !restartingJobs.Contains(job.Id) && !WriterActive(job) && !DownloadCompletion.Unresolved(job) && !IsClearingDownloads &&
        !IsMovingContent && SavedBrowserRelease(job) is not null;
    private bool FailedDownload(DownloadJob job) => job.State is "Failed" or "Interrupted" or "Integrity failed" or "Needs attention" or "Cleanup failed" or "Missing" ||
        job.ArchiveStatus == "Extraction failed" || Settings.Library.Any(item => item.Extraction == "Extraction failed" && item.DownloadArtifacts.Any(a => a.JobId == job.Id)) ||
        Installations.Any(installation => FailedInstallation(installation) && ImageFileSystem.SamePath(installation.SourcePath, job.Destination));
    private static bool FailedInstallation(InstallationJob job) => !job.IsCleared && !job.Registered && !job.Verified && job.State == InstallationState.Failed;
    internal DownloadJob? InstallationDownload(InstallationJob installation) => FailedInstallation(installation)
        ? Downloads.SingleOrDefault(job => ImageFileSystem.SamePath(job.Destination, installation.SourcePath)) : null;
    internal bool CanRetryDownload(DownloadJob job)
    {
        var reopenBrowser = CanReopenInterruptedBrowserJob(job);
        return Settings.Downloads.Contains(job) && !job.IsCleared && !retryingDownloads.Contains(job.Id) &&
            !IsClearingDownloads && (reopenBrowser || !IsExtracting && !IsInstalling && !IsMovingContent) && !DownloadCompletion.Unresolved(job) && FailedDownload(job) &&
            !directTransfers.ContainsKey(job.Id) && !restartingJobs.Contains(job.Id) && (!WriterActive(job) || BrowserDownloads.IsActive(job) && !BrowserDownloads.IsVerifying);
    }
    public bool CanRetrySelectedDownload => SelectedJob is { } job && CanRetryDownload(job);

    internal (CatalogEntry Entry, DownloadMirror Mirror)? SavedBrowserRelease(DownloadJob job)
    {
        if (job.Method != "Browser" || job.Url.Length == 0 || job.SelectionIdentity.Length == 0 || Sources.Any(source => source.Id == job.SourceId && !source.Enabled)) return null;
        try { NetworkPolicy.Validate(job.Url); } catch (InvalidOperationException) { return null; }
        CatalogEntry? entry;
        if (job.OrbitRelease is { } saved)
        {
            if (saved.ReleaseId != job.ArtifactId || saved.Size is not > 0 || saved.FileName.Length == 0 || job.EntryId.Length == 0 || job.SourceId.Length == 0) return null;
            var artifact = new CatalogArtifact { Id = job.ArtifactId, FileName = saved.FileName, Url = saved.Url, BrowserUrl = saved.BrowserUrl,
                ProviderSourceId = saved.ProviderSourceId, Provider = saved.Provider, Version = saved.Version, Region = saved.Region,
                Format = saved.ContentFormat, Compression = saved.ContainerFormat, Size = saved.Size, Sha256 = saved.Sha256 };
            entry = new CatalogEntry { Id = job.EntryId, SourceId = job.SourceId, SourceName = job.Source, Title = job.Title,
                CatalogFormat = "Orbit", GameId = saved.GameId, Platform = saved.Platform, Artifacts = [artifact] };
        }
        else entry = Entries.SingleOrDefault(entry => entry.Id == job.EntryId && entry.SourceId == job.SourceId);
        var selected = entry?.Artifacts.SingleOrDefault(artifact => artifact.Id == job.ArtifactId);
        if (entry is null || selected is null || DownloadAdmission.Capture(entry, selected) != job.SelectionIdentity) return null;
        var mirror = DownloadMirror.ForEntry(entry).FirstOrDefault(mirror => mirror.Artifact == selected && mirror.BrowserUrl == job.Url);
        return mirror is null ? null : (entry, mirror);
    }

    // True requests a fresh provider/store selection: native session capabilities are never replayed over HTTP.
    internal async Task<bool> RetryDownloadAsync(DownloadJob job)
    {
        EnsureDownloadsAvailable();
        if (!CanRetryDownload(job)) throw new IOException("This download is active, being recovered, or is not a failed item.");
        retryingDownloads.Add(job.Id); Changed(nameof(CanRetrySelectedDownload));
        try
        {
            if (job.Method == "Direct" && !CompletedHistory(job))
            {
                if (HttpResume.Eligible(job))
                {
                    if (!admission.Owns(job)) admission.Admit([job.SelectionIdentity], () => [job], _ => Save(), _ => { });
                    await RunDownloadAsync(job, true);
                    if (CompletedHistory(job) || job.State is "Paused" or "Cancelled") return false;
                }
                // Retry explicitly authorises replacement of this job's owned partial.
                await RestartAsync(job);
                if (!FailedDownload(job) || !downloadLinkFailures.Contains(job.Id)) return false;
                await DiscardDownloadAsync(job, clearHistory: false);
                return !job.CleanupPending && job.State != "Cleanup failed" && MarkStoreRetry(job);
            }
            if (BrowserDownloads.IsActive(job) && job.CanResume && await BrowserDownloads.TryResumeToSettlementAsync(job)) return false;
            if (CompletedHistory(job)) return MarkStoreRetry(job); // Failed extraction/install: retain the completed source archive/package.
            await DiscardDownloadAsync(job, clearHistory: false);
            if (job.CleanupPending || BrowserDownloads.IsActive(job) || job.State == "Cleanup failed") return false;
            return MarkStoreRetry(job);
        }
        finally { retryingDownloads.Remove(job.Id); Changed(nameof(CanRetrySelectedDownload)); Changed(nameof(Downloads)); }
    }

    private bool MarkStoreRetry(DownloadJob job)
    {
        foreach (var failed in Downloads.Where(failed => SameRetryGame(failed, job) && FailedDownload(failed) && !WriterActive(failed) && !HttpResume.Reserves(failed)))
            storeRetryDownloads.Add(failed.Id);
        return true;
    }
    private static bool SameRetryGame(DownloadJob old, DownloadJob replacement) => old.SourceId.Length > 0 && old.EntryId.Length > 0 &&
        old.SourceId == replacement.SourceId && old.EntryId == replacement.EntryId && old.OrbitRelease?.Platform == replacement.OrbitRelease?.Platform;

    private void RetireFailedInstallations(DownloadJob replaced)
    {
        foreach (var installation in Installations.Where(installation => FailedInstallation(installation) && ImageFileSystem.SamePath(installation.SourcePath, replaced.Destination)))
        {
            installation.IsCleared = true;
            try { installationJournal.Save(installation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { installation.IsCleared = false; }
        }
        Changed(nameof(Installations));
    }

    private static bool SameRetryRelease(DownloadJob old, DownloadJob replacement) => old.SourceId.Length > 0 && old.EntryId.Length > 0 && old.ArtifactId.Length > 0 &&
        old.SourceId == replacement.SourceId && old.EntryId == replacement.EntryId && old.ArtifactId == replacement.ArtifactId && old.Version == replacement.Version &&
        old.OrbitRelease?.Platform == replacement.OrbitRelease?.Platform && old.OrbitRelease?.Region == replacement.OrbitRelease?.Region;
}
