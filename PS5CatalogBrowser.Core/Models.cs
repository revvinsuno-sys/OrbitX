using System.Text.Json.Serialization;

namespace PS5CatalogBrowser.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CatalogSourceType>))]
public enum CatalogSourceType { HttpJson, OrbitLocal, OrbitLive }

public sealed class CatalogSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public CatalogSourceType SourceType { get; set; } = CatalogSourceType.HttpJson;
    public string EncryptedFilePath { get; set; } = "";
    public string KeyFilePath { get; set; } = "";
    public bool AutoRefresh { get; set; } = true;
    public string LocalFallbackSourceId { get; set; } = "";
    public int? FeedGeneration { get; set; }
    public long? Revision { get; set; }
    public int GameCount { get; set; }
    public int ReleaseCount { get; set; }
    public bool Enabled { get; set; } = true;
    public string Format { get; set; } = "Unknown";
    public DateTimeOffset? LastSuccessfulRefresh { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class CatalogArtifact
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> Mirrors { get; set; } = [];
    public string Format { get; set; } = "";
    public long? Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string SourceSet { get; set; } = "";
    public string Host { get; set; } = "";
    public string Version { get; set; } = "";
    public string Region { get; set; } = "";
    public string EditionId { get; set; } = "";
    public int? PartNumber { get; set; }
    public int? PartCount { get; set; }
    public string TitleId { get; set; } = "";
    public string BrowserUrl { get; set; } = "";
    public string ProviderSourceId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Compression { get; set; } = "";
    public long? ExtractedBytes { get; set; }
    [JsonIgnore] public string ExtractedSizeLabel => DownloadSize.Gigabytes(ExtractedBytes);
    public string ArchiveContents { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset? SourceCheckedAt { get; set; }
    public string SourceVerification { get; set; } = "";
    public bool? UpstreamConsoleVerified { get; set; }
    // Transient only: never copied into settings, library records or JSON.
    [JsonIgnore] public string? ArchivePassword { get; set; }
}

public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string CatalogFormat { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Platform { get; set; } = "PS5";
    public string Format { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public long? Size { get; set; }
    public string SizeLabel { get; set; } = "";
    public string Region { get; set; } = "";
    public List<CatalogArtifact> Artifacts { get; set; } = [];
    public string GameId { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Tagline { get; set; } = "";
    public DateOnly? ReleaseDate { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
    public int? NewOnOrbitRank { get; set; }
    public string HeroUrl { get; set; } = "";
    public string CoverFallbackUrl { get; set; } = "";
    public string HeroFallbackUrl { get; set; } = "";
    public string ArtworkLayout { get; set; } = "";
    [JsonIgnore] public bool DownloadsEnabled => true;
    [JsonIgnore] public bool DownloadAllEnabled => CatalogFormat != "Orbit";
    [JsonIgnore] public string DownloadAvailability => CatalogFormat == "Orbit" ? "Select one release; alternatives are independent downloads." : "";
    [JsonIgnore] public string ThumbnailUrl => CoverUrl;
    [JsonIgnore] public string BrowseSizeLabel => CatalogFormat == "Orbit" ? DownloadSize.ReleaseRange(Artifacts) :
        Size.HasValue ? DownloadSize.Gigabytes(Size) : string.IsNullOrWhiteSpace(SizeLabel) ? "Size unknown" : SizeLabel;
    [JsonIgnore] public string BrowseTitleId => CatalogFormat == "Orbit" ? Values(a => a.TitleId) : TitleId;
    [JsonIgnore] public string BrowseVersion => CatalogFormat == "Orbit" ? Values(a => a.Version) : Version;
    [JsonIgnore] public string BrowseRegion => CatalogFormat == "Orbit" ? Values(a => a.Region) : Region;
    [JsonIgnore] public string BrowseFormat => CatalogFormat == "Orbit" ? Values(a => a.Format) : Format;
    private string Values(Func<CatalogArtifact, string> select) => string.Join(" · ", Artifacts.Select(select).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));
    [JsonIgnore] public string Classification => Kind.Contains("payload", StringComparison.OrdinalIgnoreCase) || SourceName.Contains("evoX", StringComparison.OrdinalIgnoreCase) ? "Console payload" : "Unverified download";
}

public sealed class DownloadJob
{
    public string CoverUrl { get; set; } = "";
    public string CoverFallbackUrl { get; set; } = "";
    public OrbitReleaseSelection? OrbitRelease { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EntryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string ArtifactId { get; set; } = "";
    public string SelectionIdentity { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Destination { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string Error { get; set; } = "";
    public string ArchiveStatus { get; set; } = "";
    public long DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
    [JsonIgnore] public string DownloadedSizeLabel => DownloadSize.Gigabytes(DownloadedBytes);
    [JsonIgnore] public string TotalSizeLabel => DownloadSize.Gigabytes(TotalBytes ?? ExpectedSize);
    [JsonIgnore] public double? Percentage => TotalBytes is > 0 ? Math.Min(100, DownloadedBytes * 100.0 / TotalBytes.Value) : null;
    public string ActualSha256 { get; set; } = "";
    public string Method { get; set; } = "Direct";
    public string Mirror { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Version { get; set; } = "";
    public long? ExpectedSize { get; set; }
    public string ExpectedFileName { get; set; } = "";
    public string StagingPath { get; set; } = "";
    public string StagingRoot { get; set; } = "";
    public string StagingOwner { get; set; } = "";
    public bool CleanupPending { get; set; }
    public bool IsCleared { get; set; }
    public DownloadCompletionIntent? Completion { get; set; }
    public HttpResumeCheckpoint? HttpCheckpoint { get; set; }
    [JsonIgnore] public bool CanPause { get; set; }
    [JsonIgnore] public bool CanResume { get; set; }
}

public sealed class HttpResumeCheckpoint
{
    public int Version { get; set; } = 1;
    public string Identity { get; set; } = "";
    public string FileIdentity { get; set; } = "";
    public string StrongETag { get; set; } = "";
    public long? TotalBytes { get; set; }
    public long Cursor { get; set; }
    public string PrefixSha256 { get; set; } = "";
    public bool RestartRequired { get; set; }
}

public sealed class LocalLibraryEntry
{
    public List<InstalledPackage> PackageInstallations { get; set; } = [];
    [JsonIgnore] public List<string> PendingPackagePaths { get; set; } = [];
    [JsonIgnore] public List<string> LaunchPaths { get; set; } = [];
    [JsonIgnore] public bool FolderActionsInspected { get; set; }
    [JsonIgnore] public string InspectedTitle { get; set; } = "";
    [JsonIgnore] public string InspectedTitleId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string CoverFallbackUrl { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CatalogEntryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "Local";
    public string Version { get; set; } = "";
    public string DownloadPath { get; set; } = "";
    public string ContentPath { get; set; } = "";
    public List<string> AdditionalPaths { get; set; } = [];
    public string Integrity { get; set; } = "Not verified";
    public string Extraction { get; set; } = "Not extracted";
    public string Classification { get; set; } = "Unverified";
    public InstalledGame? InstalledGame { get; set; }
    public ContentMoveIntent? ContentMove { get; set; }
    public string DownloadRegistrationId { get; set; } = "";
    public string LastLaunchLogId { get; set; } = "";
    public EmulatorBackend LastEmulator { get; set; } = EmulatorBackend.KytyPS5;
    public List<VerifiedDownloadArtifact> DownloadArtifacts { get; set; } = [];
    public List<LibraryDeletionTarget>? DeletionTargets { get; set; }
}

public sealed record InstalledPackage(string InstallationId, string SourcePath, string SourceSha256, long SourceSize,
    string Directory, string ContentPath)
{
    public bool SourceDeleted { get; set; }
    public bool LaunchEligible { get; set; }
}

public sealed record LibraryDeletionTarget(string Path, bool IsDirectory, string FileIdentity)
{
    public bool Completed { get; set; }
}

public sealed class DownloadCompletionIntent
{
    public string Identity { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public string Phase { get; set; } = "Pending";
}

public sealed class VerifiedDownloadArtifact
{
    public OrbitReleaseSelection? OrbitRelease { get; set; }
    public string JobId { get; set; } = "";
    public string SelectionIdentity { get; set; } = "";
    public string ArtifactId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Destination { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
}

public sealed class InstalledGame
{
    public string InstallationId { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string OriginalImageFilename { get; set; } = "";
    public string Directory { get; set; } = "";
    public int FileCount { get; set; }
    public long Size { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public string Verification { get; set; } = "";
    public string OriginalImageDeletion { get; set; } = "Original retained";
    public bool LaunchEligible { get; set; }
}

public sealed class AppSettings
{
    public string Interface { get; set; } = "Classic Windows";
    public List<string> OrbitFavorites { get; set; } = [];
    public Dictionary<string, string> CompatibilityOverrides { get; set; } = [];
    public Dictionary<string, CompatibilityTestVersion> CompatibilityTestVersions { get; set; } = [];
    public Dictionary<string, string> SharpEmuCompatibilityOverrides { get; set; } = [];
    public Dictionary<string, CompatibilityTestVersion> SharpEmuCompatibilityTestVersions { get; set; } = [];
    public WindowPlacement? WindowPlacement { get; set; }
    public bool StartupFullscreen { get; set; }
    public List<CatalogSource> Sources { get; set; } = [];
    public string DownloadRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PS5Catalog");
    public string IgdbClientId { get; set; } = "";
    public string IgdbClientSecretProtected { get; set; } = "";
    // Read only to erase credentials implicitly imported by an older build.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IgdbCredentialsSeededFromShared { get; set; }
    public double DownloadBandwidthMBps { get; set; } = 100;
    public List<string> ArchivePasswords { get; set; } = ArchiveExtraction.DefaultPasswords();
    public string EmulatorPath { get; set; } = "";
    public string SharpEmuPath { get; set; } = Path.Combine(SharpEmuUpdater.DefaultRoot, "installed", "SharpEmu.exe");
    public string SharpEmuAvailableRevision { get; set; } = "";
    public DateTimeOffset? SharpEmuLastCheck { get; set; }
    public string SharpEmuLastBuildLog { get; set; } = "";
    public bool SharpEmuFullscreen { get; set; }
    public KytyArgumentSettings? KytyArguments { get; set; }
    public string OSFMountPath { get; set; } = "";
    public string InstalledGamesRoot { get; set; } = "";
    public bool RetainOriginalImage { get; set; }
    public List<DownloadJob> Downloads { get; set; } = [];
    public List<LocalLibraryEntry> Library { get; set; } = [];

    public static AppSettings CreateDefault() => new()
    {
        KytyArguments = new(),
        Sources =
        [
            new() { Name = "PS5 Homebrew Catalog", Url = "https://homebrew.page/catalog/v1.json" },
            new() { Name = "evoX-CoreOS Payloads", Url = "https://nexgen999.github.io/evoX-CoreOS/json/payloads.json" }
        ]
    };
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
