using System.ComponentModel;
using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    public bool CanRetrySelectedInstallation => SelectedInstallation is { } job && (IsQueuedForInstallation(job.SourcePath) || CanRetryInstallation(job));
    public bool CanDeleteSelectedInstallation => SelectedInstallation is { } job && CanDeleteInstallation(job);
    public ICollectionView DownloadView { get; private set; } = null!;
    internal IEnumerable<InstallationJob> VisibleInstallations => Installations.Where(job => !job.IsCleared)
        .GroupBy(job => job.SourcePath, StringComparer.OrdinalIgnoreCase).Select(group => group.Last())
        .Where(job => !InstallationFinished(job) || job.CanRetryCleanup);
    private static bool InstallationFinished(InstallationJob job) => job.State is InstallationState.Completed or InstallationState.CompletedOriginalRetained;
    internal bool ShowDownload(DownloadJob download) => !download.IsCleared && !download.State.StartsWith("Installed", StringComparison.OrdinalIgnoreCase) &&
        !VisibleInstallations.Any(job => ImageFileSystem.SamePath(job.SourcePath, download.Destination));
    internal LocalLibraryEntry? InstallationItem(InstallationJob job) => Library.FirstOrDefault(item =>
        ImageFileSystem.SamePath(item.ContentPath, job.SourcePath) || ImageFileSystem.SamePath(item.DownloadPath, job.SourcePath) ||
        item.DownloadArtifacts.Any(artifact => ImageFileSystem.SamePath(artifact.Destination, job.SourcePath)));
    internal bool CanRetryInstallation(InstallationJob job) => !InstallationFinished(job) && job.State is (InstallationState.Failed or InstallationState.Cancelled) &&
        !job.Verified && !job.Registered && (!IsInstalling || CanInstallFile(job.SourcePath)) && !IsQueuedForInstallation(job.SourcePath) && !IsExtracting && !IsMovingContent && !IsClearingDownloads &&
        (File.Exists(job.SourcePath) || Directory.Exists(job.SourcePath) || InstallationItem(job)?.ContentMove is { Phase: "Completed" });
    internal bool CanDeleteInstallation(InstallationJob job) => !job.Registered && !job.Verified && CanRetryInstallation(job) && InstallationItem(job) is { } item && !IsInstalledLibraryItem(item) && CanDeleteItem(item);
    internal async Task RetryInstallationAsync(InstallationJob job)
    {
        if (IsQueuedForInstallation(job.SourcePath)) { CancelQueuedInstallation(job.SourcePath); return; }
        if (!CanRetryInstallation(job)) throw new IOException("This installation cannot be retried while another operation owns its files.");
        if (InstallationItem(job) is { Extraction: "Extracted" } item) await CheckExtractedDownloadAsync(item);
        else if (IsExtractableArchive(job.SourcePath)) await ExtractAsync(InstallationItem(job) ?? throw new IOException("The archive registration is missing."));
        else await InstallImageAsync(job.SourcePath);
    }
    private InstallationJob BeginArchiveInstallation(LocalLibraryEntry item)
    {
        var source = item.DownloadArtifacts.FirstOrDefault()?.Destination ?? item.ContentPath;
        var job = Installations.LastOrDefault(job => !job.IsCleared && !job.Registered && ImageFileSystem.SamePath(job.SourcePath, source));
        if (job is null)
        {
            job = new InstallationJob { SourcePath = source, Title = item.Title, CatalogSource = item.Source, InstallationRoot = InstalledGamesRoot };
            Installations.Add(job);
        }
        job.State = InstallationState.Extracting; job.Error = ""; job.DeleteOriginal = !RetainOriginalImage;
        SelectedInstallation = job; installationJournal.Save(job); Changed(nameof(Installations)); Changed(nameof(Downloads));
        return job;
    }
    private void FinishArchiveInstallation(InstallationJob job, LocalLibraryEntry item, Exception? error = null)
    {
        job.Error = error?.Message ?? "";
        job.State = error is null && item.ContentMove is { Phase: "Completed" } ? InstallationState.Completed : InstallationState.Failed;
        if (InstallationFinished(job))
        {
            job.Registered = job.Verified = true; job.Destination = item.ContentPath;
            job.Files = item.ContentMove!.Files; job.Directories = item.ContentMove.Directories;
            job.SourceDeleted = !File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath);
            job.CleanupResult = job.SourceDeleted ? "Original removed after verified installation" : "Original retained by choice";
            // Archive cleanup uses its download evidence and content-move manifest, not the image mounter.
            job.DeleteOriginal = false;
        }
        installationJournal.Save(job); Changed(nameof(Installations)); Changed(nameof(Downloads));
    }
}
