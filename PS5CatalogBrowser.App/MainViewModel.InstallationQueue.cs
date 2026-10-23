using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    private sealed record QueuedImageInstallation(string Path, Func<Task> Run, TaskCompletionSource Finished);
    private readonly List<QueuedImageInstallation> installationQueue = [];
    private bool drainingInstallationQueue;

    internal bool IsQueuedForInstallation(string path) => installationQueue.Any(request => ImageFileSystem.SamePath(request.Path, path));
    internal string InstallationQueueStatus(string path) => IsQueuedForInstallation(path) ? "Queued for installation" : "";
    internal string ImageInstallActionLabel(string path) => IsQueuedForInstallation(path) ? "Cancel Install" : IsInstalling ? "Queue Install" : "Install";
    public string SelectedDownloadInstallationStatus => SelectedJob is { } job ? InstallationQueueStatus(job.Destination) : "";
    public string LibraryInstallActionLabel => SelectedLibraryEntry is { } item ? ImageInstallActionLabel(item.ContentPath) : "Install";
    public string SelectedInstallationActionLabel => SelectedInstallation is { } job && IsQueuedForInstallation(job.SourcePath) ? "Cancel Install" : IsInstalling ? "Queue Install" : "Retry installation";

    public Task InstallImageAsync(string path, Func<InstallationConfirmation, Task<InstallationChoice?>>? confirm = null,
        string packagePasscode = "", LocalLibraryEntry? packageFolder = null)
    {
        // A folder batch already owns the serial installer; its members finish before queued images.
        if (packageFolder is not null && isPackageBatch)
            return RunImageInstallationAsync(path, confirm, packagePasscode, packageFolder);
        if (disposed || !CanInstallFile(path)) throw new InvalidOperationException("Choose a complete, available PKG or exFAT image that is not already installing or queued.");
        var request = new QueuedImageInstallation(Path.GetFullPath(path),
            () => RunImageInstallationAsync(path, confirm, packagePasscode, packageFolder), new(TaskCreationOptions.RunContinuationsAsynchronously));
        installationQueue.Add(request);
        NotifyInstallActions();
        return request.Finished.Task;
    }

    public void CancelQueuedInstallation(string path)
    {
        var request = installationQueue.SingleOrDefault(request => ImageFileSystem.SamePath(request.Path, path));
        if (request is null) return;
        installationQueue.Remove(request);
        request.Finished.TrySetResult();
        NotifyInstallActions();
    }

    public void CancelAllInstallations()
    {
        var pending = installationQueue.ToArray();
        installationQueue.Clear();
        foreach (var request in pending) request.Finished.TrySetResult();
        CancelInstallation();
        NotifyInstallActions();
    }

    private void StartQueuedInstallations()
    {
        if (disposed || drainingInstallationQueue || installationQueue.Count == 0 || installationCancellation is not null || isPackageBatch ||
            IsExtracting || IsMovingContent || IsDeletingLibraryEntry || IsClearingDownloads) return;
        _ = DrainInstallationQueueAsync();
    }

    private async Task DrainInstallationQueueAsync()
    {
        drainingInstallationQueue = true;
        try
        {
            while (!disposed && installationQueue.Count > 0 && installationCancellation is null && !isPackageBatch)
            {
                var request = installationQueue[0];
                installationQueue.RemoveAt(0);
                try
                {
                    // The existing installer rechecks source ownership and eligibility at execution time.
                    await request.Run();
                    request.Finished.TrySetResult();
                }
                catch (Exception ex) { request.Finished.TrySetException(ex); }
            }
        }
        finally { drainingInstallationQueue = false; NotifyInstallActions(); }
    }
}
