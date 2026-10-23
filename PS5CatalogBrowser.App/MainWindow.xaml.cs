using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private bool closingDirect;
    private bool refreshingSourceList;
    public MainWindow() : this(null) { }
    public MainWindow(SettingsStore? settingsStore, HttpTransfer? orbitTransfer = null, OrbitPublishedKeyClient? keyClient = null)
    {
        InitializeComponent();
        try { model = new MainViewModel(settingsStore, orbitTransfer: orbitTransfer, keyClient: keyClient); DataContext = model; }
        catch (Exception ex) { MessageBox.Show("Cannot load settings: " + ex.Message, "OrbitX"); throw; }
        model.BrowserDownloads.QueuedProviderReady += (entry, mirror, job) =>
        {
            if (windowClosed) return;
            new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads, queuedJob: job) { Owner = this }.Show();
        };
        model.StartupLoading = true;
        ConfigureSettings();
        Deactivated += (_, _) => { coverConsumed = true; CancelCoverHold(); };
        ConfigureFrontend();
        ConfigureDownloadNotifications();
        model.GameStarted += (item, backend) =>
        {
            if (OrbitSurface.IsVisible) orbitFrontend?.NotifyGameLaunching(item.Title, backend);
        };
        model.GameProcessExited += backend => orbitFrontend?.NotifyGameExited(backend);
        model.GameExited += _ =>
        {
            if (windowClosed) return;
            if (WindowState == WindowState.Minimized)
                WindowState = fullscreen ? WindowState.Maximized : model.Settings.WindowPlacement?.Maximized == true ? WindowState.Maximized : WindowState.Normal;
            Show();
            if (OrbitSurface.IsVisible) orbitFrontend?.NavigatePage("Library");
            else if (ClassicTabs.Items.Cast<TabItem>().FirstOrDefault(tab => tab.Header as string == "My Library") is { } libraryTab)
                libraryTab.IsSelected = true;
            Activate();
            if (OrbitSurface.IsVisible) orbitFrontend?.Browser.Focus();
            else LibraryList.Focus();
        };
        BrowseList.IsVisibleChanged += (_, _) => { if (BrowseList.IsVisible) model.RefreshBrowseIfNeeded(); };
        ClassicTabs.SelectionChanged += async (_, args) =>
        {
            if (args.OriginalSource == ClassicTabs && ClassicTabs.SelectedItem is TabItem { Header: "Downloads" })
                await model.RefreshDownloadsOnOpenAsync();
        };
        Loaded += async (_, _) =>
        {
            _ = model.CheckForKytyUpdateAsync();
            _ = model.CheckSharpEmuAsync();
            var frontend = ApplyInterfaceAsync();
            if (model.StartupFullscreen && model.Interface == "Orbit Store") ToggleFullscreen();
            await model.ReconcileInstalledLibraryAsync();
            var compatibility = model.RefreshCompatibilityAsync();
            var sharpEmuCompatibility = model.RefreshSharpEmuCompatibilityAsync();
            await model.TestInstallationAsync();
            if (closingDirect || windowClosed) return;
            await model.RefreshFolderActionsAsync();
            try { await model.RefreshStartupAsync(); }
            finally { model.StartupLoading = false; }
            await frontend;
            await Task.WhenAll(compatibility, sharpEmuCompatibility);
            if (model.SelectedEntry is null && model.BrowseView.Cast<object>().FirstOrDefault() is { } first)
                BrowseList.ScrollIntoView(first);
        };
        Closing += async (_, e) =>
        {
            if (model.IsUpdatingKyty || model.IsUpdatingSharpEmu) { e.Cancel = true; model.Status = "Wait for the emulator update to finish before closing."; return; }
            if (model.IsExtracting) { e.Cancel = true; model.Status = "Wait for ZIP extraction to finish before closing."; return; }
            if (model.IsMovingContent)
            {
                e.Cancel = true;
                if (closingDirect) return;
                closingDirect = true;
                try { await model.StopContentMoveAsync(); closingDirect = false; Close(); }
                catch (Exception ex) { closingDirect = false; model.Status = "Cannot finish content shutdown: " + ex.Message; }
                return;
            }
            if (model.IsDeletingLibraryEntry) { e.Cancel = true; model.Status = "Wait for library deletion to finish before closing."; return; }
            if (model.IsClearingDownloads || model.HasDirectMaintenance) { e.Cancel = true; model.Status = "Wait for incomplete download cleanup to finish before closing."; return; }
            if (model.IsInstalling) { e.Cancel = true; model.CancelAllInstallations(); model.Status = "Cancelling installations and releasing the active mount. Wait for cleanup, then close again."; return; }
            if (model.HasDirectTransfers)
            {
                e.Cancel = true;
                if (closingDirect) return;
                closingDirect = true; model.Status = "Stopping HTTP writers and saving durable checkpoints before closing...";
                try { await model.StopDirectDownloadsAsync(); closingDirect = false; Close(); }
                catch (Exception ex) { closingDirect = false; model.Status = "Cannot finish HTTP shutdown: " + ex.Message; }
                return;
            }
            if (model.BrowserDownloads.IsVerifying) { e.Cancel = true; model.Status = "Wait for downloaded file verification to finish before closing."; return; }
            if (!model.BrowserDownloads.ShuttingDown && model.BrowserDownloads.HasActive && MessageBox.Show(this, "Closing interrupts active browser downloads. Close the app?", "Downloads active",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            model.BrowserDownloads.Dispose();
            if (model.BrowserDownloads.HasActive)
            {
                e.Cancel = true;
                model.Status = "Waiting for native download termination. Keep the app open, then retry closing.";
            }
        };
        Closed += (_, _) => { windowClosed = true; SavePlacement(); orbitFrontend?.Dispose(); model.Dispose(); };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CanScanDownloads) && model.CanScanDownloads && DownloadList.IsVisible)
                _ = model.RefreshDownloadsOnOpenAsync();
            if (e.PropertyName == nameof(MainViewModel.Interface)) _ = ApplyInterfaceAsync();
            if (e.PropertyName == nameof(MainViewModel.Downloads)) DownloadList.Items.Refresh();
            if (e.PropertyName == nameof(MainViewModel.Library)) LibraryList.Items.Refresh();
            if (e.PropertyName == nameof(MainViewModel.Sources)) RefreshSourceList();
            if (e.PropertyName == nameof(MainViewModel.Installations)) InstallationList.Items.Refresh();
        };
    }
    private void CloseDetails_Click(object sender, RoutedEventArgs e)
    {
        model.SelectedEntry = null;
        BrowseList.Focus();
    }
    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await model.RefreshAllAsync();
    private async void MirrorDirect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var mirror = (DownloadMirror)((Button)sender).Tag;
        model.SelectedMirror = mirror;
        await model.DownloadAsync(model.SelectedEntry ?? throw new InvalidOperationException("Select a title."), mirror.Artifact, mirror.Url, mirror.Provider);
    });
    private void MirrorBrowser_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var mirror = (DownloadMirror)((Button)sender).Tag;
        var entry = model.SelectedEntry ?? throw new InvalidOperationException("Select a title.");
        DownloadAdmission.EnsureEnabled(entry);
        NetworkPolicy.Validate(mirror.BrowserUrl); model.SelectedMirror = mirror;
        if (model.BrowserDownloads.QueueVikingFile(entry, mirror, DownloadAdmission.Capture(entry, mirror.Artifact)))
        {
            model.Status = entry.Title + " added to Downloads and queued for the VikingFile slot. The current provider window was minimized; the queued page will open after its download completes and its window is closed.";
            return;
        }
        new BrowserDownloadWindow(entry, mirror, model.BrowserDownloads) { Owner = this }.Show();
    });
    private void AboutRepository_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        Run(() => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }));
    }
    private void MirrorExternal_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var mirror = (DownloadMirror)((Button)sender).Tag;
        OrbitDownloadPolicy.ValidateSelection(model.SelectedEntry ?? throw new InvalidOperationException("Select a title."), mirror.Artifact);
        model.SelectedMirror = mirror;
        Process.Start(new ProcessStartInfo(NetworkPolicy.Validate(mirror.BrowserUrl).AbsoluteUri) { UseShellExecute = true });
        model.Status = "External provider webpage opened. Complete or abandon the workflow in that browser; this app cannot track or stop its transfers. Import the matching completed file when ready.";
    });
    private async void MirrorImport_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var mirror = (DownloadMirror)((Button)sender).Tag;
        var entry = model.SelectedEntry ?? throw new InvalidOperationException("Select a title.");
        model.SelectedMirror = mirror;
        var picker = new OpenFileDialog { Title = "Select the completed file for this release", CheckFileExists = true, Filter = "All files|*.*" };
        if (picker.ShowDialog(this) == true) await model.BrowserDownloads.ImportAsync(entry, mirror, picker.FileName);
    });
    private async void DownloadAll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await model.DownloadAllAsync(model.SelectedEntry ?? throw new InvalidOperationException("Select a catalogue entry."));
    });
    private void CloseApp_Click(object sender, RoutedEventArgs e) => Close();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Run(model.CancelSelectedDownload);
    private async void DownloadAction_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!model.CanRunDownloadAction) return;
        if (model.SelectedJob is { } queued && model.IsQueuedForInstallation(queued.Destination)) { model.CancelQueuedInstallation(queued.Destination); return; }
        if (model.CanExtractDownload) await model.ExtractSelectedDownloadAsync();
        else if (model.CanInstallDownload) await InstallAsync(model.SelectedJob!.Destination);
        else await model.CheckExtractedDownloadAsync();
    });
    private void ClearSelectedHistory_Click(object sender, RoutedEventArgs e) => Run(model.ClearSelectedHistory);
    private async void DiscardDownload_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Discard this incomplete download? Its active transfer will be cancelled and verified owned partial files deleted. Choose No to keep the record and files.",
            "Discard incomplete download", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RunAsync(model.DiscardSelectedDownloadAsync);
    }
    private async void ClearDownloads_Click(object sender, RoutedEventArgs e) => await RunAsync(model.ClearAllDownloadsAsync);
    private async void ScanDownloads_Click(object sender, RoutedEventArgs e) => await RunAsync(model.ScanDownloadsAsync);
    private void DeleteDownload_Click(object sender, RoutedEventArgs e)
    {
        if (model.SelectedJob is { } completed && model.DownloadDeletionItem(completed) is { } item)
        { model.SelectedLibraryEntry = item; DeleteLibrary_Click(sender, e); return; }
        if (model.SelectedJob is { } job && model.CanDeleteDownload(job)) DiscardDownload_Click(sender, e);
    }
    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var path = Path.GetFullPath(model.DownloadRoot);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
    });
    private async void Pause_Click(object sender, RoutedEventArgs e) => await RunAsync(model.PauseSelectedDownloadAsync);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await RunAsync(model.ResumeSelectedDownloadAsync);
    private async void ClearBrowserData_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (model.BrowserDownloads.HasActive) throw new InvalidOperationException("Finish or cancel browser downloads before clearing site data.");
        if (Application.Current.Windows.OfType<BrowserDownloadWindow>().Any()) throw new InvalidOperationException("Close the browser windows before clearing their stored site data.");
        if (MessageBox.Show(this, "Clear embedded browser cookies, cached pages and site sessions? This signs you out of websites.", "Clear site data", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await BrowserDownloadWindow.ClearSiteDataAsync(this); model.Status = "Embedded browser site data cleared.";
    });
    private async void RetryFailed_Click(object sender, RoutedEventArgs e) => await RunAsync(RetryFailedDownloadAsync);
    private async Task RetryFailedDownloadAsync()
    {
        var job = model.SelectedJob ?? throw new InvalidOperationException("Select a failed download.");
        if (!await model.RetryDownloadAsync(job)) return;
        if (job.State == "Missing") { OpenRetryStore(job); return; }
        if (model.SavedBrowserRelease(job) is { } saved)
        {
            // Re-enter the original provider page and native DownloadStarting flow. Never replay a signed transfer URL over HTTP.
            new BrowserDownloadWindow(saved.Entry, saved.Mirror, model.BrowserDownloads, unavailable: () => OpenRetryStore(job)) { Owner = this }.Show();
            model.Status = "Retrying the saved provider page. Complete any verification; matching file offers continue automatically.";
            return;
        }
        OpenRetryStore(job);
    }
    private void CheckDownloadStatus_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var id = (sender as Button)?.Tag as string ?? model.SelectedJob?.Id;
        if (id is not null && model.BrowserDownloads.CanCheckStatus(id)) model.BrowserDownloads.CheckStatus(id);
    });
    private void OpenRetryStore(DownloadJob job)
    {
        model.Status = "A fresh download link is needed. Choose a download from this item's store page.";
        if (model.Interface == "Orbit Store" && !frontendFailed && orbitFrontend?.OpenDownloadStore(job) == true) return;
        var entry = model.Entries.SingleOrDefault(entry => entry.SourceId == job.SourceId && entry.Id == job.EntryId);
        if (entry is not null) { model.SelectedEntry = entry; model.SelectedMirror = model.MirrorChoices.FirstOrDefault(mirror => mirror.Artifact.Id == job.ArtifactId); }
        else model.Status = "The original catalogue item is unavailable. Refresh catalogues and select an available release; its files were preserved.";
        OpenWindowsPage("Browse");
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (model.SelectedJob is null) throw new InvalidOperationException("Select a download.");
        var job = model.SelectedJob;
        var retained = job.DownloadedBytes;
        if (File.Exists(job.StagingPath)) retained = Math.Max(retained, new FileInfo(job.StagingPath).Length);
        if (retained > 0 && MessageBox.Show(this, $"Restart from zero and discard {retained:N0} downloaded bytes?", "Restart download",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await model.RestartAsync(job);
    });
    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) { model.AddLocal(d.FolderName); await model.RefreshFolderActionsAsync(model.SelectedLibraryEntry); } });
    private void AddElf_Click(object sender, RoutedEventArgs e) => Run(() => { var d = new OpenFileDialog { Filter = "ELF files (*.elf)|*.elf" }; if (d.ShowDialog() == true) model.AddLocal(d.FileName); });
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var item = model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item.");
        var path = Directory.Exists(item.ContentPath) ? item.ContentPath : Path.GetDirectoryName(item.ContentPath)!;
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
    });
    private async void Extract_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await model.ExtractAsync(model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item."));
    });
    private async void Reinspect_Click(object sender, RoutedEventArgs e) => await RunAsync(() => model.ReinspectAsync(model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item.")));
    private void DeleteLibrary_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var item = model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item.");
        var paths = model.PrepareLibraryDeletion(item);
        var list = string.Join("\n", paths.Where(path => !path.Completed).Select(path => path.Path));
        if (MessageBox.Show(this, $"Delete \"{item.Title}\" and its local files?\n\nWindows will be asked to move these files and folders (including their contents) to the Recycle Bin:\n{list}\n\nDownload and installation history will be kept.", "Delete library item and files",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        try { model.DeleteLibraryEntry(item, paths); }
        finally { IsEnabled = true; }
    });
    private void ApplyArguments_Click(object sender, RoutedEventArgs e) => Run(model.ApplyArguments);
    private void ChooseShaderCacheFolder_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) model.ArgumentShaderCacheFolder = dialog.FolderName;
    });
    private async void Launch_Click(object sender, RoutedEventArgs e) => await RunAsync(() => LaunchGameAsync(false));
    private void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingSourceList) return;
        if (model?.SelectedSource is { } source)
        {
            SourceNameBox.Text = source.Name; SourceUrlBox.Text = source.Url; SourceEnabledBox.IsChecked = source.Enabled;
            SourceTypeBox.SelectedIndex = (int)source.SourceType;
            SourceAutoRefreshBox.IsChecked = source.AutoRefresh;
            SourceFallbackBox.SelectedValue = source.LocalFallbackSourceId;
            SourceFileBox.Text = source.EncryptedFilePath; SourceKeyBox.Text = source.KeyFilePath;
            SourceManagedKeyBox.SelectedValue = source.KeyFilePath;
        }
    }
    private void RefreshSourceList()
    {
        var selected = model.SelectedSource;
        refreshingSourceList = true;
        try
        {
            SourceList.Items.Refresh();
            if (selected is not null && model.Sources.Contains(selected)) SourceList.SelectedItem = selected;
        }
        finally { refreshingSourceList = false; }
    }
    private void SourceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HttpSourcePanel is null || OrbitSourcePanel is null) return;
        var live = SourceTypeBox.SelectedIndex == 2;
        HttpSourcePanel.Visibility = SourceTypeBox.SelectedIndex != 1 ? Visibility.Visible : Visibility.Collapsed;
        OrbitSourcePanel.Visibility = SourceTypeBox.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalOrbitFilePanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        LiveOrbitPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live && model?.SelectedSource?.SourceType != CatalogSourceType.OrbitLive)
        {
            SourceUrlBox.Text = LiveOrbitCatalog.OfficialUrl;
            SourceNameBox.Text = "Orbit Store";
            SourceAutoRefreshBox.IsChecked = true;
        }
    }
    private void ChooseSourceFile_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog { Filter = "Orbit encrypted catalogue (*.enc)|*.enc", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) SourceFileBox.Text = picker.FileName;
    });
    private async void ChooseSourceKey_Click(object sender, RoutedEventArgs e) => await RunSourceEditAsync(async () =>
    {
        var picker = new OpenFileDialog { Filter = "Key files (*.bin;*.hex;*.txt;*.h)|*.bin;*.hex;*.txt;*.h|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) SelectManagedKey(await model.ImportOrbitKeyAsync(picker.FileName));
    });
    private void SourceManagedKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceKeyBox is not null && SourceManagedKeyBox.SelectedItem is OrbitManagedKey key) SourceKeyBox.Text = key.FilePath;
    }
    private void SelectManagedKey(OrbitManagedKey key)
    {
        SourceManagedKeyBox.SelectedValue = key.FilePath; SourceKeyBox.Text = key.FilePath;
        PublishSourcePicker();
    }
    private async void DownloadSourceKey_Click(object sender, RoutedEventArgs e) => await RunSourceEditAsync(async () =>
    {
        var selectedPath = SourceKeyBox.Text;
        var key = await model.FetchPublishedOrbitKeyAsync();
        if (string.IsNullOrWhiteSpace(selectedPath) && string.IsNullOrWhiteSpace(SourceKeyBox.Text)) SelectManagedKey(key);
        else SourceManagedKeyBox.SelectedValue = SourceKeyBox.Text; // Downloading never changes an existing selection.
    });
    private async Task EnsureLiveKeyAsync()
    {
        if (SourceTypeBox.SelectedIndex != 2) return;
        if (string.IsNullOrWhiteSpace(SourceNameBox.Text)) throw new ArgumentException("Enter a source name.");
        LiveOrbitCatalog.ValidateUrl(SourceUrlBox.Text.Trim());
        if (SourceEnabledBox.IsChecked == true && string.IsNullOrWhiteSpace(SourceKeyBox.Text))
            SelectManagedKey(await model.FetchPublishedOrbitKeyAsync());
    }
    private async void AddSource_Click(object sender, RoutedEventArgs e) => await RunSourceEditAsync(async () =>
    {
        await EnsureLiveKeyAsync();
        if (SourceTypeBox.SelectedIndex == 2) model.AddLiveOrbitSource(SourceNameBox.Text, SourceUrlBox.Text, SourceKeyBox.Text, SourceEnabledBox.IsChecked == true, SourceAutoRefreshBox.IsChecked == true, SourceFallbackBox.SelectedValue as string ?? "");
        else if (SourceTypeBox.SelectedIndex == 1) model.AddLocalOrbitSource(SourceNameBox.Text, SourceFileBox.Text, SourceKeyBox.Text, SourceEnabledBox.IsChecked == true);
        else model.AddSource(SourceNameBox.Text, SourceUrlBox.Text);
    });
    private async void SaveSource_Click(object sender, RoutedEventArgs e) => await RunSourceEditAsync(async () =>
    {
        var source = model.SelectedSource ?? throw new InvalidOperationException("Select a source.");
        if (SourceTypeBox.SelectedIndex != (int)source.SourceType)
            throw new InvalidOperationException("Add a new source to use a different source type.");
        if (SourceNameBox.Text.Trim() == source.Name &&
            (source.SourceType == CatalogSourceType.OrbitLocal || SourceUrlBox.Text.Trim() == source.Url) &&
            (source.SourceType == CatalogSourceType.HttpJson || SourceKeyBox.Text.Trim() == source.KeyFilePath) &&
            (source.SourceType != CatalogSourceType.OrbitLocal || SourceFileBox.Text.Trim() == source.EncryptedFilePath) &&
            (source.SourceType != CatalogSourceType.OrbitLive ||
                SourceAutoRefreshBox.IsChecked == source.AutoRefresh && (SourceFallbackBox.SelectedValue as string ?? "") == source.LocalFallbackSourceId))
        {
            model.SetSourceEnabled(source, SourceEnabledBox.IsChecked == true);
            return;
        }
        await EnsureLiveKeyAsync();
        if (source.SourceType == CatalogSourceType.OrbitLive) model.UpdateLiveOrbitSource(source, SourceNameBox.Text, SourceUrlBox.Text, SourceKeyBox.Text, SourceEnabledBox.IsChecked == true, SourceAutoRefreshBox.IsChecked == true, SourceFallbackBox.SelectedValue as string ?? "");
        else if (source.SourceType == CatalogSourceType.OrbitLocal) model.UpdateLocalOrbitSource(source, SourceNameBox.Text, SourceFileBox.Text, SourceKeyBox.Text, SourceEnabledBox.IsChecked == true);
        else model.UpdateSource(source, SourceNameBox.Text, SourceUrlBox.Text, SourceEnabledBox.IsChecked == true);
    });
    private void RemoveSource_Click(object sender, RoutedEventArgs e) => Run(() => model.RemoveSource(model.SelectedSource ?? throw new InvalidOperationException("Select a source.")));
    private async void TestSource_Click(object sender, RoutedEventArgs e) => await RunAsync(() => model.RefreshAsync(model.SelectedSource ?? throw new InvalidOperationException("Select a source."), true));
    private async void RefreshSource_Click(object sender, RoutedEventArgs e) => await RunAsync(() => model.RefreshAsync(model.SelectedSource ?? throw new InvalidOperationException("Select a source.")));
    private void CancelSourceKey_Click(object sender, RoutedEventArgs e) => model.CancelPublishedOrbitKeyDownload();
    private async Task RunSourceEditAsync(Func<Task> action)
    {
        if (!SourceEditorPanel.IsEnabled) return;
        SourceEditorPanel.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { model.Status = "Orbit key download cancelled or timed out. Existing keys and source selections were kept."; }
        catch (Exception ex) { model.Status = ex.Message; if (IsLoaded) MessageBox.Show(this, ex.Message, "OrbitX"); }
        finally { SourceEditorPanel.IsEnabled = true; }
    }
    private void ChooseDownloadRoot_Click(object sender, RoutedEventArgs e) => Run(() => { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) model.DownloadRoot = d.FolderName; });
    private void ChooseEmulator_Click(object sender, RoutedEventArgs e) => Run(() => { var d = new OpenFileDialog { Filter = "KytyPS5 (kyty_emulator.exe)|kyty_emulator.exe" }; if (d.ShowDialog() == true) { model.EmulatorPath = d.FileName; model.NotifyEmulatorAvailability(); } });
    private void TestEmulator_Click(object sender, RoutedEventArgs e) => Run(() => { model.NotifyEmulatorAvailability(); MessageBox.Show(model.EmulatorAvailability, "KytyPS5 availability"); });
    private async void CheckKytyVersion_Click(object sender, RoutedEventArgs e) => await model.CheckForKytyUpdateAsync();
    private async void UpdateKyty_Click(object sender, RoutedEventArgs e) => await model.UpdateKytyAsync();
    private async void InstallLibrary_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var item = model.SelectedLibraryEntry ?? throw new InvalidOperationException("Select a library item.");
        if (model.IsQueuedForInstallation(item.ContentPath)) { model.CancelQueuedInstallation(item.ContentPath); return; }
        if (!Directory.Exists(item.ContentPath)) { await InstallAsync(item.ContentPath); return; }
        await model.RefreshFolderActionsAsync(item);
        if (!model.CanInstallItem(item)) return;
        var picker = new FolderContentSelectionDialog(item.Title, item.ContentPath, item.PendingPackagePaths, install: true, model.RetainOriginalImage) { Owner = this };
        if (picker.ShowDialog() == true) await model.InstallFolderPackagesAsync(item, picker.SelectedPaths);
    });
    private async void InstallFile_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new OpenFileDialog { Filter = "Game images and packages|*.exfat;*.pkg|exFAT images|*.exfat|PS4/PS5 packages|*.pkg", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == true) await InstallAsync(picker.FileName);
    });
    private async Task InstallAsync(string path)
    {
        ((TabControl)InstallationsTab.Parent).SelectedItem = InstallationsTab;
        await model.InstallImageAsync(path);
    }
    private void CancelInstallation_Click(object sender, RoutedEventArgs e) => model.CancelInstallation();
    private void ClearInstallations_Click(object sender, RoutedEventArgs e) => Run(model.ClearAllInstallations);
    private async void RetryCleanup_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var job = model.SelectedInstallation ?? throw new InvalidOperationException("Select an installation.");
        if (MessageBox.Show(this, "Reverify all installed files and persisted registration, then delete only the original image with its recorded identity?\n\n" + job.SourcePath,
            "Retry original-image cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await model.RetryInstallationCleanupAsync(job);
    });
    private async void RetryInstallation_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
        model.RetryInstallationAsync(model.SelectedInstallation ?? throw new IOException("Select an installation.")));
    private void DeleteInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanDeleteSelectedInstallation) return;
        model.SelectedLibraryEntry = model.InstallationItem(model.SelectedInstallation!);
        DeleteLibrary_Click(sender, e);
    }
    private void InstallationDetails_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var id = ((Button)sender).Content?.ToString() == "Installation details" ? model.SelectedLibraryEntry?.InstalledGame?.InstallationId : null;
        var job = id is null ? model.SelectedInstallation : model.Installations.FirstOrDefault(job => job.Id == id);
        if (job is null) throw new InvalidOperationException("Select an installed game or installation job.");
        MessageBox.Show(this, $"{job.Title}\nTitle ID: {job.TitleId}\nSource: {job.CatalogSource}\nOriginal: {job.SourcePath}\nDestination: {job.Destination}\n" +
            $"Files: {job.Files.Count:N0}\nSize: {job.TotalBytes:N0} bytes\nInstalled: {job.InstalledAt}\nState: {job.StateLabel}\nFull verification: {job.Verified}\nPersisted registration: {job.Registered}\n" +
            $"Mount: {job.MountDescription}\nCleanup: {job.CleanupResult}\n{job.Error}", "Installation details");
    });
    private void ChooseOSFMount_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog { Filter = "OSFMount console executable|OSFMount.com", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) model.OSFMountPath = picker.FileName;
    });
    private async void DetectOSFMount_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { model.OSFMountPath = OSFMountDependency.Detect(); await model.TestInstallationAsync(); });
    private async void TestOSFMount_Click(object sender, RoutedEventArgs e) => await RunAsync(model.TestInstallationAsync);
    private void InstallOSFMount_Click(object sender, RoutedEventArgs e) => Run(() => Process.Start(new ProcessStartInfo(OSFMountDependency.DownloadUrl) { UseShellExecute = true }));
    private void ChooseInstalledRoot_Click(object sender, RoutedEventArgs e) => Run(() => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) model.InstalledGamesRoot = picker.FolderName; });
    private void Run(Action action) { try { action(); LibraryList.Items.Refresh(); SourceList.Items.Refresh(); } catch (Exception ex) { model.Status = ex.Message; MessageBox.Show(ex.Message, "OrbitX"); } }
    private async Task RunAsync(Func<Task> action) { try { await action(); DownloadList.Items.Refresh(); LibraryList.Items.Refresh(); SourceList.Items.Refresh(); } catch (Exception ex) { model.Status = ex.Message; MessageBox.Show(ex.Message, "OrbitX"); } }
}
