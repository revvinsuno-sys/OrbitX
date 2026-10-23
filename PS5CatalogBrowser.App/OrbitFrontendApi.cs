using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

internal sealed partial class OrbitFrontendApi : IDisposable
{
    internal event Action<OrbitFrontendAction>? ActionRequested;
    private readonly MainViewModel model;
    private OrbitFrontendSnapshot? snapshot;
    private byte[]? catalogJson;
    private readonly IgdbMediaLookup mediaLookup = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    public OrbitFrontendApi(MainViewModel model)
    {
        this.model = model; model.Entries.CollectionChanged += Invalidate; model.Sources.CollectionChanged += Invalidate;
        model.PropertyChanged += ModelChanged;
    }
    private void Invalidate(object? sender, NotifyCollectionChangedEventArgs args) { snapshot = null; catalogJson = null; }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(MainViewModel.Sources) or nameof(MainViewModel.Compatibility) or nameof(MainViewModel.OfficialSharpEmuCompatibility) or nameof(MainViewModel.Library)) { snapshot = null; catalogJson = null; } }
    internal OrbitFrontendSnapshot Catalog => snapshot ??= OrbitFrontendCatalog.Create(model.Entries, model.Sources, model.Compatibility, model.Settings.CompatibilityOverrides,
        model.Settings.CompatibilityTestVersions, model.CurrentKytyVersion, model.OfficialSharpEmuCompatibility, model.Settings.SharpEmuCompatibilityOverrides, model.Settings.SharpEmuCompatibilityTestVersions);
    internal byte[] SerializeResponse(object value) => ReferenceEquals(value, snapshot?.Releases)
        ? catalogJson ??= JsonSerializer.SerializeToUtf8Bytes(value, CatalogJson)
        : JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal async Task<(int Status, object Body)> RespondMediaAsync(string document, string method, string url, string? body, string? origin)
    {
        if (!OrbitFrontendSecurity.IsDocument(document) || !OrbitFrontendSecurity.IsOrigin(url) || origin is not (null or "") && origin != OrbitFrontendSecurity.Origin)
            return (403, new { error = "Untrusted frontend request." });
        var uri = new Uri(url);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return (400, new { error = "Unexpected API arguments." });
        if (method != "POST" || uri.AbsolutePath != "/api/v1/media") return (404, new { error = "Endpoint unavailable." });
        try
        {
            using var request = JsonDocument.Parse(body ?? "{}", new JsonDocumentOptions { MaxDepth = 4 });
            if (request.RootElement.ValueKind != JsonValueKind.Object || !request.RootElement.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
                return (400, new { error = "A game title is required." });
            var secret = "";
            try { if (model.Settings.IgdbClientSecretProtected.Length > 0) secret = IgdbCredentialProtection.Unprotect(model.Settings.IgdbClientSecretProtected); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { }
            return (200, await mediaLookup.LookupAsync(title.GetString()!, model.Settings.IgdbClientId, secret).ConfigureAwait(false));
        }
        catch (JsonException) { return (400, new { error = "The media request was invalid." }); }
    }
    internal (int Status, object Body) Respond(string document, string method, string url, string? body = null, string? origin = null)
    {
        if (!OrbitFrontendSecurity.IsDocument(document) || !OrbitFrontendSecurity.IsOrigin(url) || origin is not (null or "") && origin != OrbitFrontendSecurity.Origin)
            return (403, new { error = "Untrusted frontend request." });
        var uri = new Uri(url);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return (400, new { error = "Unexpected API arguments." });
        var path = uri.AbsolutePath;
        if (method == "POST" && path == "/api/v1/windows/settings") return SettingsRequest(body);
        if (method == "POST" && path == "/api/v1/windows/igdb-settings") return SaveIgdbSettings(body);
        if (method == "POST" && path is "/api/v1/windows/library/relink/search" or "/api/v1/windows/library/relink/link") return LibraryRelinkRequest(path, body);
        if (method == "POST" && path == "/api/v1/favorites") return Favorite(body);
        if (method == "POST" && path is "/api/v1/windows/log" or "/api/v1/windows/details" or "/api/v1/windows/compatibility" or "/api/v1/windows/delete/prepare" or "/api/v1/windows/delete/confirm") return LibraryRequest(path, body);
        if (method == "POST" && path is "/api/v1/windows/action" or "/api/v1/windows/release") return WindowsRequest(path, body);
        if (method != "GET") return (405, new { error = "Operation unavailable." });
        var catalog = Catalog;
        object? result = path switch
        {
            "/api/v1/system" => new { startupLoading = model.StartupLoading, catalogueRevision = catalog.Revision, name = "OrbitX", version = typeof(OrbitFrontendApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown", platform = "desktop", paired = true,
                stateHealthy = true, preferredStorage = "", consoleValidated = false, targetFirmware = "", localSessionAvailable = false,
                pairNotificationAvailable = false, httpPort = 0, launcherStatus = "not-included", concurrentDownloads = 1 },
            "/api/v1/sources" => new { enabled = catalog.Sources.Select(source => source.Id), acknowledged = true, noticeVersion = 1, options = catalog.Sources },
            "/api/v1/catalog" => catalog.Releases,
            "/api/v1/favorites" => model.Settings.OrbitFavorites,
            "/api/v1/downloads" => Queue(),
            "/api/v1/storage" => Array.Empty<object>(),
            "/api/v1/browser" => new { available = false, active = false, id = "", title = "", state = "idle", message = "Provider pages open in the Windows download browser.", jobId = "" },
            "/api/v1/library" => new { status = "ready", message = "", provider = "Windows", providerVersion = "1", busy = false, stale = false,
                checkedAt = 0, updatedAt = 0, refreshAfter = 60, games = Array.Empty<object>(), capabilities = Array.Empty<string>(), action = (object?)null, storageJob = (object?)null, storageError = "", storageBusy = false },
            "/api/v1/library/storage" => new { drives = Array.Empty<object>(), destinations = Array.Empty<object>(), busy = false, stale = false, updatedAt = 0, error = "Use Windows Settings." },
            "/api/v1/debrid" => new { provider = "torbox", available = false, connected = false, busy = false, error = "", checkedAt = 0, retryAt = 0, revision = 0, hosts = Array.Empty<object>() },
            "/api/v1/windows/status" => new { issues = catalog.Issues, downloadCount = model.Downloads.Count, libraryCount = model.LibraryView.Cast<object>().Count() },
            "/api/v1/windows/state" => WindowsState(),
            "/api/v1/windows/settings" => SettingsState(),
            "/api/v1/windows/igdb-settings" => new { clientId = model.Settings.IgdbClientId, hasClientSecret = model.Settings.IgdbClientSecretProtected.Length > 0 },
            _ => null
        };
        return result is null ? (404, new { error = "Endpoint unavailable." }) : (200, result);
    }
    private object Queue() => model.Downloads.Select((job, index) =>
    {
        var matches = Catalog.Releases.Where(release => release.WindowsIdentity.SourceId == job.SourceId && release.WindowsIdentity.EntryId == job.EntryId &&
            release.WindowsIdentity.ArtifactId == job.ArtifactId && (job.OrbitRelease is null || release.WindowsIdentity.Platform == job.OrbitRelease.Platform)).Take(2).ToArray();
        var release = matches.Length == 1 ? matches[0] : null;
        var status = job.State switch { "Downloaded" or "Imported" or "Installed" => "complete", "Queued" => "queued", "Paused" => "paused", "Cancelled" => "cancelled",
            "Downloading" => "downloading", "Checking integrity" => "verifying", _ => "error" };
        return new { job.Id, gameId = release?.GameId ?? "", releaseId = release?.Id ?? "", titleId = release?.TitleId ?? "", format = release?.Format ?? "",
            path = "", job.Title, order = index, storageId = "windows", filename = job.FileName, status, received = job.DownloadedBytes,
            total = job.TotalBytes ?? 0, speed = job.BytesPerSecond, retryAt = 0, job.Error, verification = "" };
    }).ToArray();
    internal (CatalogEntry Entry, DownloadMirror Mirror)? ResolveRelease(string id)
    {
        var identity = Catalog.Releases.FirstOrDefault(release => release.Id == id && release.WindowsAvailable)?.WindowsIdentity;
        if (identity is null) return null;
        var entries = model.Entries.Where(entry => entry.SourceId == identity.SourceId && entry.Id == identity.EntryId && entry.Platform == identity.Platform &&
            model.Sources.Any(source => source.Id == entry.SourceId && source.Enabled)).Take(2).ToArray();
        if (entries.Length != 1 || entries[0].Artifacts.Count(artifact => artifact.Id == identity.ArtifactId) != 1) return null;
        var mirror = DownloadMirror.ForEntry(entries[0]).FirstOrDefault(choice => choice.Artifact.Id == identity.ArtifactId);
        return mirror is null ? null : (entries[0], mirror);
    }
    internal string DownloadSearchTitle(DownloadJob job) => model.DownloadMetadata(job).Title;
    internal string? StoreGameId(DownloadJob job)
    {
        var ids = Catalog.Releases.Where(release => release.WindowsIdentity.SourceId == job.SourceId && release.WindowsIdentity.EntryId == job.EntryId &&
            (job.OrbitRelease is null || release.Platform.Equals(job.OrbitRelease.Platform, StringComparison.OrdinalIgnoreCase))).Select(release => release.GameId).Distinct().Take(2).ToArray();
        // A refreshed/re-added source can have a new local identity. The original
        // Orbit game ID and platform were captured when this download was added.
        if (ids.Length == 0 && job.OrbitRelease is { GameId.Length: > 0 } saved)
        {
            var entries = model.Entries.Where(entry => entry.GameId == saved.GameId && entry.Platform.Equals(saved.Platform, StringComparison.OrdinalIgnoreCase)).ToArray();
            ids = Catalog.Releases.Where(release => entries.Any(entry => entry.SourceId == release.WindowsIdentity.SourceId && entry.Id == release.WindowsIdentity.EntryId))
                .Select(release => release.GameId).Distinct().Take(2).ToArray();
        }
        if (ids.Length == 0)
        {
            var entries = model.DownloadCatalogEntries(job);
            ids = Catalog.Releases.Where(release => entries.Any(entry => entry.SourceId == release.WindowsIdentity.SourceId && entry.Id == release.WindowsIdentity.EntryId))
                .Select(release => release.GameId).Distinct().Take(2).ToArray();
        }
        return ids.Length == 1 ? ids[0] : null;
    }
    private static string[] ReleaseActions((CatalogEntry Entry, DownloadMirror Mirror) release) =>
        (release.Mirror.DirectEnabled ? new[] { "download" } : []).Concat(release.Mirror.BrowserEnabled ? ["browser"] : []).Concat(["external", "import"])
        .Concat(release.Entry.DownloadAllEnabled && release.Entry.CatalogFormat != "OptiStore" && release.Entry.Artifacts.Count > 1 ? ["downloadAll"] : []).ToArray();
    internal string[] DownloadActions(DownloadJob job) => new[] {
        "findDownloadGame",
        model.BrowserDownloads.CanCheckStatus(job.Id) ? "checkStatus" : null,
        job.CanPause ? "pause" : null, job.CanResume ? "resume" : null, model.CanCancelDownload(job) ? "cancel" : null,
        model.CanRetryDownload(job) ? "retryDownload" : null, model.CanRestartDownload(job) && !model.CanRetryDownload(job) ? "restart" : null, model.CanDiscardDownload(job) && !model.CanDeleteDownload(job) ? "discard" : null,
        model.CanClearDownload(job) ? "clearDownload" : null, model.CanRunDownload(job) ? "installDownload" : null,
        model.CanDeleteDownload(job) ? "deleteDownload" : null
    }.OfType<string>().ToArray();
    internal string[] LibraryActions(LocalLibraryEntry item) => new[] {
        "openFolder", "reinspect", "relink", !model.IsClearingDownloads && !model.IsExtracting && Path.GetExtension(item.ContentPath).ToLowerInvariant() is ".zip" or ".rar" ? "extract" : null, model.CanInstallItem(item) ? "installLibrary" : null,
        model.CanDeleteItem(item) ? "deleteLibrary" : null, model.CanLaunchItem(item) ? "launch" : null, model.CanLaunchItem(item) ? "chooseEmulator" : null,
        item.InstalledGame is not null ? "libraryDetails" : null, model.CanViewLaunchLog(item) ? "viewLog" : null
    }.OfType<string>().ToArray();
    internal string[] InstallationActions(InstallationJob job) => new[] {
        "installationDetails", model.IsQueuedForInstallation(job.SourcePath) ? "cancelQueuedInstallation" : model.CanRetryInstallation(job) ? "retryInstallation" : null, model.CanDeleteInstallation(job) ? "deleteInstallation" : null, !model.IsInstalling && job.CanRetryCleanup ? "retryCleanup" : null
    }.OfType<string>().ToArray();
    internal string[] GlobalActions => new[] { "refreshDownloads", "settings", "refresh", "clearDownloads", "downloadsFolder", "addFolder", "addElf", model.CanScanDownloads ? "scanDownloads" : null,
        model.CanInstallFromFile ? "installFile" : null, model.IsInstalling ? "cancelInstallation" : null, model.IsMovingContent ? "cancelContentMove" : null, model.CanClearInstallations ? "clearInstallations" : null }.OfType<string>().ToArray();
    private object? ItemProgress(DownloadJob? download = null, LocalLibraryEntry? item = null)
    {
        if (model.ActiveContentMove is { } moving && (item?.Id == moving.Id || download is not null &&
            (moving.DownloadArtifacts.Any(artifact => artifact.JobId == download.Id) || ImageFileSystem.SamePath(moving.DownloadPath, download.Destination))))
            return new { id = moving.Id, kind = "Verification", text = model.ContentMoveProgressText, percentage = model.ContentMovePercentage, cancellable = true };
        if (model.ActiveExtraction is { } extraction && (item?.Id == extraction.Id || download is not null &&
            (extraction.DownloadArtifacts.Any(artifact => artifact.JobId == download.Id) || ImageFileSystem.SamePath(extraction.DownloadPath, download.Destination))))
            return new { id = extraction.Id, kind = "Extraction", text = model.ExtractionFeedback, percentage = model.ExtractionPercentage, cancellable = false };
        if (model.ActiveInstallation is { } installation && (download is not null && ImageFileSystem.SamePath(download.Destination, installation.SourcePath) ||
            item is not null && (item.Id == installation.ParentLibraryId || item.InstalledGame?.InstallationId == installation.Id || ImageFileSystem.SamePath(item.ContentPath, installation.SourcePath))))
            return InstallationProgress(installation);
        return null;
    }
    private object? InstallationProgress(InstallationJob job)
    {
        if (model.ActiveInstallation?.Id == job.Id)
            return new { id = job.Id, kind = "Installation", text = model.InstallationProgressText, percentage = model.InstallationPercentage, cancellable = true };
        if (model.ActiveExtraction is { } extraction && model.InstallationItem(job)?.Id == extraction.Id)
            return new { id = job.Id, kind = "Extraction", text = model.ExtractionFeedback, percentage = model.ExtractionPercentage, cancellable = false };
        if (model.ActiveContentMove is { } moving && model.InstallationItem(job)?.Id == moving.Id)
            return new { id = job.Id, kind = "Verification", text = model.ContentMoveProgressText, percentage = model.ContentMovePercentage, cancellable = true };
        return null;
    }
    private object? ItemFailure(DownloadJob? download = null, LocalLibraryEntry? item = null)
    {
        var latest = model.Installations.LastOrDefault(job => !job.IsCleared &&
            (download is not null && ImageFileSystem.SamePath(job.SourcePath, download.Destination) ||
             item is not null && (job.ParentLibraryId == item.Id || ImageFileSystem.SamePath(job.SourcePath, item.ContentPath))));
        if (latest is not { State: InstallationState.Failed } || latest.Error.Length == 0) return null;
        var overflow = latest.Error.Contains("overflow", StringComparison.OrdinalIgnoreCase);
        return new
        {
            label = overflow ? "Installation failed · arithmetic overflow" : "Installation failed",
            error = latest.Error,
            description = overflow ? "The package reader encountered a number outside its supported range. This can indicate unsupported package data, a damaged file, or a reader bug; it does not by itself prove the download is corrupt." : ""
        };
    }
    private object WindowsState() => new {
        status = model.Status, scanning = model.IsScanningDownloads, scanStatus = model.ScanStatus, downloadRoot = model.DownloadRoot, extraction = model.ExtractionFeedback, extractionPercentage = model.ExtractionPercentage,
        installation = model.InstallationAvailability, installationProgress = model.InstallationProgressText, installationPercentage = model.InstallationPercentage, actions = GlobalActions,
        emulators = new { kytyPS5 = new { available = model.EmulatorAvailability == "Executable found", availability = model.EmulatorAvailability },
            sharpEmu = new { available = model.SharpEmuAvailability == "Executable found", availability = model.SharpEmuAvailability },
            shadPS4 = new { available = model.ShadPS4Availability == "Executable found", availability = model.ShadPS4Availability } },
        downloads = model.Downloads.Where(model.ShowDownload).Select(job => {
            var metadata = model.DownloadMetadata(job);
            return new { job.Id, title = metadata.Title, job.State, job.Error, archiveStatus = metadata.Status, method = job.Method == "Scan" ? "Local file" : job.Method, job.DownloadedBytes, totalBytes = job.TotalBytes ?? job.ExpectedSize,
                job.Percentage, job.BytesPerSecond, progress = ItemProgress(download: job), failure = ItemFailure(download: job), fileName = Path.GetFileName(job.Destination), installLabel = model.DownloadActionFor(job), installationStatus = model.InstallationQueueStatus(job.Destination), actions = DownloadActions(job) };
        }).ToArray(),
        providerSessions = model.BrowserDownloads.Sessions.Where(session => session.Downloads.Count == 0)
            .Select(session => new { session.Id, session.Title, actions = new[] { "checkStatus" } }).ToArray(),
        library = model.LibraryView.Cast<LocalLibraryEntry>().Select(item => new { item.Id, title = model.LibraryDisplayTitle(item), item.Version, item.Integrity, item.Extraction, item.Classification,
            platform = model.LibraryCatalogEntry(item)?.Platform ?? (model.LibraryTitleId(item).StartsWith("CUSA", StringComparison.OrdinalIgnoreCase) ? "PS4" : "PS5"),
            installedBytes = item.InstalledGame?.Size is > 0 ? item.InstalledGame.Size : (long?)null,
            titleId = model.LibraryTitleId(item), cover = LibraryArt(item.CoverUrl, model.LibraryCatalogEntry(item)?.CoverUrl),
            coverFallback = LibraryArt(item.CoverFallbackUrl, model.LibraryCatalogEntry(item)?.CoverFallbackUrl),
            compatibility = model.LibraryCompatibility(item), compatibilityOverride = model.LibraryCompatibilityOverride(item),
            kytyLocalCompatibility = model.LibraryCompatibilityOverride(item),
            sharpEmuOfficialCompatibility = model.LibrarySharpEmuOfficialCompatibility(item),
            sharpEmuLocalCompatibility = model.LibrarySharpEmuCompatibilityOverride(item),
            sharpEmuCompatibilityVersion = model.LibrarySharpEmuTestVersion(item)?.Version ?? "",
            compatibilityVersion = model.LibraryTestVersion(item)?.Version ?? "", currentKytyVersion = model.CurrentKytyVersion,
            compatibilityVersionMatch = KytyVersion.Comparison(model.LibraryTestVersion(item)?.Version ?? "", model.CurrentKytyVersion, model.LibraryCompatibilityOverride(item)),
            websiteCompatibility = model.LibraryWebsiteCompatibility(item),
            websiteReports = model.LibraryWebsiteReports(item).Select(report => new { report.Status, report.TestedVersion, report.Os, report.TestedDate,
                versionMatch = KytyVersion.Comparison(report.TestedVersion, model.CurrentKytyVersion, report.Status) }).ToArray(),
            progress = ItemProgress(item: item), failure = ItemFailure(item: item), installLabel = model.ImageInstallActionLabel(item.ContentPath), installationStatus = model.InstallationQueueStatus(item.ContentPath), actions = LibraryActions(item) }).ToArray(),
        installations = model.VisibleInstallations.Select(job => new { job.Id,
            cardId = model.Settings.Downloads.FirstOrDefault(download => ImageFileSystem.SamePath(download.Destination, job.SourcePath))?.Id ?? job.Id,
            fileName = Path.GetFileName(job.SourcePath), job.Title, state = job.StateLabel, job.Error,
            job.TotalBytes, job.CleanupResult, installationStatus = model.InstallationQueueStatus(job.SourcePath), installLabel = model.IsInstalling ? "Queue Install" : "Retry installation", failure = ItemFailure(download: model.Settings.Downloads.FirstOrDefault(download => ImageFileSystem.SamePath(download.Destination, job.SourcePath))), progress = InstallationProgress(job), actions = InstallationActions(job) }).ToArray()
    };
    private (int Status, object Body) SaveIgdbSettings(string? body)
    {
        try
        {
            using var json = JsonDocument.Parse(body ?? "{}", new JsonDocumentOptions { MaxDepth = 2 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => property.Name is not ("clientId" or "clientSecret")) ||
                !root.TryGetProperty("clientId", out var id) || id.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("clientSecret", out var secret) || secret.ValueKind != JsonValueKind.String)
                return (400, new { error = "Enter an IGDB client ID and secret." });
            model.SaveIgdbCredentials(id.GetString()!, secret.GetString());
            return (200, new { clientId = model.Settings.IgdbClientId, hasClientSecret = model.Settings.IgdbClientSecretProtected.Length > 0 });
        }
        catch (JsonException) { return (400, new { error = "The IGDB settings request was invalid." }); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException)
        { return (400, new { error = ex.Message }); }
    }
    private (int Status, object Body) WindowsRequest(string path, string? body)
    {
        if (body is null || body.Length > 1024) return (400, new { error = "Invalid Windows request." });
        try
        {
            using var json = JsonDocument.Parse(body, new() { MaxDepth = 2 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (400, new { error = "Invalid request schema." });
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct().Count() != properties.Length || properties.Any(p => p.Name is not ("action" or "id" or "backend") || p.Value.ValueKind != JsonValueKind.String) ||
                !root.TryGetProperty("id", out var idValue) || idValue.GetString() is not { Length: <= 100 } id) return (400, new { error = "Invalid action identity." });
            if (path.EndsWith("/release", StringComparison.Ordinal))
            {
                if (properties.Length != 1) return (400, new { error = "Unexpected release arguments." });
                return ResolveRelease(id) is { } release ? (200, new { title = release.Entry.Title, provider = release.Mirror.Provider, filename = release.Mirror.ExpectedFileName,
                    version = release.Mirror.Artifact.Version, format = release.Mirror.Format, size = release.Mirror.Artifact.Size, actions = ReleaseActions(release),
                    instructions = new { download = "Download directly into your configured Windows Downloads folder. Follow progress on Downloads.", browser = release.Mirror.BrowserInstructions,
                        external = "Complete the download in your external browser, then return and choose Import completed file for this same release.",
                        import = "Choose the completed file matching this release. This app verifies it before registering it.", downloadAll = "Download the explicitly listed files for this catalogue entry. Follow each file on Downloads." } }) : (404, new { error = "That release is no longer available. Refresh the catalogue." });
            }
            if (!root.TryGetProperty("action", out var actionValue)) return (400, new { error = "Invalid action schema." });
            var action = actionValue.GetString()!;
            EmulatorBackend? backend = null;
            if (root.TryGetProperty("backend", out var backendValue))
            {
                if (action != "chooseEmulator" || properties.Length != 3 || !Enum.TryParse<EmulatorBackend>(backendValue.GetString(), true, out var parsed) || !Enum.IsDefined(parsed))
                    return (400, new { error = "Invalid emulator selection." });
                backend = parsed;
                var item = model.LibraryView.Cast<LocalLibraryEntry>().SingleOrDefault(entry => entry.Id == id);
                if (item is null) return (404, new { error = "That library game is no longer available." });
                var titleId = model.LibraryTitleId(item);
                var platform = model.LibraryCatalogEntry(item)?.Platform ?? (titleId.StartsWith("CUSA", StringComparison.OrdinalIgnoreCase) ? "PS4" : "PS5");
                var supported = platform.Equals("PS4", StringComparison.OrdinalIgnoreCase) ? backend == EmulatorBackend.ShadPS4 :
                    platform.Equals("PS5", StringComparison.OrdinalIgnoreCase) ? backend is EmulatorBackend.KytyPS5 or EmulatorBackend.SharpEmu : false;
                if (!supported) return (409, new { error = "That emulator does not support this game's platform." });
                var availability = backend switch { EmulatorBackend.KytyPS5 => model.EmulatorAvailability, EmulatorBackend.SharpEmu => model.SharpEmuAvailability, _ => model.ShadPS4Availability };
                if (availability != "Executable found") return (409, new { error = availability });
            }
            else if (properties.Length != 2) return (400, new { error = "Invalid action schema." });
            if (!Allows(action, id)) return (409, new { error = "This action is no longer available. Refresh and select it again." });
            if (ActionRequested is null) return (503, new { error = "Windows controls are unavailable." });
            ActionRequested.Invoke(new(action, id, Emulator: backend));
            return (202, new { accepted = true });
        }
        catch (JsonException) { return (400, new { error = "Invalid action JSON." }); }
    }
    internal bool Allows(string action, string id) => id.Length == 0 ? GlobalActions.Contains(action) :
        action is "download" or "browser" or "external" or "import" or "downloadAll" ? ResolveRelease(id) is { } release && ReleaseActions(release).Contains(action) :
        action == "checkStatus" ? model.BrowserDownloads.CanCheckStatus(id) :
        model.Downloads.Any(job => job.Id == id && DownloadActions(job).Contains(action)) ||
        model.LibraryView.Cast<LocalLibraryEntry>().Any(item => item.Id == id && LibraryActions(item).Contains(action)) ||
        model.Installations.Any(job => !job.IsCleared && job.Id == id && InstallationActions(job).Contains(action));
    private (int Status, object Body) Favorite(string? body)
    {
        if (body is null || body.Length > 4096) return (400, new { error = "Invalid favourite request." });
        try
        {
            using var json = JsonDocument.Parse(body, new() { MaxDepth = 4 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 || !root.TryGetProperty("gameId", out var id) || id.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("favorite", out var favorite) || favorite.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (400, new { error = "Invalid favourite schema." });
            var gameId = id.GetString()!;
            if (!Catalog.Releases.Any(release => release.GameId == gameId)) return (400, new { error = "Game identity is no longer available." });
            model.SetOrbitFavorite(gameId, favorite.GetBoolean());
            return (200, model.Settings.OrbitFavorites);
        }
        catch (JsonException) { return (400, new { error = "Invalid favourite JSON." }); }
    }
    public void Dispose()
    { model.Entries.CollectionChanged -= Invalidate; model.Sources.CollectionChanged -= Invalidate; model.PropertyChanged -= ModelChanged; mediaLookup.Dispose(); }
}
internal sealed record OrbitFrontendAction(string Action, string Id, List<LibraryDeletionTarget>? DeletionTargets = null, EmulatorBackend? Emulator = null);
