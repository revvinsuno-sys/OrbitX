using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static Assembly ProductAssembly => typeof(MainViewModel).Assembly;
    public string AboutBuildVersion => ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? ProductAssembly.GetName().Version?.ToString() ?? "Unknown";
    public string AboutLastUpdated => File.GetLastWriteTime(ProductAssembly.Location).ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.CurrentCulture);
    private readonly SettingsStore store;
    private readonly IDisposable settingsOwnership;
    private bool disposed;
    private bool browseRefreshPending;
    private bool stoppingDirect;
    private readonly HashSet<string> pauseRequests = [];
    private readonly HashSet<string> restartingJobs = [];
    private readonly System.Windows.Threading.Dispatcher ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly HttpTransfer http;
    private readonly HttpTransfer orbitHttp;
    private readonly TimeProvider catalogueClock;
    private readonly OrbitPublishedKeyClient publishedKeys;
    public OrbitKeyStore KeyStore { get; }
    public ObservableCollection<OrbitManagedKey> ManagedOrbitKeys { get; } = [];
    public bool IsFetchingOrbitKey { get; private set; }
    private CancellationTokenSource? keyDownloadCancellation;
    private readonly CancellationTokenSource catalogueLifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer orbitRefreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly Dictionary<string, OrbitCatalogResult> orbitSnapshots = [];
    private readonly Dictionary<string, DateTimeOffset> automaticAttempts = [];
    private readonly Dictionary<string, long> sourceConfigurationVersions = [];
    private readonly CatalogCache cache = new();
    private readonly HashSet<string> refreshingSources = [];
    private readonly KytyLauncher launcher = new();
    private readonly DownloadAdmission admission = new();
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private readonly HashSet<string> reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, TaskCompletionSource Finished)> directTransfers = [];
    private readonly InstallationJournal installationJournal;
    private CancellationTokenSource? installationCancellation;
    private string installationAvailability = "Test OSFMount availability in Settings", installationProgress = "No installation active";
    private bool installationAvailable;
    private double installationPercentage;
    private InstallationJob? selectedInstallation;
    private InstallationJob? activeInstallation;
    internal InstallationJob? ActiveInstallation => activeInstallation;
    internal LocalLibraryEntry? ActiveExtraction { get; private set; }
    internal LocalLibraryEntry? ActiveContentMove { get; private set; }
    internal string ContentMoveProgressText { get; private set; } = "";
    internal double? ContentMovePercentage { get; private set; }
    private CancellationTokenSource? contentMoveCancellation;
    private bool contentMoveStopRequested;

    public void CancelContentMove()
    {
        contentMoveStopRequested = true;
        contentMoveCancellation?.Cancel();
        Status = "Stopping verification; any folder move already committed will finish safely.";
    }
    public async Task StopContentMoveAsync()
    {
        CancelContentMove();
        while (IsMovingContent) await Task.Delay(50);
    }
    private void BeginContentMove()
    {
        contentMoveStopRequested = false;
        contentMoveCancellation = CancellationTokenSource.CreateLinkedTokenSource(catalogueLifetime.Token);
        IsMovingContent = true;
    }
    private void EndContentMove()
    {
        ActiveContentMove = null; IsMovingContent = false;
        contentMoveCancellation?.Dispose(); contentMoveCancellation = null;
    }
    private IProgress<InstallationProgress> ContentProgress(LocalLibraryEntry item)
    {
        ActiveContentMove = item; ContentMovePercentage = null;
        ContentMoveProgressText = "Scanning " + item.Title;
        return new Progress<InstallationProgress>(update =>
        {
            if (ActiveContentMove != item) return;
            ContentMovePercentage = update.TotalBytes > 0 ? update.Percentage : null;
            ContentMoveProgressText = $"{item.Title}\n{update.File}\n{update.Bytes / 1073741824.0:F2} / {update.TotalBytes / 1073741824.0:F2} GiB · {update.BytesPerSecond / 1048576.0:F1} MiB/s · Elapsed {update.Elapsed:hh\\:mm\\:ss}";
            ExtractionFeedback = ContentMoveProgressText; ExtractionPercentage = ContentMovePercentage ?? 0;
            Status = ContentMoveProgressText;
        });
    }
    private bool isClearingDownloads;
    private bool isDeletingLibraryEntry;
    private string search = "", sourceFilter = "All", kindFilter = "All", platformFilter = "All", sort = "Title", status = "Ready";
    private CatalogEntry? selectedEntry;
    private CatalogArtifact? selectedArtifact;
    private string selectedDownloadUrl = "";
    private DownloadJob? selectedJob;
    private LocalLibraryEntry? selectedLibraryEntry;
    private CatalogSource? selectedSource;
    private DownloadMirror? selectedMirror;
    public AppSettings Settings { get; }
    internal DownloadBandwidthCoordinator Bandwidth { get; }
    internal event Action<DownloadJob>? DownloadAdded;
    public string[] InterfaceChoices { get; } = ["Classic Windows", "Orbit Store"];
    public string Interface
    {
        get => InterfaceChoices.Contains(Settings.Interface) ? Settings.Interface : "Classic Windows";
        set
        {
            if (!InterfaceChoices.Contains(value)) throw new ArgumentException("Select a supported interface.");
            var previous = Settings.Interface; Settings.Interface = value;
            try { Save(); } catch { Settings.Interface = previous; throw; }
            Changed();
        }
    }
    internal string FrontendProfilePath => Path.Combine(store.DirectoryPath, "orbit-frontend-profile");
    internal void SaveWindowPlacement(WindowPlacement placement) { Settings.WindowPlacement = placement; Save(); }
    internal bool StartupLoading { get; set; }
    public bool StartupFullscreen
    {
        get => Settings.StartupFullscreen;
        set
        {
            var previous = Settings.StartupFullscreen;
            Settings.StartupFullscreen = value;
            try { Save(); } catch { Settings.StartupFullscreen = previous; throw; }
            Changed();
        }
    }
    internal void SetOrbitFavorite(string gameId, bool favorite)
    {
        var previous = Settings.OrbitFavorites.ToList();
        if (favorite) { if (!Settings.OrbitFavorites.Contains(gameId)) Settings.OrbitFavorites.Add(gameId); }
        else Settings.OrbitFavorites.Remove(gameId);
        try { Save(); } catch { Settings.OrbitFavorites = previous; throw; }
    }
    public ObservableCollection<CatalogEntry> Entries { get; } = [];
    public ObservableCollection<DownloadJob> Downloads { get; }
    public ObservableCollection<LocalLibraryEntry> Library { get; }
    public ICollectionView LibraryView { get; }
    public bool IsMovingContent { get; private set; }
    public ObservableCollection<InstallationJob> Installations { get; }
    public ICollectionView InstallationView { get; }
    public ObservableCollection<CatalogSource> Sources { get; }
    public ObservableCollection<CatalogSource> LocalOrbitFallbackChoices { get; } = [new CatalogSource { Id = "", Name = "None" }];
    public ICollectionView BrowseView { get; }
    public ObservableCollection<string> SourceChoices { get; } = ["All"];
    public ObservableCollection<DownloadMirror> MirrorChoices { get; } = [];
    public ICollectionView MirrorView { get; }
    public BrowserDownloadManager BrowserDownloads { get; }
    public string[] KindChoices { get; } = ["All", "game", "app", "payload", "other"];
    public string[] PlatformChoices { get; } = ["All", "PS5", "PS4", "other"];
    public string[] SortChoices { get; } = ["Title", "Source", "Version", "Size"];
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(SettingsStore? settingsStore = null, HttpTransfer? transfer = null, HttpTransfer? orbitTransfer = null, TimeProvider? catalogueClock = null, OrbitPublishedKeyClient? keyClient = null)
    {
        this.catalogueClock = catalogueClock ?? TimeProvider.System;
        store = settingsStore ?? new SettingsStore(); http = transfer ?? new HttpTransfer();
        try { settingsOwnership = store.AcquireApplicationOwnership(); } catch { http.Dispose(); throw; }
        try { Settings = store.LoadForStartup(); admission.RestoreCompletions(Settings.Downloads); } catch { settingsOwnership.Dispose(); http.Dispose(); throw; }
        LoadSharpEmuInstallation();
        Settings.IgdbClientId ??= "";
        Settings.IgdbClientSecretProtected ??= "";
        if (Settings.IgdbCredentialsSeededFromShared)
        {
            Settings.IgdbClientId = "";
            Settings.IgdbClientSecretProtected = "";
            Settings.IgdbCredentialsSeededFromShared = false;
            Save();
        }
        if (!double.IsFinite(Settings.DownloadBandwidthMBps) || Settings.DownloadBandwidthMBps < 0 || Settings.DownloadBandwidthMBps > 100000) Settings.DownloadBandwidthMBps = 100;
        Settings.ArchivePasswords ??= ArchiveExtraction.DefaultPasswords();
        RefreshKytyVersion();
        Bandwidth = new(Settings.DownloadBandwidthMBps);
        http.DownloadLimiter = Bandwidth.CreateLimiter();
        installationJournal = new(Path.Combine(store.DirectoryPath, "installations"));
        try { Installations = new(installationJournal.Load()); } catch { settingsOwnership.Dispose(); http.Dispose(); throw; }
        InstallationView = CollectionViewSource.GetDefaultView(Installations);
        InstallationView.Filter = item => item is InstallationJob job && VisibleInstallations.Contains(job);
        SelectedInstallation = Installations.LastOrDefault(job => !job.IsCleared);
        if (Settings.OSFMountPath.Length == 0) Settings.OSFMountPath = OSFMountDependency.Detect();
        Downloads = new(Settings.Downloads.Where(job => !job.IsCleared));
        DownloadView = new ListCollectionView(Downloads);
        DownloadView.Filter = item => item is DownloadJob job && ShowDownload(job);
        Library = new(Settings.Library);
        ReadInstalledMembership();
        LibraryView = new ListCollectionView(Library);
        LibraryView.Filter = IsInstalledLibraryItem;
        foreach (var item in Library.Where(item => item.InstalledGame is not null))
            if (Installations.FirstOrDefault(job => job.Id == item.InstalledGame!.InstallationId) is not { State: InstallationState.Completed or InstallationState.CompletedOriginalRetained })
            {
                item.Classification = "Needs attention — interrupted installation";
                item.InstalledGame!.LaunchEligible = false;
            }
        Sources = new(Settings.Sources);
        foreach (var local in Sources.Where(s => s.SourceType == CatalogSourceType.OrbitLocal)) LocalOrbitFallbackChoices.Add(local);
        orbitHttp = orbitTransfer ?? new HttpTransfer(new System.Net.Http.HttpClientHandler
            { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
        publishedKeys = keyClient ?? new OrbitPublishedKeyClient();
        KeyStore = new OrbitKeyStore(Path.Combine(store.DirectoryPath, "keys"));
        ReloadManagedKeys();
        MirrorView = CollectionViewSource.GetDefaultView(MirrorChoices);
        MirrorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DownloadMirror.GroupLabel)));
        BrowserDownloads = new BrowserDownloadManager(() => DownloadRoot, () => Downloads,
            job => PersistDownloads([job]),
            (job, item) =>
            {
                if (item is not null) { SyncDownloadLibrary(); Changed(nameof(Library)); }
                Changed(nameof(Downloads));
                if (SelectedJob == job) { Changed(nameof(SelectedJob)); Changed(nameof(CanRetrySelectedDownload)); }
                if (job.State is "Downloaded" or "Imported")
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(NotifyInstallActions));
                Status = $"{job.Title}: {job.State}" + (job.Error.Length > 0 ? "; " + job.Error : "");
                try { Save(); } catch { Status = "Download record could not be saved. Check available disk space and permissions."; throw; }
            }, Settings, store.Save, admission) { Bandwidth = Bandwidth };
        BrowseView = CollectionViewSource.GetDefaultView(Entries);
        BrowseView.Filter = Filter;
        ApplySort();
        RefreshSourceChoices();
        orbitRefreshTimer.Tick += OrbitRefreshTimer_Tick;
        orbitRefreshTimer.Start();
        WatchInstalledFolder();
        installedInventoryTimer.Tick += InstalledInventoryTimer_Tick;
        installedInventoryTimer.Start();
    }
    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name == nameof(Library)) { ReadInstalledMembership(); LibraryView.Refresh(); PropertyChanged?.Invoke(this, new(nameof(LibraryView))); }
        if (name is nameof(Downloads) or nameof(Installations)) { DownloadView?.Refresh(); InstallationView?.Refresh(); }
        if (name is nameof(Downloads) or nameof(Library)) NotifyInstallActions();
    }
    private bool IsInstalledLibraryItem(object value)
    {
        if (value is not LocalLibraryEntry item || !Path.IsPathFullyQualified(InstalledGamesRoot) || !Path.IsPathFullyQualified(item.ContentPath) ||
            !ImageFileSystem.IsWithin(InstalledGamesRoot, item.ContentPath) || item.ContentMove is { Phase: not "Completed" } ||
            !installedInventoryPaths.Any(path => InstalledLibraryInventory.Covers(path, item.ContentPath))) return false;
        try { ImageFileSystem.EnsureNoLinks(item.ContentPath); return File.Exists(item.ContentPath) || Directory.Exists(item.ContentPath); }
        catch (IOException) { return false; }
    }
    private void ReloadManagedKeys()
    {
        ManagedOrbitKeys.Clear();
        try { foreach (var key in KeyStore.Load()) ManagedOrbitKeys.Add(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status = "Managed Orbit keys are unavailable. Check the keys folder permissions or choose a key file manually."; }
    }
    public async Task<OrbitManagedKey> FetchPublishedOrbitKeyAsync()
    {
        if (IsFetchingOrbitKey) throw new InvalidOperationException("Wait for the current Orbit key download.");
        IsFetchingOrbitKey = true; Changed(nameof(IsFetchingOrbitKey));
        keyDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(catalogueLifetime.Token);
        try
        {
            var key = await publishedKeys.DownloadAsync(KeyStore, new Progress<string>(message => { if (!disposed) Status = message; }), keyDownloadCancellation.Token);
            if (!disposed) { ReloadManagedKeys(); Status = key.Description + " is available. Existing source selections are unchanged."; }
            return key;
        }
        finally { keyDownloadCancellation.Dispose(); keyDownloadCancellation = null; IsFetchingOrbitKey = false; Changed(nameof(IsFetchingOrbitKey)); }
    }
    public void CancelPublishedOrbitKeyDownload() => keyDownloadCancellation?.Cancel();
    public async Task<OrbitManagedKey> ImportOrbitKeyAsync(string filePath)
    {
        if (IsFetchingOrbitKey) throw new InvalidOperationException("Wait for the current Orbit key download.");
        var key = await KeyStore.ImportAsync(filePath, catalogueLifetime.Token);
        if (!disposed) { ReloadManagedKeys(); Status = key.Description + " imported."; }
        return key;
    }
    public string Search { get => search; set { search = value; Changed(); BrowseView.Refresh(); } }
    public string SourceFilter { get => sourceFilter; set { var next = value ?? "All"; if (sourceFilter == next) return; sourceFilter = next; Changed(); BrowseView.Refresh(); } }
    public string KindFilter { get => kindFilter; set { kindFilter = value ?? "All"; Changed(); BrowseView.Refresh(); } }
    public string PlatformFilter { get => platformFilter; set { platformFilter = value ?? "All"; Changed(); BrowseView.Refresh(); } }
    public string Sort { get => sort; set { sort = value ?? "Title"; Changed(); ApplySort(); } }
    public string Status { get => status; set { status = value; Changed(); } }
    public bool IsUpdatingKyty { get; private set; }
    public bool IsClearingDownloads { get => isClearingDownloads; private set { isClearingDownloads = value; Changed(); Changed(nameof(CanScanDownloads)); NotifyInstallActions(); } }
    public CatalogEntry? SelectedEntry
    {
        get => selectedEntry;
        set
        {
            selectedEntry = value; MirrorChoices.Clear();
            if (value is not null) foreach (var mirror in DownloadMirror.ForEntry(value)) MirrorChoices.Add(mirror);
            SelectedMirror = MirrorChoices.FirstOrDefault(); Changed(); Changed(nameof(OrbitBrowseSummary));
        }
    }
    public DownloadMirror? SelectedMirror
    {
        get => selectedMirror;
        set { selectedMirror = value; SelectedArtifact = value?.Artifact; SelectedDownloadUrl = value?.Url ?? ""; Changed(); }
    }
    public CatalogArtifact? SelectedArtifact
    {
        get => selectedArtifact;
        set
        {
            selectedArtifact = value;
            SelectedDownloadUrl = value?.Url ?? "";
            Changed();
        }
    }
    public string SelectedDownloadUrl { get => selectedDownloadUrl; set { selectedDownloadUrl = value ?? ""; Changed(); } }
    public DownloadJob? SelectedJob { get => selectedJob; set { selectedJob = value; if (!IsExtracting) ExtractionFeedback = ""; Changed(); Changed(nameof(CanExtractDownload)); Changed(nameof(CanRetrySelectedDownload)); NotifyInstallActions(); } }
    public LocalLibraryEntry? SelectedLibraryEntry { get => selectedLibraryEntry; set { selectedLibraryEntry = value; Changed(); Changed(nameof(CanViewSelectedLaunchLog)); NotifyInstallActions(); } }
    public CatalogSource? SelectedSource { get => selectedSource; set { selectedSource = value; Changed(); } }
    public string OrbitBrowseSummary => SelectedEntry is { CatalogFormat: "Orbit" } entry && Sources.FirstOrDefault(s => s.Id == entry.SourceId) is {} source
        ? $"Orbit Store · {source.Format} · Updated {source.LastSuccessfulRefresh:g} · {source.GameCount} games · {source.ReleaseCount} release options" : "";
    public string DownloadRoot { get => Settings.DownloadRoot; set { Settings.DownloadRoot = value; Changed(); Save(); } }
    public double DownloadBandwidthMBps
    {
        get => Settings.DownloadBandwidthMBps;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 100000) throw new ArgumentException("Enter a bandwidth limit from 0 to 100000 MB/s; 0 means unlimited.");
            var previous = Settings.DownloadBandwidthMBps; Settings.DownloadBandwidthMBps = value;
            try { Save(); } catch { Settings.DownloadBandwidthMBps = previous; throw; }
            Bandwidth.SetLimit(value); Changed();
        }
    }
    public string ArchivePasswordsText
    {
        get => string.Join(Environment.NewLine, Settings.ArchivePasswords);
        set
        {
            var passwords = value.Replace("\r", "").Split('\n').Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (passwords.Count > 64 || passwords.Any(password => password.Length > 1024 || password.Contains('\0')))
                throw new ArgumentException("Use up to 64 passwords, at most 1024 characters each, one per line.");
            var previous = Settings.ArchivePasswords; Settings.ArchivePasswords = passwords;
            try { Save(); } catch { Settings.ArchivePasswords = previous; throw; }
            Changed();
        }
    }
    internal void SaveIgdbCredentials(string clientId, string? clientSecret)
    {
        clientId = clientId.Trim();
        if (clientId.Length > 256 || clientId.Contains('\0')) throw new ArgumentException("Enter an IGDB client ID up to 256 characters.");
        if (clientSecret is { Length: > 1024 } || clientSecret?.Contains('\0') == true) throw new ArgumentException("The IGDB client secret must be at most 1024 characters.");
        var previousId = Settings.IgdbClientId;
        var previousSecret = Settings.IgdbClientSecretProtected;
        Settings.IgdbClientId = clientId;
        if (!string.IsNullOrWhiteSpace(clientSecret)) Settings.IgdbClientSecretProtected = IgdbCredentialProtection.Protect(clientSecret);
        Settings.IgdbCredentialsSeededFromShared = false;
        try { Save(); }
        catch { Settings.IgdbClientId = previousId; Settings.IgdbClientSecretProtected = previousSecret; throw; }
    }
    public string EmulatorPath { get => Settings.EmulatorPath; set { if (IsUpdatingKyty) throw new InvalidOperationException("Wait for the KytyPS5 update to finish before changing its executable."); Settings.EmulatorPath = value; RefreshKytyVersion(); Changed(); Save(); } }
    public string EmulatorAvailability => File.Exists(Settings.EmulatorPath) && Path.GetFileName(Settings.EmulatorPath).Equals("kyty_emulator.exe", StringComparison.OrdinalIgnoreCase) ? "Executable found" : "Select kyty_emulator.exe";
    public void NotifyEmulatorAvailability() => Changed(nameof(EmulatorAvailability));

    public string OSFMountPath { get => Settings.OSFMountPath; set { Settings.OSFMountPath = value; installationAvailable = false; Changed(); NotifyInstallActions(); SaveInstallationSettings(); } }
    public string InstalledGamesRoot { get => Settings.InstalledGamesRoot; set { Settings.InstalledGamesRoot = value; Changed(); ReadInstalledMembership(); LibraryView.Refresh(); if (SelectedLibraryEntry is not null && !LibraryView.Contains(SelectedLibraryEntry)) SelectedLibraryEntry = null; NotifyInstallActions(); SaveInstallationSettings(); WatchInstalledFolder(); } }
    public bool RetainOriginalImage { get => Settings.RetainOriginalImage; set { Settings.RetainOriginalImage = value; Changed(); SaveInstallationSettings(); } }
    private void SaveInstallationSettings()
    {
        try { Save(); Status = "Image installation settings saved"; }
        catch (Exception ex) { Status = "Image installation settings could not be saved: " + ex.Message; throw; }
    }
    public string InstallationAvailability { get => installationAvailability; private set { installationAvailability = value; Changed(); } }
    public string InstallationProgressText { get => installationProgress; private set { installationProgress = value; Changed(); } }
    public double InstallationPercentage { get => installationPercentage; private set { installationPercentage = value; Changed(); } }
    public InstallationJob? SelectedInstallation { get => selectedInstallation; set { selectedInstallation = value; Changed(); Changed(nameof(CanRetrySelectedInstallation)); Changed(nameof(CanDeleteSelectedInstallation)); Changed(nameof(SelectedInstallationActionLabel)); } }
    private bool isPackageBatch, packageBatchCancelled;
    public bool IsInstalling => installationCancellation is not null || isPackageBatch || drainingInstallationQueue;
    public bool IsDeletingLibraryEntry => isDeletingLibraryEntry;
    public bool CanClearInstallations => !IsInstalling && Installations.Any(job => !job.IsCleared && job.CanClearHistory && !File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath));
    public bool CanInstallFromFile => !IsClearingDownloads && Path.IsPathFullyQualified(InstalledGamesRoot) && !IsExtracting && !IsMovingContent && !IsDeletingLibraryEntry;
    private LocalLibraryEntry? DownloadLibraryItem => LibraryItemFor(SelectedJob);
    private LocalLibraryEntry? LibraryItemFor(DownloadJob? job) => job is not null ? Settings.Library.FirstOrDefault(item =>
        item.DownloadArtifacts.Any(a => a.JobId == job.Id) || ImageFileSystem.SamePath(item.DownloadPath, job.Destination)) : null;
    public bool IsExtracting { get; private set; }
    private double extractionPercentage;
    private string extractionFeedback = "";
    public double ExtractionPercentage { get => extractionPercentage; private set { extractionPercentage = value; Changed(); } }
    public string ExtractionFeedback { get => extractionFeedback; private set { extractionFeedback = value; Changed(); } }
    public string DownloadActionLabel => DownloadActionFor(SelectedJob);
    internal string DownloadActionFor(DownloadJob? job) => job is not null && IsQueuedForInstallation(job.Destination) ? "Cancel Install" :
        job is not null && CanInstall(job) && IsInstalling ? "Queue Install" : IsExtracting ? "Extracting…" : LibraryItemFor(job)?.Extraction == "Extracted" ? "Check extracted files" :
        job is not null && IsExtractableArchive(job.Destination) ? "Extract and install" : "Install";
    private static bool IsExtractableArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".rar";
    public bool CanRunDownloadAction => CanRunDownload(SelectedJob);
    internal bool CanRunDownload(DownloadJob? job) => job is not null && IsQueuedForInstallation(job.Destination) ||
        !IsClearingDownloads && !IsExtracting && !IsMovingContent && (!IsInstalling || CanInstall(job)) && !IsDeletingLibraryEntry &&
        (job is null || !EmptyDownloadFolder(job)) &&
        (CanExtract(job) || CanInstall(job) || job is { State: "Downloaded" or "Imported" } && !WriterActive(job) && LibraryItemFor(job)?.Extraction == "Extracted");
    public bool ShowDownloadAction => IsExtracting || CanRunDownloadAction;
    public string DownloadExplanation
    {
        get
        {
            if (IsExtracting) return "Extraction is in progress. Supported runnable games move to My Library automatically after verification.";
            if (IsMovingContent) return "Checking files before moving supported runnable games into My Library…";
            if (SelectedJob is not { } job) return "Select a download to see its next step. ZIP/RAR archives can be extracted; PKG and exFAT images can be installed.";
            if (job.State is not ("Downloaded" or "Imported")) return $"{job.State}. Installation is available after the download completes and is verified.";
            if (DownloadLibraryItem is { Extraction: "Extracted" } item)
                return $"Already extracted to {item.ContentPath}\n" + (item.Classification.StartsWith("Cannot install:", StringComparison.Ordinal) ? item.Classification :
                    "Check the extracted files for installation. Source cleanup follows the retention setting.");
            return Path.GetExtension(job.Destination).ToLowerInvariant() switch
            {
                ".zip" or ".rar" => "Extract the archive and install its game folder into My Library. Source cleanup follows the retention setting after verification. KytyPS5 determines launch compatibility when launched.",
                ".exfat" => CanInstallDownload ? "Install this image into My Library. You will confirm the destination before installation." : InstallationAvailability + ". Check the installed games folder in Settings.",
                ".pkg" => "Install a complete PS4 or PS5 debug/fake PKG. Confirm the destination and original-file retention before final installation. Retail encryption requires matching key material.",
                ".7z" => "7z extraction is not supported. Extract it with an archive tool, then install the game folder or supported image.",
                ".ffpkg" or ".ffpfs" or ".ffpfsc" => "This filesystem image format needs conversion to an exFAT image or extraction with an image tool before installation.",
                _ => "This file cannot be installed directly. Open the Downloads folder to inspect it."
            };
        }
    }
    public bool CanInstallDownload => CanInstall(SelectedJob);
    private bool CanInstall(DownloadJob? job) => job is { State: "Downloaded" or "Imported" } && CanInstallFile(job.Destination);
    public bool CanExtractDownload => CanExtract(SelectedJob);
    private bool CanExtract(DownloadJob? job) => !IsExtracting && !IsMovingContent && !IsInstalling && job is { State: "Downloaded" or "Imported" } &&
        !WriterActive(job) && !Library.Any(item => item.Extraction == "Extracting") &&
        LibraryItemFor(job)?.Extraction != "Extracted" &&
        IsExtractableArchive(job.Destination) && File.Exists(job.Destination);
    public bool CanInstallLibrary => CanInstallItem(SelectedLibraryEntry);
    internal bool CanInstallItem(LocalLibraryEntry? item) => item is { AdditionalPaths.Count: 0, DeletionTargets: null } &&
        (IsQueuedForInstallation(item.ContentPath) || (Directory.Exists(item.ContentPath) ? !IsInstalling && CanInstallFromFile && IsInstalledLibraryItem(item) && item.PendingPackagePaths.Count > 0 : CanInstallFile(item.ContentPath)));
    public bool CanDeleteLibraryEntry => CanDeleteItem(SelectedLibraryEntry);
    internal bool CanDeleteItem(LocalLibraryEntry? item) => !IsClearingDownloads && !IsInstalling && !IsMovingContent && !IsDeletingLibraryEntry && item is { Extraction: not "Extracting" } && item.ContentMove is not { Phase: not "Completed" } && Library.Contains(item);
    public bool CanLaunchSelected => CanLaunchItem(SelectedLibraryEntry);
    internal bool CanLaunchItem(LocalLibraryEntry? item) => !IsUpdatingSharpEmu && !IsUpdatingKyty && !IsMovingContent && !IsDeletingLibraryEntry && item is { DeletionTargets: null } && LibraryView.Contains(item) && !launcher.IsActiveFor(item.ContentPath) && item.ContentMove is not { Phase: not "Completed" } && item.InstalledGame?.LaunchEligible != false &&
        (item.InstalledGame is null || item.InstalledGame.InstallationId != activeInstallation?.Id) &&
        (Directory.Exists(item.ContentPath) ? (item.FolderActionsInspected ? item.LaunchPaths.Count > 0 : item.Classification is "Ready for launch attempt" or "Installed — Ready for launch attempt") :
            item.Classification is "Ready for launch attempt" or "Direct ELF launch attempt" or "Installed — Ready for launch attempt") && !IsInstalling;
    private bool SourceInUse(string path) => reservedPaths.Any(reserved => ImageFileSystem.SamePath(reserved, path)) || Settings.Downloads.Any(job =>
        ImageFileSystem.SamePath(job.Destination, path) &&
        (job.State is not ("Downloaded" or "Imported" or "Installed — original removed") || BrowserDownloads.IsActive(job) ||
         job.GroupId.Length > 0 && Settings.Downloads.Any(member => member.GroupId == job.GroupId && member.Completion?.Phase != "Completed"))) ||
        Settings.Library.Any(item => item.AdditionalPaths.Any(part => ImageFileSystem.SamePath(part, path)));
    private bool CanInstallFile(string path, bool batch = false) => (CanInstallFromFile || batch && isPackageBatch && installationCancellation is null &&
        !IsClearingDownloads && !IsExtracting && !IsMovingContent && !IsDeletingLibraryEntry && Path.IsPathFullyQualified(InstalledGamesRoot)) &&
        (Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase) || installationAvailable) && !SourceInUse(path) &&
        !IsQueuedForInstallation(path) && (activeInstallation is null || !ImageFileSystem.SamePath(activeInstallation.SourcePath, path)) && InstallationSource.CanOwn(path);
    private void NotifyInstallActions()
    {
        Changed(nameof(CanRetrySelectedDownload)); Changed(nameof(CanDeleteSelectedDownload)); Changed(nameof(CanScanDownloads));
        Changed(nameof(DownloadActionLabel)); Changed(nameof(CanRunDownloadAction)); Changed(nameof(ShowDownloadAction)); Changed(nameof(DownloadExplanation));
        Changed(nameof(CanExtractDownload));
        Changed(nameof(IsInstalling)); Changed(nameof(CanInstallFromFile)); Changed(nameof(CanInstallDownload)); Changed(nameof(CanInstallLibrary));
        Changed(nameof(CanLaunchSelected)); Changed(nameof(CanClearInstallations)); Changed(nameof(CanRetrySelectedInstallation)); Changed(nameof(CanDeleteSelectedInstallation)); Changed(nameof(CanDeleteLibraryEntry));
        Changed(nameof(SelectedDownloadInstallationStatus)); Changed(nameof(LibraryInstallActionLabel)); Changed(nameof(SelectedInstallationActionLabel));
        StartQueuedInstallations();
    }
    public async Task TestInstallationAsync()
    {
        installationAvailable = false; NotifyInstallActions(); InstallationAvailability = "Checking OSFMount…";
        try
        {
            InstallationAvailability = await Task.Run(() => OSFMountDependency.Validate(OSFMountPath));
            installationAvailable = File.Exists(MountHelper);
            if (!installationAvailable) InstallationAvailability = "Mount helper is missing; rebuild/reinstall OrbitX.";
        }
        catch (Exception ex) { InstallationAvailability = ex.Message; }
        NotifyInstallActions();
    }
    private static string MountHelper => Path.Combine(AppContext.BaseDirectory, "PS5CatalogBrowser.MountHost.exe");
    private ImageInstallationCoordinator CreateInstaller(string? source = null, string passcode = "", IProgress<InstallationProgress>? progress = null) => new(
        source is not null && Path.GetExtension(source).Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            ? new PackageImageMounter(InstalledGamesRoot, passcode, progress) : new OSFMountAdapter(OSFMountPath, MountHelper), installationJournal,
        job =>
        {
            // Persist a detached candidate so failed writes cannot expose an unregistered game in the live library.
            var candidate = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(Settings))!;
            InstalledLibrary.Register(candidate, job); store.Save(candidate);
            foreach (var registered in candidate.Library.Where(item => item.InstalledGame?.InstallationId == job.Id || item.Id == job.ParentLibraryId))
            {
                if (job.ParentLibraryId.Length == 0) registered.Classification = "Installing — verification and cleanup";
                var index = Settings.Library.FindIndex(item => item.Id == registered.Id);
                if (index >= 0)
                {
                    Settings.Library[index] = registered;
                    var visible = Library.FirstOrDefault(item => item.Id == registered.Id);
                    if (visible is not null) Library[Library.IndexOf(visible)] = registered;
                }
                else { Settings.Library.Add(registered); Library.Add(registered); }
            }
            SelectedLibraryEntry = Library.First(item => item.InstalledGame?.InstallationId == job.Id || item.Id == job.ParentLibraryId);
            Changed(nameof(Library)); return Task.CompletedTask;
        }, job => Task.FromResult(InstalledLibrary.IsRegistered(store.Load(), job)), SourceInUse);
    private async Task RunImageInstallationAsync(string path, Func<InstallationConfirmation, Task<InstallationChoice?>>? confirm = null, string packagePasscode = "", LocalLibraryEntry? packageFolder = null)
    {
        if (installationCancellation is not null || !CanInstallFile(path, packageFolder is not null)) throw new InvalidOperationException("Choose an installed games directory and a complete, unowned PKG or exFAT image. exFAT also requires OSFMount. Check Settings and download status.");
        if (packageFolder is not null && (!Library.Contains(packageFolder) || !IsInstalledLibraryItem(packageFolder) ||
            !ImageFileSystem.IsWithin(packageFolder.ContentPath, path) || !Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a PKG belonging to this library folder.");
        var item = Library.FirstOrDefault(item => ImageFileSystem.SamePath(item.ContentPath, path));
        var download = Settings.Downloads.LastOrDefault(job => ImageFileSystem.SamePath(job.Destination, path));
        var catalogue = Entries.FirstOrDefault(entry => entry.Id == (item?.CatalogEntryId ?? download?.EntryId));
        var job = new InstallationJob
        {
            SourcePath = path, InstallationRoot = InstalledGamesRoot, Title = new[] { catalogue?.Title, item?.Title, download?.Title }.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))?.Trim() ?? "",
            TitleId = catalogue?.TitleId ?? "", CatalogEntryId = catalogue?.Id ?? item?.CatalogEntryId ?? download?.EntryId ?? "",
            CatalogSource = catalogue?.SourceName ?? item?.Source ?? download?.Source ?? "Local", DeleteOriginal = !RetainOriginalImage
        };
        if (packageFolder is not null)
        {
            job.ParentLibraryId = packageFolder.Id; job.RequireGameRoot = false; job.InstallationRoot = packageFolder.ContentPath;
            job.Title = Path.GetFileNameWithoutExtension(path); job.CatalogEntryId = packageFolder.CatalogEntryId; job.CatalogSource = packageFolder.Source;
        }
        var previous = Installations.LastOrDefault(old => !old.IsCleared && !old.Registered && !old.Verified &&
            old.State is InstallationState.Failed or InstallationState.Cancelled && ImageFileSystem.SamePath(old.SourcePath, path));
        if (previous is not null)
        {
            previous.State = InstallationState.Pending; previous.Error = ""; previous.DeleteOriginal = job.DeleteOriginal;
            previous.Files.Clear(); previous.Directories.Clear(); previous.Destination = previous.StagingPath = "";
            job = previous;
        }
        else Installations.Add(job);
        SelectedInstallation = job; Changed(nameof(Downloads)); Changed(nameof(Installations));
        installationCancellation = new(); activeInstallation = job; NotifyInstallActions();
        var progress = ProgressFor(job);
        try
        {
            await CreateInstaller(path, packagePasscode, progress).InstallAsync(job, confirm ?? (confirmation =>
            {
                var stem = confirmation.FolderName;
                for (var index = 0; index < 10000; index++)
                {
                    var name = stem + (index == 0 ? "" : " (" + index + ")");
                    var destination = Path.Combine(job.InstallationRoot, name);
                    if (!Directory.Exists(destination) && !File.Exists(destination))
                        return Task.FromResult<InstallationChoice?>(new(name, job.DeleteOriginal));
                }
                throw new IOException("No unused installation destination folder is available.");
            }), progress, installationCancellation.Token);
            foreach (var installed in Settings.Library.Where(item => item.InstalledGame?.InstallationId == job.Id))
            {
                var completed = job.State is InstallationState.Completed or InstallationState.CompletedOriginalRetained;
                installed.Classification = completed ? "Installed — Ready for launch attempt" : "Needs attention";
                installed.InstalledGame!.LaunchEligible = completed;
            }
            SaveInstallationCleanup(job);
            Status = job.Title + ": " + job.StateLabel + (job.Error.Length > 0 ? "; " + job.Error : "");
        }
        finally
        {
            installationCancellation.Dispose(); installationCancellation = null; activeInstallation = null; NotifyInstallActions();
            Changed(nameof(Library)); Changed(nameof(Downloads)); Changed(nameof(Installations));
            if (!isPackageBatch) await RefreshFolderActionsAsync();
        }
    }
    private IProgress<InstallationProgress> ProgressFor(InstallationJob job, string action = "Installing")
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        InstallationPercentage = 0; InstallationProgressText = "Preparing " + job.Title;
        return new Progress<InstallationProgress>(update =>
        {
            if (job.IsCleared && !ReferenceEquals(activeInstallation, job)) return;
            InstallationPercentage = update.State is InstallationState.Completed or InstallationState.CompletedOriginalRetained ? 100 : update.Percentage;
            InstallationProgressText = $"{action} {job.Title}\nStage: {update.StateLabel}\nProgress: {InstallationPercentage:F1}%\nProcessed: {update.Bytes / 1073741824.0:F2} GiB / {update.TotalBytes / 1073741824.0:F2} GiB  ·  {update.BytesPerSecond / 1048576.0:F1} MiB/s\n" +
                $"Elapsed: {clock.Elapsed:hh\\:mm\\:ss}" + (update.Remaining is { } eta ? $"  ·  Stage ETA: {eta:hh\\:mm\\:ss}" : "") + "\n" + update.File;
            Changed(nameof(Installations)); Changed(nameof(SelectedInstallation));
            Status = action + " " + job.Title + ": " + update.StateLabel + (job.Error.Length > 0 ? "; " + job.Error : "");
        });
    }
    public void CancelInstallation() { packageBatchCancelled = true; installationCancellation?.Cancel(); }
    public void ClearAllInstallations()
    {
        if (IsInstalling) throw new InvalidOperationException("Finish or cancel the active installation before clearing history.");
        try
        {
            foreach (var job in Installations.Where(job => !job.IsCleared && job.CanClearHistory && !File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath)))
            {
                job.IsCleared = true;
                try { installationJournal.Save(job); }
                catch { job.IsCleared = false; throw; }
            }
            InstallationProgressText = "No installation active"; InstallationPercentage = 0;
            Status = "Installation history cleared; image files, installed games and verification manifests preserved.";
        }
        finally
        {
            InstallationView.Refresh();
            if (SelectedInstallation?.IsCleared == true) SelectedInstallation = null;
            Changed(nameof(Installations)); Changed(nameof(CanClearInstallations));
        }
    }
    public async Task RetryInstallationCleanupAsync(InstallationJob job)
    {
        if (IsInstalling) throw new InvalidOperationException("Wait for the active installation.");
        installationCancellation = new(); activeInstallation = job; NotifyInstallActions();
        try
        {
            await CreateInstaller(job.SourcePath).RetryCleanupAsync(job, installationCancellation.Token, ProgressFor(job));
            SaveInstallationCleanup(job); Status = job.CleanupResult;
        }
        finally { installationCancellation.Dispose(); installationCancellation = null; activeInstallation = null; await RefreshFolderActionsAsync(); NotifyInstallActions(); Changed(nameof(Library)); Changed(nameof(Downloads)); Changed(nameof(Installations)); }
    }

    private void SaveInstallationCleanup(InstallationJob job)
    {
        InstalledLibrary.UpdateCleanup(Settings, job);
        Save();
        foreach (var download in Downloads.Where(download => download.IsCleared).ToArray())
        {
            Downloads.Remove(download);
            if (SelectedJob == download) SelectedJob = null;
        }
    }
    private bool Filter(object candidate)
    {
        if (candidate is not CatalogEntry item) return false;
        if (Sources.Any(s => s.Id == item.SourceId && !s.Enabled)) return false;
        // Hide an explicitly linked fallback or a proven identical snapshot. Never merge source identities.
        if (Sources.Any(s => s.Id == item.SourceId && s.SourceType == CatalogSourceType.OrbitLocal) &&
            orbitSnapshots.TryGetValue(item.SourceId, out var local) &&
            Sources.Any(s => s.Enabled && s.SourceType == CatalogSourceType.OrbitLive &&
                orbitSnapshots.TryGetValue(s.Id, out var live) &&
                (s.LocalFallbackSourceId == item.SourceId || live.ContentFingerprint == local.ContentFingerprint))) return false;
        return (Search.Length == 0 || (item.Title + " " + item.Description + " " + item.BrowseTitleId).Contains(Search, StringComparison.OrdinalIgnoreCase)) &&
            (SourceFilter == "All" || item.SourceName == SourceFilter) &&
            (KindFilter == "All" || (KindFilter == "other" ? !KindChoices.Skip(1).Take(3).Any(k => item.Kind.Contains(k, StringComparison.OrdinalIgnoreCase)) : item.Kind.Contains(KindFilter, StringComparison.OrdinalIgnoreCase))) &&
            (PlatformFilter == "All" || (PlatformFilter == "other" ? item.Platform is not ("PS5" or "PS4") : item.Platform.Equals(PlatformFilter, StringComparison.OrdinalIgnoreCase)));
    }
    private void ApplySort()
    {
        BrowseView.SortDescriptions.Clear();
        BrowseView.SortDescriptions.Add(new(Sort switch { "Source" => nameof(CatalogEntry.SourceName), "Version" => nameof(CatalogEntry.Version), "Size" => nameof(CatalogEntry.Size), _ => nameof(CatalogEntry.Title) }, ListSortDirection.Ascending));
    }
    public Task RefreshStartupAsync() => Task.WhenAll(Sources.Where(s => s.Enabled &&
        (s.SourceType != CatalogSourceType.OrbitLive || s.AutoRefresh)).Select(s => RefreshAsync(s)).ToArray());

    private async void OrbitRefreshTimer_Tick(object? sender, EventArgs e)
    {
        try { await RefreshAutomaticAsync(); }
        catch (Exception) { if (!disposed) Status = "Automatic catalogue refresh could not finish. Retry Refresh Now."; }
    }

    public async Task RefreshAutomaticAsync()
    {
        foreach (var source in Sources.Where(s => s.Enabled && s.SourceType == CatalogSourceType.OrbitLive && s.AutoRefresh).ToArray())
        {
            if (automaticAttempts.TryGetValue(source.Id, out var last) && catalogueClock.GetUtcNow() - last < LiveOrbitCatalog.AutomaticInterval) continue;
            await RefreshAsync(source);
        }
    }

    public async Task RefreshAllAsync()
    {
        await Task.WhenAll(Sources.Where(s => s.Enabled).Select(source => RefreshAsync(source)).ToArray());
    }
    public async Task RefreshAsync(CatalogSource source, bool testOnly = false)
    {
        if (disposed || !source.Enabled) return;
        if (source.SourceType is CatalogSourceType.OrbitLocal or CatalogSourceType.OrbitLive)
        {
            await RefreshLocalOrbitAsync(source, testOnly);
            return;
        }
        if (source.SourceType != CatalogSourceType.HttpJson) throw new InvalidOperationException("Unsupported catalogue source type.");
        var snapshot = new CatalogSource { Id = source.Id, Name = source.Name, Url = source.Url, SourceType = source.SourceType };
        var configurationVersion = sourceConfigurationVersions.GetValueOrDefault(source.Id);
        bool Current() => !disposed && source.Enabled && Sources.Contains(source) && source.Url == snapshot.Url &&
            source.SourceType == snapshot.SourceType && sourceConfigurationVersions.GetValueOrDefault(source.Id) == configurationVersion;
        try
        {
            Status = $"Refreshing {source.Name}…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (format, entries, data) = await http.LoadCatalogAsync(snapshot, timeout.Token);
            if (!Current()) return;
            string? cacheError = null;
            try { await cache.SaveAsync(snapshot, data, timeout.Token); }
            catch (Exception ex) { cacheError = "Offline copy could not be saved: " + ex.Message; }
            if (!Current()) return;
            source.Format = format;
            source.LastSuccessfulRefresh = DateTimeOffset.Now;
            source.LastError = cacheError ?? "";
            if (!testOnly) await ReplaceEntriesAsync(source, entries);
            if (!Current()) return;
            Status = $"{source.Name}: {entries.Count} entries ({format})" + (cacheError is null ? "" : "; " + cacheError);
        }
        catch (OperationCanceledException) { if (Current()) await UseCachedAsync(source, "Catalogue refresh timed out after 30 seconds.", testOnly, Current); }
        catch (Exception ex) { if (Current()) await UseCachedAsync(source, ex.Message, testOnly, Current); }
        finally { RefreshSourceChoices(); Save(); }
    }
    private async Task RefreshLocalOrbitAsync(CatalogSource source, bool testOnly)
    {
        if (disposed || !source.Enabled || !refreshingSources.Add(source.Id)) return;
        if (source.SourceType == CatalogSourceType.OrbitLive) automaticAttempts[source.Id] = catalogueClock.GetUtcNow();
        // Capture configuration before yielding. Removed/edited sources must not receive an obsolete result.
        var snapshot = new CatalogSource { Id = source.Id, Name = source.Name, SourceType = source.SourceType,
            Url = source.Url, EncryptedFilePath = source.EncryptedFilePath, KeyFilePath = source.KeyFilePath, Enabled = source.Enabled };
        var configurationVersion = sourceConfigurationVersions.GetValueOrDefault(source.Id);
        bool Current() => !disposed && Sources.Contains(source) && source.SourceType == snapshot.SourceType &&
            sourceConfigurationVersions.GetValueOrDefault(source.Id) == configurationVersion &&
            source.Url == snapshot.Url && source.EncryptedFilePath == snapshot.EncryptedFilePath && source.KeyFilePath == snapshot.KeyFilePath && source.Enabled == snapshot.Enabled;
        try
        {
            Status = $"Loading {source.Name}…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(catalogueLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = snapshot.SourceType == CatalogSourceType.OrbitLive
                ? await LiveOrbitCatalog.LoadAsync(snapshot, orbitHttp, timeout.Token)
                : await LocalOrbitCatalog.LoadAsync(snapshot, timeout.Token);
            if (!Current()) return;
            if (snapshot.SourceType == CatalogSourceType.OrbitLive)
                LiveOrbitCatalog.ValidateUpdate(orbitSnapshots.GetValueOrDefault(source.Id), result);
            foreach (var entry in result.Entries) entry.SourceName = source.Name;
            var format = $"Orbit schema {result.SchemaVersion} / feed {result.FeedVersion} / revision {result.Revision}";
            source.LastError = "";
            if (!testOnly || source.SourceType == CatalogSourceType.OrbitLocal)
            {
                source.Format = format; source.LastSuccessfulRefresh = DateTimeOffset.Now;
                source.FeedGeneration = result.FeedVersion; source.Revision = result.Revision;
                source.GameCount = result.Entries.Count; source.ReleaseCount = result.Entries.Sum(e => e.Artifacts.Count);
            }
            if (!testOnly && source.Enabled)
            {
                orbitSnapshots[source.Id] = result;
                await ReplaceEntriesAsync(source, result.Entries);
                BrowseView.Refresh(); Changed(nameof(OrbitBrowseSummary));
            }
            Status = $"{source.Name}: " + (testOnly ? "Connection verified; " : "") + $"{result.Entries.Count} games, {result.Entries.Sum(e => e.Artifacts.Count)} release options ({format})";
        }
        catch (Exception ex)
        {
            if (!Current()) return;
            var error = ex is OperationCanceledException ? "Catalogue request cancelled or timed out; retry refresh." : ex.Message;
            source.LastError = "Refresh failed: " + error + (Entries.Any(e => e.SourceId == source.Id) ? " Showing last valid in-memory entries." : " Source unavailable; no entries loaded this session.");
            Status = source.Name + ": " + source.LastError;
        }
        finally { refreshingSources.Remove(source.Id); RefreshSourceChoices(); Save(); }
    }
    private async Task UseCachedAsync(CatalogSource source, string error, bool testOnly, Func<bool> current)
    {
        try
        {
            var saved = await cache.LoadAsync(source, CancellationToken.None);
            if (!current()) return;
            if (saved is not null)
            {
                source.Format = saved.Value.Format;
                if (!testOnly) await ReplaceEntriesAsync(source, saved.Value.Entries);
                if (!current()) return;
                source.LastError = "Refresh failed: " + error + (testOnly ? " Saved copy is available." : " Showing last saved copy.");
                Status = testOnly ? $"{source.Name}: connection failed; saved copy is available. {error}" :
                    $"{source.Name}: showing {saved.Value.Entries.Count} saved entries; {error}";
                return;
            }
            source.LastError = error + " No saved copy is available for this source URL.";
        }
        catch (Exception cacheError) { if (!current()) return; source.LastError = error + " Saved copy could not be loaded: " + cacheError.Message; }
        Status = $"{source.Name}: {source.LastError}";
    }
    private async Task ReplaceEntriesAsync(CatalogSource source, List<CatalogEntry> entries)
    {
        var configurationVersion = sourceConfigurationVersions.GetValueOrDefault(source.Id);
        bool Current() => !disposed && source.Enabled && Sources.Contains(source) &&
            sourceConfigurationVersions.GetValueOrDefault(source.Id) == configurationVersion;
        if (!Current()) return;
        RestoreCatalogArtwork(source, entries);
        var selectedId = SelectedEntry?.SourceId == source.Id ? SelectedEntry.Id : null;
        var releaseId = selectedId is not null ? SelectedArtifact?.Id : null;
        var mirrorUrl = selectedId is not null ? SelectedMirror?.Url : null;
        foreach (var old in Entries.Where(e => e.SourceId == source.Id).ToArray()) Entries.Remove(old);
        var added = 0;
        foreach (var entry in entries)
        {
            if (!Current()) return;
            Entries.Add(entry);
            if (++added % 50 == 0 && source.SourceType == CatalogSourceType.HttpJson) await Task.Yield();
        }
        if (Current() && selectedId is not null)
        {
            SelectedEntry = entries.FirstOrDefault(e => e.Id == selectedId);
            if (releaseId is not null)
                SelectedMirror = MirrorChoices.FirstOrDefault(m => m.Artifact.Id == releaseId && m.Url == mirrorUrl) ?? MirrorChoices.FirstOrDefault(m => m.Artifact.Id == releaseId);
        }
    }
    public void AddSource(string name, string url)
    {
        NetworkPolicy.Validate(url);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a source name.");
        var source = new CatalogSource { Name = name.Trim(), Url = url.Trim() };
        Sources.Add(source); Settings.Sources.Add(source); SelectedSource = source; RefreshSourceChoices(); Save();
    }
    private void RestoreCatalogArtwork(CatalogSource source, List<CatalogEntry> entries)
    {
        var byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var changed = false;
        foreach (var job in Settings.Downloads.Where(j => j.SourceId == source.Id && j.CoverUrl.Length == 0 && j.CoverFallbackUrl.Length == 0))
            if (byId.TryGetValue(job.EntryId, out var entry))
            { job.CoverUrl = entry.CoverUrl; job.CoverFallbackUrl = entry.CoverFallbackUrl; changed = true; }
        foreach (var item in Settings.Library.Where(i => i.CoverUrl.Length == 0 && i.CoverFallbackUrl.Length == 0 &&
            (i.SourceId == source.Id || i.SourceId.Length == 0 && i.DownloadArtifacts.Any(a => a.SourceId == source.Id))))
            if (byId.TryGetValue(item.CatalogEntryId, out var entry))
            { item.SourceId = source.Id; item.CoverUrl = entry.CoverUrl; item.CoverFallbackUrl = entry.CoverFallbackUrl; changed = true; }
        if (changed) { Changed(nameof(Downloads)); Changed(nameof(Library)); }
    }
    public void UpdateSource(CatalogSource source, string name, string url, bool enabled)
    {
        if (source.SourceType != CatalogSourceType.HttpJson) throw new InvalidOperationException("Select an HTTP JSON source.");
        NetworkPolicy.Validate(url);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a source name.");
        var urlChanged = !source.Url.Equals(url.Trim(), StringComparison.Ordinal);
        source.Name = name.Trim(); source.Url = url.Trim(); source.Enabled = enabled;
        if (urlChanged) { source.Format = "Unknown"; source.LastSuccessfulRefresh = null; source.LastError = ""; }
        if (urlChanged || !enabled) RemoveSourceEntries(source);
        RefreshSourceChoices(); Save();
    }
    public void SetSourceEnabled(CatalogSource source, bool enabled)
    {
        if (!Sources.Contains(source)) throw new InvalidOperationException("Select a configured source.");
        if (source.Enabled == enabled) return;
        source.Enabled = enabled;
        // Invalidate pending refreshes without removing and rebuilding the loaded catalogue.
        sourceConfigurationVersions[source.Id] = sourceConfigurationVersions.GetValueOrDefault(source.Id) + 1;
        if (!enabled)
        {
            automaticAttempts.Remove(source.Id);
            if (SelectedEntry?.SourceId == source.Id) SelectedEntry = null;
        }
        RefreshSourceChoices(refreshBrowse: false); Save();
    }
    public void RefreshBrowseIfNeeded()
    {
        if (!browseRefreshPending) return;
        browseRefreshPending = false;
        BrowseView.Refresh();
    }
    public void AddLiveOrbitSource(string name, string url, string keyPath, bool enabled, bool autoRefresh, string localFallbackSourceId = "")
    {
        ValidateLiveSource(name, url, keyPath, enabled);
        ValidateFallback(localFallbackSourceId);
        var source = new CatalogSource { Name = name.Trim(), Url = url.Trim(), KeyFilePath = keyPath.Trim(),
            SourceType = CatalogSourceType.OrbitLive, Enabled = enabled, AutoRefresh = autoRefresh, LocalFallbackSourceId = localFallbackSourceId };
        Sources.Add(source); Settings.Sources.Add(source); SelectedSource = source; RefreshSourceChoices(); Save();
    }
    public void UpdateLiveOrbitSource(CatalogSource source, string name, string url, string keyPath, bool enabled, bool autoRefresh, string localFallbackSourceId = "")
    {
        if (source.SourceType != CatalogSourceType.OrbitLive) throw new InvalidOperationException("Select a live Orbit source.");
        ValidateLiveSource(name, url, keyPath, enabled);
        ValidateFallback(localFallbackSourceId);
        var changed = source.Url != url.Trim() || source.KeyFilePath != keyPath.Trim();
        source.Name = name.Trim(); source.Url = url.Trim(); source.KeyFilePath = keyPath.Trim();
        source.Enabled = enabled; source.AutoRefresh = autoRefresh; source.LocalFallbackSourceId = localFallbackSourceId;
        if (changed || !enabled) RemoveSourceEntries(source);
        else foreach (var entry in Entries.Where(e => e.SourceId == source.Id)) entry.SourceName = source.Name;
        if (changed) { source.Format = "Unknown"; source.FeedGeneration = null; source.Revision = null;
            source.GameCount = 0; source.ReleaseCount = 0; source.LastSuccessfulRefresh = null; source.LastError = ""; }
        RefreshSourceChoices(); BrowseView.Refresh(); Save();
    }
    private static void ValidateLiveSource(string name, string url, string keyPath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a source name.");
        LiveOrbitCatalog.ValidateUrl(url.Trim());
        if ((enabled || !string.IsNullOrWhiteSpace(keyPath)) && !Path.IsPathFullyQualified(keyPath.Trim()))
            throw new ArgumentException("Select an absolute path to a local decoding key file.");
    }
    private void ValidateFallback(string id)
    {
        if (id.Length > 0 && !Sources.Any(s => s.Id == id && s.SourceType == CatalogSourceType.OrbitLocal))
            throw new ArgumentException("Select an existing local Orbit source as the fallback for this feed.");
    }
    public void AddLocalOrbitSource(string name, string filePath, string keyPath, bool enabled = true)
    {
        LocalOrbitCatalog.ValidatePaths(filePath.Trim(), keyPath.Trim());
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a source name.");
        var source = new CatalogSource { Name = name.Trim(), SourceType = CatalogSourceType.OrbitLocal,
            EncryptedFilePath = filePath.Trim(), KeyFilePath = keyPath.Trim(), Enabled = enabled };
        Sources.Add(source); Settings.Sources.Add(source); SelectedSource = source; RefreshSourceChoices(); Save();
    }
    public void UpdateLocalOrbitSource(CatalogSource source, string name, string filePath, string keyPath, bool enabled)
    {
        if (source.SourceType != CatalogSourceType.OrbitLocal) throw new InvalidOperationException("Select a local Orbit source.");
        LocalOrbitCatalog.ValidatePaths(filePath.Trim(), keyPath.Trim());
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a source name.");
        var pathsChanged = source.EncryptedFilePath != filePath.Trim() || source.KeyFilePath != keyPath.Trim();
        source.Name = name.Trim(); source.EncryptedFilePath = filePath.Trim(); source.KeyFilePath = keyPath.Trim(); source.Enabled = enabled;
        if (pathsChanged) { source.Format = "Unknown"; source.LastSuccessfulRefresh = null; source.LastError = ""; }
        if (pathsChanged || !enabled) RemoveSourceEntries(source);
        else foreach (var entry in Entries.Where(e => e.SourceId == source.Id)) entry.SourceName = source.Name;
        RefreshSourceChoices(); Save();
    }
    private void RemoveSourceEntries(CatalogSource source)
    {
        sourceConfigurationVersions[source.Id] = sourceConfigurationVersions.GetValueOrDefault(source.Id) + 1;
        orbitSnapshots.Remove(source.Id);
        automaticAttempts.Remove(source.Id);
        if (SelectedEntry?.SourceId == source.Id) SelectedEntry = null;
        foreach (var entry in Entries.Where(e => e.SourceId == source.Id).ToArray()) Entries.Remove(entry);
    }
    public void RemoveSource(CatalogSource source)
    {
        Sources.Remove(source); Settings.Sources.Remove(source);
        foreach (var live in Sources.Where(s => s.LocalFallbackSourceId == source.Id)) live.LocalFallbackSourceId = "";
        RemoveSourceEntries(source);
        SelectedSource = null; RefreshSourceChoices(); Save();
    }
    private void RefreshSourceChoices(bool refreshBrowse = true)
    {
        var names = Sources.Where(source => source.Enabled).Select(source => source.Name).Prepend("All").Distinct(StringComparer.Ordinal).ToArray();
        if (!names.Contains(SourceFilter)) { sourceFilter = "All"; Changed(nameof(SourceFilter)); }
        foreach (var stale in SourceChoices.Where(name => !names.Contains(name)).ToArray()) SourceChoices.Remove(stale);
        foreach (var name in names.Where(name => !SourceChoices.Contains(name))) SourceChoices.Add(name);
        foreach (var old in LocalOrbitFallbackChoices.Where(s => s.Id.Length > 0 && !Sources.Contains(s)).ToArray()) LocalOrbitFallbackChoices.Remove(old);
        foreach (var local in Sources.Where(s => s.SourceType == CatalogSourceType.OrbitLocal && !LocalOrbitFallbackChoices.Contains(s))) LocalOrbitFallbackChoices.Add(local);
        browseRefreshPending = !refreshBrowse;
        if (refreshBrowse) BrowseView.Refresh();
        Changed(nameof(Sources));
    }
    public async Task DownloadAsync(CatalogEntry entry, CatalogArtifact artifact, string? selectedUrl = null, string? provider = null)
    {
        DownloadAdmission.EnsureEnabled(entry);
        EnsureDownloadsAvailable();
        OrbitDownloadPolicy.ValidateSelection(entry, artifact);
        if (entry.CatalogFormat == "Orbit" && (!OrbitDownloadPolicy.DirectUrl(artifact.Url) || selectedUrl is not null && selectedUrl != artifact.Url))
            throw new InvalidOperationException("This Orbit release requires a browser workflow or completed-file import.");
        if (selectedUrl is not null && selectedUrl != artifact.Url && !artifact.Mirrors.Contains(selectedUrl))
            throw new InvalidOperationException("Select a listed file URL or mirror.");
        var job = admission.Admit([DownloadAdmission.Capture(entry, artifact)],
            () => [CreateJob(entry, artifact, "", selectedUrl)], PersistDownloads, RollbackDownloads)[0];
        if (provider is not null && entry.CatalogFormat != "Orbit") job.Mirror = provider;
        await RunDownloadAsync(job);
    }
    public async Task DownloadAllAsync(CatalogEntry entry)
    {
        if (!entry.DownloadAllEnabled) throw new InvalidOperationException("Orbit alternatives must be selected individually. Download All is disabled.");
        DownloadAdmission.EnsureEnabled(entry);
        EnsureDownloadsAvailable();
        if (entry.Artifacts.Count == 0) throw new InvalidOperationException("This entry has no downloadable files.");
        if (entry.CatalogFormat == "OptiStore" && entry.Artifacts.Count > 1)
            throw new InvalidOperationException("OptiStore links may be alternatives, mirrors, updates or multipart files. Select and download the specific links you need individually.");
        var group = Guid.NewGuid().ToString("N");
        var jobs = admission.Admit(entry.Artifacts.Select(a => DownloadAdmission.Capture(entry, a)).ToArray(),
            () =>
            {
                var prepared = new List<DownloadJob>();
                try
                {
                    foreach (var artifact in entry.Artifacts) prepared.Add(CreateJob(entry, artifact, group, pending: prepared));
                    return prepared;
                }
                catch { foreach (var job in prepared) BrowserDownloadFiles.DiscardStaging(job); throw; }
            }, PersistDownloads, RollbackDownloads);
        try
        {
            foreach (var job in jobs)
                if (!await RunDownloadAsync(job)) return;
        }
        finally
        {
            foreach (var job in jobs)
            {
                if (job.State == "Queued") job.State = "Cancelled";
                admission.Release(job);
            }
            Save();
        }
    }
    private DownloadJob CreateJob(CatalogEntry entry, CatalogArtifact artifact, string group, string? selectedUrl = null, IEnumerable<DownloadJob>? pending = null)
    {
        var url = selectedUrl ?? artifact.Url;
        NetworkPolicy.Validate(url);
        var name = SafeFileName(artifact.FileName.Length > 0 ? artifact.FileName : artifact.Name.Length > 0 ? artifact.Name : Path.GetFileName(new Uri(artifact.Url).LocalPath));
        var root = Path.GetFullPath(DownloadRoot);
        ImageFileSystem.EnsureNoLinks(root);
        Directory.CreateDirectory(root);
        var destination = UniquePath(root, name, pending);
        var job = new DownloadJob { EntryId = entry.Id, Title = entry.Title, Source = entry.SourceName, SourceId = entry.SourceId, ArtifactId = artifact.Id, GroupId = group,
            CoverUrl = entry.CoverUrl, CoverFallbackUrl = entry.CoverFallbackUrl,
            OrbitRelease = OrbitReleaseSelection.Capture(entry, artifact),
            Url = url, Destination = destination, ExpectedSha256 = artifact.Sha256, ExpectedSize = artifact.Size is > 0 ? artifact.Size : null,
            ExpectedFileName = name, FileName = Path.GetFileName(destination),
            Mirror = entry.CatalogFormat == "Orbit" ? artifact.Provider : Uri.TryCreate(url, UriKind.Absolute, out var host) ? host.Host : "", Version = artifact.Version.Length > 0 ? artifact.Version : entry.Version };
        BrowserDownloadFiles.PrepareStaging(job);
        return job;
    }
    private void PersistDownloads(List<DownloadJob> jobs)
    {
        EnsureDownloadsAvailable();
        var previous = SelectedJob;
        var replaced = Downloads.Where(old => jobs.Any(job => SameRetryRelease(old, job) || storeRetryDownloads.Contains(old.Id) && SameRetryGame(old, job)) && FailedDownload(old) &&
            !WriterActive(old) && !old.CleanupPending && !HttpResume.Reserves(old) && !DownloadCompletion.Unresolved(old)).ToArray();
        try
        {
            foreach (var old in replaced) old.IsCleared = true;
            foreach (var job in jobs) { Downloads.Add(job); Settings.Downloads.Add(job); }
            SelectedJob = jobs.Last(); Save();
            foreach (var old in replaced) { Downloads.Remove(old); storeRetryDownloads.Remove(old.Id); RetireFailedInstallations(old); }
            foreach (var job in jobs) ui.BeginInvoke(new Action(() =>
            {
                if (!disposed && Settings.Downloads.Contains(job) && !job.IsCleared)
                    try { DownloadAdded?.Invoke(job); } catch { System.Diagnostics.Trace.WriteLine("Download-added notification unavailable."); }
            }));
        }
        catch { foreach (var old in replaced) old.IsCleared = false; RollbackDownloads(jobs); SelectedJob = previous; throw; }
    }
    private void RollbackDownloads(List<DownloadJob> jobs)
    {
        foreach (var job in jobs)
        {
            Downloads.Remove(job); Settings.Downloads.Remove(job);
            if (job.StagingPath.Length > 0 && Directory.Exists(Path.GetDirectoryName(job.StagingPath)) && !File.Exists(job.StagingPath)) BrowserDownloadFiles.DiscardStaging(job);
        }
        if (SelectedJob is not null && jobs.Contains(SelectedJob)) SelectedJob = null;
    }
    public async Task RestartAsync(DownloadJob job)
    {
        EnsureDownloadsAvailable();
        if (job.Method != "Direct") throw new InvalidOperationException("Open the mirror again from Browse. Browser transfers can only resume within their original session.");
        if (job.Completion is not null || CompletedHistory(job)) throw new IOException("Completed or finalising download evidence cannot be restarted.");
        if (!Settings.Downloads.Contains(job) || !Downloads.Contains(job)) throw new IOException("Restart requires the original retained job.");
        if (job.HttpCheckpoint is { } checkpoint && checkpoint.Identity != HttpResume.Identity(job, checkpoint))
            throw new IOException("Original HTTP selection or representation identity changed. Files were preserved.");
        if (job.State == "Queued" && directTransfers.ContainsKey(job.Id)) return;
        if (!restartingJobs.Add(job.Id)) throw new IOException("This job already has a restart in progress.");
        try
        {
            if (!admission.Owns(job)) admission.Admit([job.SelectionIdentity], () => [job], _ => Save(), _ => { });
            if (directTransfers.TryGetValue(job.Id, out var transfer))
            {
                pauseRequests.Add(job.Id); transfer.Cancellation.Cancel();
                await transfer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (job.Completion is not null || CompletedHistory(job)) throw new IOException("Download completed while stopping. Its files were preserved.");
            if (File.Exists(job.Destination) || Directory.Exists(job.Destination)) throw new IOException("Destination already exists. Its files were preserved.");
            if (job.StagingPath.Length > 0 && Directory.Exists(Path.GetDirectoryName(job.StagingPath)))
            {
                job.CleanupPending = true; await store.SaveAsync(Settings);
                await Task.Run(() => HttpResume.DiscardPartial(job));
                if (Directory.Exists(Path.GetDirectoryName(job.StagingPath))) throw new IOException("Owned staging contains other files. Its reservation and files were preserved.");
            }
            job.HttpCheckpoint = null; job.CleanupPending = false;
            job.StagingPath = job.StagingRoot = job.StagingOwner = "";
            job.DownloadedBytes = 0; job.TotalBytes = null; job.ActualSha256 = ""; job.Error = "";
            job.State = "Queued"; job.CanPause = job.CanResume = false;
            BrowserDownloadFiles.PrepareStaging(job); await store.SaveAsync(Settings);
        }
        catch
        {
            if (job.Completion is null && !CompletedHistory(job))
            { job.State = "Needs attention"; job.Error = "Restart could not safely prepare the original job. Keep its files and record."; }
            admission.Release(job); throw;
        }
        finally { restartingJobs.Remove(job.Id); }
        if (stoppingDirect) return;
        await RunDownloadAsync(job);
    }

    public async Task PauseSelectedDownloadAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a download.");
        if (job.Method != "Direct") { BrowserDownloads.Pause(job); return; }
        if (!job.CanPause || !directTransfers.TryGetValue(job.Id, out var transfer)) throw new IOException("This HTTP transfer is not safely resumable.");
        pauseRequests.Add(job.Id); job.CanPause = false; transfer.Cancellation.Cancel();
        await transfer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task ResumeSelectedDownloadAsync()
    {
        EnsureDownloadsAvailable();
        var job = SelectedJob ?? throw new InvalidOperationException("Select a download.");
        if (job.Method != "Direct") { BrowserDownloads.Resume(job); return; }
        if (directTransfers.ContainsKey(job.Id) || restartingJobs.Contains(job.Id)) throw new IOException("This job already has a queued or active writer or restart.");
        if (!job.CanResume || !HttpResume.Eligible(job) || !Settings.Downloads.Contains(job)) throw new IOException("This partial cannot safely resume. Explicit Restart is required.");
        if (!admission.Owns(job)) admission.Admit([job.SelectionIdentity], () => [job], _ => Save(), _ => { });
        await RunDownloadAsync(job, true);
    }

    public bool HasDirectTransfers => directTransfers.Count > 0;
    public bool HasDirectMaintenance => restartingJobs.Count > 0;
    public async Task StopDirectDownloadsAsync()
    {
        stoppingDirect = true;
        var transfers = directTransfers.ToArray();
        foreach (var pair in transfers) { pauseRequests.Add(pair.Key); pair.Value.Cancellation.Cancel(); }
        try { await Task.WhenAll(transfers.Select(pair => pair.Value.Finished.Task)); }
        finally { stoppingDirect = false; }
    }

    private async Task<bool> RunDownloadAsync(DownloadJob job, bool resume = false)
    {
        if (job.State == "Cancelled") { admission.Release(job); return false; }
        var cancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        directTransfers.Add(job.Id, (cancellation, finished));
        var acquired = false; downloadLinkFailures.Remove(job.Id);
        try
        {
            await downloadGate.WaitAsync(cancellation.Token); acquired = true;
            await Bandwidth.BeginDirectAsync(cancellation.Token);
            if (!reservedPaths.Add(job.Destination)) throw new IOException("Destination is already reserved by another download.");
            job.State = "Downloading"; Save();
            Status = $"Downloading {job.Title}…";
            var progress = new Progress<DownloadJob>(_ => { Changed(nameof(Downloads)); Changed(nameof(SelectedJob)); Status = $"Downloading {job.Title}: {job.DownloadedBytes:N0} bytes"; });
            await http.DownloadAsync(job, progress, cancellation.Token, _ =>
                ui.InvokeAsync(() => store.SaveAsync(Settings)).Task.Unwrap(), resume);
            await DownloadCompletion.CompleteAsync(job, Settings, store.Save, cancellation.Token);
            SyncDownloadLibrary();
            try { BrowserDownloadFiles.DiscardStaging(job); }
            catch (IOException) { job.Error = "Download completed; its empty staging directory could not be removed."; }
            Status = job.State == "Installed" ? $"Installed {job.Title}" : $"Downloaded {job.Title}";
            return true;
        }
        catch (OperationCanceledException)
        {
            var paused = pauseRequests.Contains(job.Id) && HttpResume.Eligible(job);
            job.State = paused ? "Paused" : "Cancelled"; job.CanResume = paused;
            job.Error = paused ? "Owned partial paused and checkpointed. Resume continues this job after validation." :
                acquired ? "Application-managed HTTP download cancelled. Owned incomplete data retained; explicitly Restart or Discard incomplete." :
                "Queued HTTP download cancelled before its transfer started.";
            Status = job.Error; return false;
        }
        catch (Exception ex)
        {
            if (ex is System.Net.Http.HttpRequestException or InvalidDataException) downloadLinkFailures.Add(job.Id);
            if (!DownloadCompletion.Unresolved(job) && job.State is ("Queued" or "Downloading" or "Verifying download" or "Verified"))
            { job.State = ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase) ? "Integrity failed" : "Failed"; job.Error = ex.Message; }
            Status = $"Download failed: {ex.Message}"; return false;
        }
        finally
        {
            if (acquired) { Bandwidth.EndDirect(); reservedPaths.Remove(job.Destination); downloadGate.Release(); }
            directTransfers.Remove(job.Id); cancellation.Dispose(); pauseRequests.Remove(job.Id); if (!restartingJobs.Contains(job.Id)) admission.Release(job);
            try
            {
                try { Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "Download state could not be saved; keep its files and recovery record."; }
                Changed(nameof(Downloads)); Changed(nameof(Library)); Changed(nameof(SelectedJob)); Changed(nameof(CanRetrySelectedDownload));
            }
            finally { finished.TrySetResult(); }
        }
    }
    private void SyncDownloadLibrary()
    {
        foreach (var cleared in Downloads.Where(job => job.IsCleared).ToArray())
        {
            Downloads.Remove(cleared);
            if (SelectedJob == cleared) SelectedJob = null;
        }
        foreach (var item in Settings.Library.Where(item => item.DownloadRegistrationId.Length > 0))
        {
            var existing = Library.FirstOrDefault(record => record.Id == item.Id);
            if (existing is null) Library.Add(item);
            else if (!ReferenceEquals(existing, item)) Library[Library.IndexOf(existing)] = item;
        }
    }
    private void EnsureDownloadsAvailable()
    {
        if (IsMovingContent) throw new InvalidOperationException("Wait for the runnable content move to finish.");
        if (disposed || stoppingDirect) throw new InvalidOperationException("Application is closing; new transfers cannot start.");
        if (IsClearingDownloads) throw new InvalidOperationException("Downloads are being cleared. Wait before starting another transfer.");
    }
    public void CancelSelectedDownload()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a download.");
        if (DownloadCompletion.Unresolved(job)) { Status = "Finalisation evidence must be kept. Restart to recover this download."; return; }
        if (directTransfers.TryGetValue(job.Id, out var transfer)) { transfer.Cancellation.Cancel(); return; }
        if (BrowserDownloads.IsActive(job)) { BrowserDownloads.Cancel(job); return; }
        if (job.State == "Checking integrity") { Status = "Wait for file verification to finish."; return; }
        if (job.State == "Queued") { job.State = "Cancelled"; job.Error = "Queued download cancelled before its transfer started."; admission.Release(job); Save(); Changed(nameof(Downloads)); Status = job.Error; return; }
        Status = "No active transfer. Keep this record, clear completed history, or explicitly discard incomplete data.";
    }
    private void ClearDownloadRecord(DownloadJob job)
    {
        job.IsCleared = true;
        try { Save(); }
        catch { job.IsCleared = false; throw; }
        Downloads.Remove(job);
        if (SelectedJob == job) SelectedJob = null;
    }
    private bool WriterActive(DownloadJob job) => restartingJobs.Contains(job.Id) || directTransfers.ContainsKey(job.Id) || BrowserDownloads.IsActive(job) || admission.Owns(job) && !HttpResume.Reserves(job);
    private static bool CompletedHistory(DownloadJob job) => !DownloadCompletion.Unresolved(job) && job.State is ("Downloaded" or "Imported" or "Installed" or "Missing" or "Changed");
    internal bool CanClearDownload(DownloadJob job) => CompletedHistory(job) && !WriterActive(job) && !File.Exists(job.Destination) && !Directory.Exists(job.Destination);
    internal bool CanCancelDownload(DownloadJob job) => WriterActive(job) || job.State == "Queued";
    internal bool CanDiscardDownload(DownloadJob job) => !IsClearingDownloads && !restartingJobs.Contains(job.Id) && !CompletedHistory(job) && !DownloadCompletion.Unresolved(job);
    internal bool CanRestartDownload(DownloadJob job) => !retryingDownloads.Contains(job.Id) && CanDiscardDownload(job) && job.Method == "Direct" && job.Completion is null;

    public void ClearSelectedHistory()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a download.");
        if (!CanClearDownload(job)) throw new IOException("Incomplete downloads must be kept or explicitly discarded.");
        ClearDownloadRecord(job); Status = "History cleared; completed files and library entries preserved.";
    }

    public Task DiscardSelectedDownloadAsync() => DiscardDownloadAsync(SelectedJob ?? throw new InvalidOperationException("Select a download."), clearHistory: true);
    private async Task DiscardDownloadAsync(DownloadJob job, bool clearHistory)
    {
        if (IsClearingDownloads) return;
        if (restartingJobs.Contains(job.Id) || clearHistory && retryingDownloads.Contains(job.Id)) throw new IOException("Wait for the selected retry or restart to finish before discarding.");
        if (CompletedHistory(job)) throw new IOException("Use Clear selected history for completed downloads.");
        if (DownloadCompletion.Unresolved(job)) throw new IOException("Verified completion evidence must be kept. Restart to recover this download.");
        IsClearingDownloads = true;
        try
        {
            if (!Settings.Downloads.Contains(job) || !Downloads.Contains(job)) throw new IOException("The selected job is not a retained persisted download.");
            if (directTransfers.TryGetValue(job.Id, out var transfer))
            {
                transfer.Cancellation.Cancel();
                try { await transfer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { throw new IOException("Writer termination is not confirmed. Keep this record and retry after the transfer stops."); }
            }
            if (BrowserDownloads.IsActive(job)) await BrowserDownloads.CancelAsync(job);
            if (WriterActive(job)) throw new IOException("The writer is still active. Cancel it and retry after termination.");
            // Cancellation can race successful completion. Never discard a newly completed transfer.
            if (CompletedHistory(job)) throw new IOException("The transfer completed. Clear history to preserve its files.");
            if (DownloadCompletion.Unresolved(job)) throw new IOException("The transfer has verified completion evidence. Keep its files and restart to recover.");
            if (job.StagingPath.Length == 0 && (job.Method != "Direct" || File.Exists(job.Destination + "." + job.Id + ".part")))
                throw new IOException("Temporary file ownership is unknown. Inspect the recorded path manually; no files were deleted.");
            if (job.StagingPath.Length > 0)
            {
                var paths = new[] { job.StagingPath, job.StagingPath + ".crdownload", Path.Combine(Path.GetDirectoryName(job.StagingPath)!, ".owner") };
                var protectedPaths = Settings.Library.SelectMany(item => new[] { item.DownloadPath, item.ContentPath, item.InstalledGame?.Directory ?? "" }.Concat(item.AdditionalPaths))
                    .Concat(Settings.Downloads.Where(CompletedHistory).Select(record => record.Destination)).Where(path => path.Length > 0);
                if (paths.Any(path => InstalledGamesRoot.Length > 0 && ImageFileSystem.IsWithin(InstalledGamesRoot, path) || protectedPaths.Any(protectedPath =>
                    ImageFileSystem.SamePath(protectedPath, path) || Directory.Exists(protectedPath) && ImageFileSystem.IsWithin(protectedPath, path))))
                    throw new IOException("Staging path is registered library content; deletion refused.");
                job.CleanupPending = true;
                Save(); // Record intent before any deletion; keep it if the subsequent clear cannot be saved.
                await Task.Run(() => HttpResume.DiscardPartial(job));
            }
            if (clearHistory) ClearDownloadRecord(job);
            else
            {
                job.HttpCheckpoint = null; job.CleanupPending = false; job.CanResume = job.CanPause = false;
                job.State = "Failed"; job.DownloadedBytes = 0; job.Error = "A fresh provider link is needed. Choose a download from the store page.";
                Save();
            }
            admission.Release(job);
            Status = "Incomplete download discarded; completed files and library entries preserved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            job.IsCleared = false;
            if (!CompletedHistory(job)) job.State = "Cleanup failed";
            job.Error = "Discard failed: " + ex.Message + " Keep this record or retry Discard incomplete.";
            Status = job.Error;
            try { Save(); } catch { Status += " The record could not be saved; the last persisted record remains recoverable."; }
            Changed(nameof(Downloads));
        }
        finally { IsClearingDownloads = false; }
    }

    public Task ClearAllDownloadsAsync()
    {
        if (IsClearingDownloads) return Task.CompletedTask;
        IsClearingDownloads = true;
        try
        {
            foreach (var job in Downloads.Where(CanClearDownload).ToArray()) ClearDownloadRecord(job);
            Status = "Completed history cleared; active and incomplete downloads retained. Select an incomplete record to discard it.";
        }
        finally { IsClearingDownloads = false; }
        return Task.CompletedTask;
    }
    public async Task ExtractAsync(LocalLibraryEntry item)
    {
        if (IsClearingDownloads || IsExtracting || IsInstalling) throw new IOException("Wait for the current scan, extraction or installation to finish.");
        if (IsMovingContent) throw new IOException("Wait for the runnable content move to finish.");
        if (item.DeletionTargets is not null) throw new IOException("Deletion needs attention. Retry Delete before extraction.");
        if (item.AdditionalPaths.Count > 0) throw new InvalidOperationException("Multipart assembly and extraction are not supported by this version.");
        if (!IsExtractableArchive(item.ContentPath)) throw new InvalidOperationException("Select a ZIP or RAR archive.");
        if (SourceInUse(item.ContentPath)) throw new IOException("An active or incomplete download owns this archive.");
        var archiveInstallation = BeginArchiveInstallation(item);
        ActiveExtraction = item;
        IsExtracting = true; ExtractionPercentage = 0; ExtractionFeedback = $"Extracting {item.Title}…";
        item.Extraction = "Extracting"; Changed(nameof(IsExtracting)); Changed(nameof(Library));
        foreach (var job in Settings.Downloads.Where(job => item.DownloadArtifacts.Any(a => a.JobId == job.Id))) job.ArchiveStatus = "";
        try
        {
            Save();
            var destination = UniqueDirectory(Path.GetDirectoryName(item.ContentPath)!, Path.GetFileNameWithoutExtension(item.ContentPath) + "-extracted");
            var progress = new Progress<double>(value => { ExtractionPercentage = value; });
            // Resolve only recorded source/entry/artifact identities, never infer passwords from filenames.
            var job = Settings.Downloads.FirstOrDefault(job => item.DownloadArtifacts.Any(a => a.JobId == job.Id));
            var supplied = job is null ? null : Entries.SingleOrDefault(entry => entry.SourceId == job.SourceId && entry.Id == job.EntryId)
                ?.Artifacts.SingleOrDefault(artifact => artifact.Id == job.ArtifactId)?.ArchivePassword;
            var passwords = ArchiveExtraction.Candidates(supplied, Settings.ArchivePasswords);
            using (var sourceLease = new InstallationSource(item.ContentPath))
            {
                if (job is not null)
                    await DownloadCompletion.VerifyScannedArchiveAsync(Settings, job, sourceLease.Stream, store.Save, catalogueLifetime.Token,
                        bytes => { ExtractionFeedback = $"Verifying original archive {bytes:N0} / {sourceLease.Stream.Length:N0} bytes"; });
                await Task.Run(() => ArchiveExtraction.ExtractAsync(item.ContentPath, destination, passwords, catalogueLifetime.Token, progress));
            }
            item.ContentPath = destination; // Move the whole extraction, including wrappers and companion files.
            item.Extraction = "Extracted"; item.Classification = FileInspection.Classify(item.ContentPath);
            ExtractionFeedback = $"Extracted {item.Title}. Checking files…";
        }
        catch (Exception ex) when (ArchivePasswordException.IsPasswordFailure(ex))
        {
            FinishArchiveInstallation(archiveInstallation, item, ex);
            item.Extraction = "Password protected";
            foreach (var job in Settings.Downloads.Where(job => item.DownloadArtifacts.Any(a => a.JobId == job.Id) || ImageFileSystem.SamePath(item.DownloadPath, job.Destination)))
                job.ArchiveStatus = "Password protected";
            ExtractionFeedback = Status = "Password protected";
            Changed(nameof(Downloads));
            return;
        }
        catch (Exception ex) { FinishArchiveInstallation(archiveInstallation, item, ex); item.Extraction = "Extraction failed";
            foreach (var job in Settings.Downloads.Where(job => item.DownloadArtifacts.Any(a => a.JobId == job.Id))) job.ArchiveStatus = "Extraction failed";
            Changed(nameof(Downloads)); ExtractionFeedback = Status = "Extraction failed: " + ex.Message; throw; }
        finally { IsExtracting = false; ActiveExtraction = null; Changed(nameof(IsExtracting)); Changed(nameof(Library)); Changed(nameof(CanRetrySelectedDownload)); Save(); }
        await CheckExtractedDownloadAsync(item);
    }

    public Task ExtractSelectedDownloadAsync()
    {
        var job = SelectedJob ?? throw new IOException("Select a completed ZIP or RAR download.");
        if (!CanExtractDownload) throw new IOException("Select a complete, unowned ZIP or RAR download.");
        var item = Settings.Library.SingleOrDefault(item => item.DownloadArtifacts.Any(a => a.JobId == job.Id) || ImageFileSystem.SamePath(item.DownloadPath, job.Destination))
            ?? throw new IOException("The original download registration is unavailable; preserve the file for manual inspection.");
        return ExtractAsync(item);
    }

    public async Task CheckExtractedDownloadAsync(LocalLibraryEntry? item = null)
    {
        item ??= DownloadLibraryItem ?? throw new IOException("Select an extracted download.");
        if (IsExtracting || IsMovingContent || IsInstalling || IsDeletingLibraryEntry) throw new IOException("Wait for the current operation to finish.");
        if (item.Extraction != "Extracted") throw new IOException("Extract this archive first.");
        var archiveInstallation = BeginArchiveInstallation(item);
        string result;
        try
        {
            if (!Directory.Exists(item.ContentPath)) throw new InvalidDataException("The extracted folder is missing.");
            await PromoteRunnableDownloadsAsync(item);
            if (contentMoveStopRequested) { FinishArchiveInstallation(archiveInstallation, item, new OperationCanceledException("Installation cancelled; files preserved.")); return; }
            if (item.ContentMove is { Phase: "Completed" })
            {
                BeginContentMove(); NotifyInstallActions();
                try { await InstalledArchiveCleanup.CompleteAsync(Settings, item, store, DeletionDownloadActive, contentMoveCancellation!.Token, ContentProgress(item)); }
                catch (OperationCanceledException ex) when (contentMoveStopRequested) { FinishArchiveInstallation(archiveInstallation, item, ex); Status = "Archive cleanup cancelled; source preserved."; return; }
                finally { EndContentMove(); NotifyInstallActions(); }
            }
            SyncDownloadLibrary();
            FinishArchiveInstallation(archiveInstallation, item, item.ContentMove is { Phase: "Completed" } ? null : new IOException("Files remain in Downloads; installation did not finish."));
            result = item.ContentMove is { Phase: "Completed" } ? $"{item.Title} is now in My Library." :
                $"Extracted {item.Title}, but it remains in Downloads. {Status}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            FinishArchiveInstallation(archiveInstallation, item, ex);
            if (item.ContentMove is { Phase: "Completed" })
            {
                foreach (var download in Downloads.Where(job => item.DownloadArtifacts.Any(a => a.JobId == job.Id))) download.ArchiveStatus = "Archive cleanup needs attention";
                result = $"{item.Title} is in My Library. Archive cleanup needs attention: {ex.Message}";
            }
            else
            {
                item.Classification = "Cannot install: " + ex.Message;
                result = $"Extracted {item.Title}. Files remain in Downloads; see the check result above.";
            }
        }
        await RefreshFolderActionsAsync(item);
        ExtractionFeedback = Status = result; Changed(nameof(Library)); Save();
    }

    public async Task<int> PromoteRunnableDownloadsAsync(LocalLibraryEntry? selected = null)
    {
        if (disposed || IsExtracting || IsMovingContent || IsInstalling || IsDeletingLibraryEntry) return 0;
        BeginContentMove(); NotifyInstallActions(); Changed(nameof(CanExtractDownload));
        var moved = 0; var failures = new List<string>();
        try
        {
            foreach (var item in Settings.Library.Where(item => selected is null || item.Id == selected.Id).ToArray())
            {
                contentMoveCancellation!.Token.ThrowIfCancellationRequested();
                try { if (await RunnableContentMove.MoveAsync(Settings, item, store.Save, DeletionDownloadActive, contentMoveCancellation.Token, ContentProgress(item))) moved++; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { failures.Add(item.Title + ": " + ex.Message); }
            }
            Status = failures.Count > 0 ? "Some content remains in Downloads: " + string.Join("; ", failures) :
                moved > 0 ? $"Moved {moved} game item(s) into Installed. Launch compatibility will be determined by KytyPS5." : "No completed game folders or ELF files are available to move into Installed.";
            return moved;
        }
        catch (OperationCanceledException) when (contentMoveStopRequested)
        { Status = "Content verification cancelled; files preserved. Scan downloads to retry."; return moved; }
        finally
        {
            EndContentMove(); SyncDownloadLibrary(); Changed(nameof(Downloads)); Changed(nameof(Library));
            NotifyInstallActions(); Changed(nameof(CanExtractDownload));
        }
    }
    private string[] DeletionProtectedRoots => new[] { store.DirectoryPath, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Settings.EmulatorPath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(Settings.EmulatorPath))! : "" }.Where(path => path.Length > 0).ToArray();
    private bool DeletionDownloadActive(DownloadJob job) => BrowserDownloads.IsActive(job) || directTransfers.ContainsKey(job.Id) || restartingJobs.Contains(job.Id);
    public List<LibraryDeletionTarget> PrepareLibraryDeletion(LocalLibraryEntry item)
    {
        if (IsInstalling || IsDeletingLibraryEntry || item.Extraction == "Extracting")
            throw new InvalidOperationException("Wait for the active image operation, deletion or extraction before deleting an item.");
        return LibraryDeletion.Prepare(Settings, item, DeletionProtectedRoots, DeletionDownloadActive);
    }
    public void DeleteLibraryEntry(LocalLibraryEntry item, List<LibraryDeletionTarget> confirmed)
    {
        _ = PrepareLibraryDeletion(item);
        isDeletingLibraryEntry = true; Changed(nameof(IsDeletingLibraryEntry)); NotifyInstallActions();
        try
        {
            LibraryDeletion.Delete(Settings, item, confirmed, DeletionProtectedRoots, DeletionDownloadActive, store.Save, LibraryRecycleBin.Recycle);
            foreach (var download in Downloads.Where(download => download.IsCleared).ToArray())
            {
                Downloads.Remove(download);
                if (SelectedJob == download) SelectedJob = null;
            }
            var clearSelection = SelectedLibraryEntry == item;
            Library.Remove(item);
            foreach (var installation in Installations.Where(job => !job.Registered && !job.Verified &&
                (ImageFileSystem.SamePath(job.SourcePath, item.DownloadPath) || ImageFileSystem.SamePath(job.SourcePath, item.ContentPath) ||
                 item.DownloadArtifacts.Any(a => ImageFileSystem.SamePath(a.Destination, job.SourcePath)))))
            { installation.IsCleared = true; installationJournal.Save(installation); }
            Changed(nameof(Installations));
            if (clearSelection) SelectedLibraryEntry = null;
            Status = $"Deleted {item.Title} and its local files from My Library.";
        }
        catch (Exception ex)
        {
            if (item.DeletionTargets is not null) item.Classification = "Deletion needs attention — retry Delete";
            throw new IOException("Deletion did not finish. The library entry was kept; use Delete again to retry the remaining paths. " + ex.Message, ex);
        }
        finally
        {
            isDeletingLibraryEntry = false; Changed(nameof(IsDeletingLibraryEntry)); Changed(nameof(Library)); Changed(nameof(Downloads)); NotifyInstallActions();
        }
    }
    public void AddLocal(string path)
    {
        if (!Path.IsPathFullyQualified(InstalledGamesRoot) || !ImageFileSystem.IsWithin(InstalledGamesRoot, path))
            throw new IOException("My Library only accepts content inside the configured Installed directory. Use Downloads for content awaiting installation.");
        if (activeInstallation is { } active && ImageFileSystem.SamePath(active.SourcePath, path)) throw new IOException("An installation owns this image. Wait for it to finish.");
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Local content is missing.", path);
        if (Library.Any(x => x.ContentPath.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new IOException("This path is already in the library.");
        var item = new LocalLibraryEntry { Title = Path.GetFileName(path), ContentPath = path, Classification = FileInspection.Classify(path), Extraction = "Existing local content" };
        Library.Add(item); Settings.Library.Add(item); SelectedLibraryEntry = item; Save();
    }
    public async Task ReinspectAsync(LocalLibraryEntry item)
    {
        if (IsMovingContent) throw new IOException("Wait for the runnable content move to finish.");
        if (item.PackageInstallations.Count > 0)
        {
            if (IsInstalling || IsDeletingLibraryEntry) throw new IOException("Wait for the active file operation.");
            try
            {
                await LibraryPackageFolder.VerifyAsync(item, Installations.ToArray(), catalogueLifetime.Token);
                foreach (var package in item.PackageInstallations) package.LaunchEligible = true;
                item.Integrity = "Installed content SHA-256 verified";
            }
            catch
            {
                foreach (var package in item.PackageInstallations) package.LaunchEligible = false;
                item.Integrity = "Installed file verification failed"; throw;
            }
            finally { await RefreshFolderActionsAsync(item); Save(); }
            return;
        }
        if (item.ContentMove is { } move)
        {
            if (move.Phase != "Completed") throw new IOException("This content move needs reconciliation; files were preserved.");
            await RunnableContentMove.VerifyAsync(move, item.ContentPath, CancellationToken.None);
            item.Integrity = "Installed content SHA-256 verified"; await RefreshFolderActionsAsync(item); Changed(nameof(Library)); Save(); return;
        }
        if (item.DeletionTargets is not null) throw new IOException("Deletion needs attention. Retry Delete before reinspection.");
        if (activeInstallation is { } active && (item.InstalledGame?.InstallationId == active.Id || ImageFileSystem.SamePath(item.ContentPath, active.SourcePath)))
            throw new IOException("Wait for the active installation before reinspection.");
        if (item.InstalledGame is { } game)
        {
            if (IsInstalling) throw new InvalidOperationException("Finish the active image operation before reinspecting an installed game.");
            var installation = Installations.FirstOrDefault(job => job.Id == game.InstallationId) ?? throw new IOException("Installation verification manifest is missing.");
            installationCancellation = new(); activeInstallation = installation; NotifyInstallActions();
            var progress = ProgressFor(installation, "Reinspecting");
            try
            {
                await ImageFileSystem.VerifyFinalAsync(game.Directory, installation, installationCancellation.Token, progress);
                game.Verification = item.Integrity = "All extracted files SHA-256 verified"; game.LaunchEligible = true;
                item.Classification = "Installed — Ready for launch attempt"; Status = "Installed files fully verified";
            }
            catch { game.Verification = item.Integrity = "Installed file verification failed"; game.LaunchEligible = false; item.Classification = "Needs attention"; throw; }
            finally
            {
                installationCancellation.Dispose(); installationCancellation = null; activeInstallation = null; NotifyInstallActions();
                Save(); Changed(nameof(Library));
            }
            installation.Registered = InstalledLibrary.IsRegistered(store.Load(), installation);
            installationJournal.Save(installation); Changed(nameof(Installations));
            progress.Report(new(InstallationState.Completed, "Installed files fully verified", installation.TotalBytes, installation.TotalBytes, 0, TimeSpan.Zero));
            return;
        }
        item.Classification = item.AdditionalPaths.Count > 0 ? "Multiple files — assembly unsupported" : FileInspection.Classify(item.ContentPath);
        var paths = new[] { item.DownloadPath }.Concat(item.AdditionalPaths).Where(path => path.Length > 0).ToArray();
        if (paths.Any(path => !File.Exists(path))) { item.Integrity = "Missing download"; item.Classification = "Missing local content"; }
        else if (paths.Length > 0)
        {
            var checkedHashes = 0;
            foreach (var path in paths)
            {
                var job = Settings.Downloads.LastOrDefault(j => j.Destination.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (job?.ExpectedSha256.Length != 64) continue;
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file)).ToLowerInvariant();
                job.ActualSha256 = actual;
                if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    item.Integrity = "Integrity failed"; item.Classification = "Integrity failed"; job.State = "Integrity failed";
                    Status = "SHA-256 mismatch: " + path; Save(); Changed(nameof(Library)); Changed(nameof(Downloads)); return;
                }
                job.State = "Downloaded"; job.Error = ""; job.DownloadedBytes = new FileInfo(path).Length; checkedHashes++;
            }
            item.Integrity = checkedHashes == paths.Length ? "SHA-256 verified on reinspection" : "Some files lack expected checksums";
        }
        Status = "Reinspection complete";
        await RefreshFolderActionsAsync(item);
        Changed(nameof(Library)); Save();
    }
    public void Launch(LocalLibraryEntry item, string? selectedPath = null, EmulatorBackend? emulator = null)
    {
        if (IsUpdatingSharpEmu) throw new IOException("Wait for the SharpEmu update to finish before launching.");
        if (IsUpdatingKyty) throw new IOException("Wait for the KytyPS5 update to finish before launching.");
        if (IsInstalling) throw new IOException("Wait for installation and cleanup to finish before launching.");
        if (!IsInstalledLibraryItem(item)) throw new IOException("Launch from the configured Installed directory in My Library.");
        if (IsMovingContent || item.ContentMove is { Phase: not "Completed" }) throw new IOException("Wait for the content move to complete before launching.");
        if (item.DeletionTargets is not null || IsDeletingLibraryEntry) throw new IOException("Deletion needs attention. Retry Delete before launching this item.");
        if (item.InstalledGame?.InstallationId == activeInstallation?.Id && item.InstalledGame is not null) throw new InvalidOperationException("Wait for installation and cleanup to finish before launching this game.");
        if (item.InstalledGame is { LaunchEligible: false }) throw new InvalidOperationException("Reinspect the installed game before a launch attempt.");
        var targets = Directory.Exists(item.ContentPath) ? item.LaunchPaths : [item.ContentPath];
        if (selectedPath is null && targets.Count != 1) throw new IOException("Choose one extracted game folder to launch.");
        var path = selectedPath ?? targets[0];
        if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase) || !ImageFileSystem.SamePath(item.ContentPath, path) && !ImageFileSystem.IsWithin(item.ContentPath, path))
            throw new IOException("Choose a launchable folder belonging to this library item.");
        ImageFileSystem.EnsureNoLinks(path);
        var backend = emulator ?? item.LastEmulator;
        var executable = backend == EmulatorBackend.SharpEmu ? SharpEmuPath : EmulatorPath;
        var launch = launcher.Launch(executable, path, LaunchLogDirectory, id =>
        {
            var previous = item.LastLaunchLogId;
            item.LastLaunchLogId = id;
            try { Save(); } catch { item.LastLaunchLogId = previous; throw; }
            Changed(nameof(CanViewSelectedLaunchLog)); Changed(nameof(Library));
        }, Settings.KytyArguments, backend, Settings.SharpEmuFullscreen);
        GameStarted?.Invoke(item, backend);
        // Start returned a real child process; cancellation/validation/start failures never reach this write.
        var previousEmulator = item.LastEmulator;
        item.LastEmulator = backend;
        try { Save(); } catch { item.LastEmulator = previousEmulator; _ = ObserveGameExitAsync(launch, item, backend); throw; }
        Status = $"{backend} process started for {item.Title}; console output is being saved. Use View log to review it.";
        NotifySharpEmu();
        _ = ObserveGameExitAsync(launch, item, backend);
    }
    public event Action<LocalLibraryEntry, EmulatorBackend>? GameStarted;
    public event Action<LocalLibraryEntry>? GameExited;
    public event Action<EmulatorBackend>? GameProcessExited;
    private async Task ObserveGameExitAsync(LoggedLaunch launch, LocalLibraryEntry item, EmulatorBackend backend)
    {
        int exitCode;
        try { exitCode = await launch.Completion.ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (!ui.HasShutdownStarted)
                await ui.InvokeAsync(() => { if (!disposed) Status = "Could not monitor " + backend + ": " + ex.Message; });
            return;
        }
        if (ui.HasShutdownStarted) return;
        await ui.InvokeAsync(() =>
        {
            if (disposed) return;
            if (Library.Contains(item)) SelectedLibraryEntry = item;
            Status = exitCode == 0 ? $"Returned to My Library from {item.Title}." :
                $"{backend} exited for {item.Title} (code {exitCode}). Use View log for details.";
            Changed(nameof(Library)); NotifySharpEmu();
            GameProcessExited?.Invoke(backend);
            GameExited?.Invoke(item);
        });
    }
    private string LaunchLogDirectory => Path.Combine(store.DirectoryPath, "launch-logs");
    internal bool CanViewLaunchLog(LocalLibraryEntry item) => KytyLaunchLog.Exists(LaunchLogDirectory, item.LastLaunchLogId);
    public bool CanViewSelectedLaunchLog => SelectedLibraryEntry is { } item && CanViewLaunchLog(item);
    internal string ReadLaunchLog(LocalLibraryEntry item)
    {
        if (!Library.Contains(item)) throw new IOException("This library item is no longer available.");
        return KytyLaunchLog.Read(LaunchLogDirectory, item.LastLaunchLogId);
    }
    private void Save() { if (!disposed) store.Save(Settings); }
    private string UniquePath(string directory, string name, IEnumerable<DownloadJob>? pending = null)
    {
        var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        for (var n = 0; n < 10000; n++)
        {
            var path = Path.Combine(directory, n == 0 ? name : $"{stem} ({n}){extension}");
            if (!File.Exists(path) && !Directory.Exists(path) && !reservedPaths.Contains(path) && !Downloads.Concat(pending ?? []).Any(j => j.Destination.Equals(path, StringComparison.OrdinalIgnoreCase))) return path;
        }
        throw new IOException("No available filename.");
    }
    private static string UniqueDirectory(string parent, string name)
    {
        for (var n = 0; n < 10000; n++)
        {
            var path = Path.Combine(parent, n == 0 ? name : $"{name} ({n})");
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
        }
        throw new IOException("No available extraction folder.");
    }
    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0) name = "download.bin";
        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(name), "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) name = "_" + name;
        if (name.Length > 160) name = name[..140] + Path.GetExtension(name);
        return name;
    }
    public void Dispose()
    {
        if (HasDirectTransfers || HasDirectMaintenance) throw new InvalidOperationException("Stop and join direct HTTP writers before disposing the application.");
        disposed = true;
        CancelAllInstallations();
        installedInventoryTimer.Stop(); installedWatcher?.Dispose();
        orbitRefreshTimer.Stop(); catalogueLifetime.Cancel(); catalogueLifetime.Dispose(); orbitHttp.Dispose(); publishedKeys.Dispose();
        BrowserDownloads.Dispose();
        http.Dispose(); downloadGate.Dispose();
        settingsOwnership.Dispose();
    }
}
