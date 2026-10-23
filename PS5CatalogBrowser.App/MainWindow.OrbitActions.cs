using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    // Dispatch into the same handlers as Classic. They retain the native confirmations, pickers,
    // transfer admission and writer ownership; the frontend supplies record IDs, never paths or URLs.
    private async void RunOrbitAction(OrbitFrontendAction request) => await RunAsync(async () =>
    {
        var action = request.Action;
        if (action is "download" or "browser" or "external" or "import" or "downloadAll")
        {
            var release = orbitFrontend?.ResolveRelease(request.Id) ?? throw new InvalidOperationException("That release is no longer available.");
            model.SelectedEntry = release.Entry; model.SelectedMirror = release.Mirror;
            var control = new Button { Tag = release.Mirror };
            switch (action)
            {
                case "download": MirrorDirect_Click(control, new()); break;
                case "browser": MirrorBrowser_Click(control, new()); break;
                case "external": MirrorExternal_Click(control, new()); break;
                case "import": MirrorImport_Click(control, new()); break;
                case "downloadAll": DownloadAll_Click(this, new()); break;
            }
            return;
        }
        if (action is "findDownloadGame" or "retryDownload" or "pause" or "resume" or "cancel" or "restart" or "discard" or "clearDownload" or "installDownload" or "deleteDownload")
            model.SelectedJob = model.Downloads.Single(job => job.Id == request.Id);
        if (action is "openFolder" or "reinspect" or "extract" or "installLibrary" or "deleteLibrary" or "launch" or "chooseEmulator" or "libraryDetails")
            model.SelectedLibraryEntry = model.Library.Single(item => item.Id == request.Id);
        if (action is "retryCleanup" or "installationDetails" or "retryInstallation" or "deleteInstallation" or "cancelQueuedInstallation")
            model.SelectedInstallation = model.Installations.Single(job => job.Id == request.Id && !job.IsCleared);
        if (action == "retryInstallationDownload") model.SelectedJob = model.InstallationDownload(model.Installations.Single(job => job.Id == request.Id && !job.IsCleared));
        switch (action)
        {
            case "checkStatus": model.BrowserDownloads.CheckStatus(request.Id); break;
            case "findDownloadGame": OpenRetryStore(model.SelectedJob!); break;
            case "retryDownload":
            case "retryInstallationDownload": await RetryFailedDownloadAsync(); break;
            case "settings": orbitFrontend?.NavigatePage("Settings"); break;
            case "refresh": await model.RefreshAllAsync(); break;
            case "pause": Pause_Click(this, new()); break;
            case "resume": Resume_Click(this, new()); break;
            case "cancel": Cancel_Click(this, new()); break;
            case "restart": Retry_Click(this, new()); break;
            case "discard": DiscardDownload_Click(this, new()); break;
            case "deleteDownload": DeleteDownload_Click(this, new()); break;
            case "refreshDownloads": await model.RefreshDownloadsOnOpenAsync(); break;
            case "scanDownloads": ScanDownloads_Click(this, new()); break;
            case "clearDownload": ClearSelectedHistory_Click(this, new()); break;
            case "installDownload": DownloadAction_Click(this, new()); break;
            case "clearDownloads": ClearDownloads_Click(this, new()); break;
            case "downloadsFolder": OpenDownloadsFolder_Click(this, new()); break;
            case "addFolder": AddFolder_Click(this, new()); break;
            case "addElf": AddElf_Click(this, new()); break;
            case "installFile": InstallFile_Click(this, new()); break;
            case "openFolder": OpenFolder_Click(this, new()); break;
            case "reinspect": Reinspect_Click(this, new()); break;
            case "extract": Extract_Click(this, new()); break;
            case "installLibrary": InstallLibrary_Click(this, new()); break;
            case "deleteLibrary":
                if (request.DeletionTargets is { } targets) model.DeleteLibraryEntry(model.SelectedLibraryEntry!, targets);
                else DeleteLibrary_Click(this, new());
                break;
            case "launch": await LaunchGameAsync(false); break;
            case "chooseEmulator": await LaunchGameAsync(request.Emulator is null, request.Emulator); break;
            case "libraryDetails": InstallationDetails_Click(new Button { Content = "Installation details" }, new()); break;
            case "installationDetails": InstallationDetails_Click(new Button(), new()); break;
            case "retryCleanup": RetryCleanup_Click(this, new()); break;
            case "retryInstallation": await model.RetryInstallationAsync(model.SelectedInstallation!); break;
            case "cancelQueuedInstallation": model.CancelQueuedInstallation(model.SelectedInstallation!.SourcePath); break;
            case "deleteInstallation":
                model.SelectedLibraryEntry = model.InstallationItem(model.SelectedInstallation!);
                DeleteLibrary_Click(this, new()); break;
            case "cancelInstallation": CancelInstallation_Click(this, new()); break;
            case "cancelContentMove": model.CancelContentMove(); break;
            case "clearInstallations": ClearInstallations_Click(this, new()); break;
            default: throw new InvalidOperationException("Unknown Windows action.");
        }
    });
}
