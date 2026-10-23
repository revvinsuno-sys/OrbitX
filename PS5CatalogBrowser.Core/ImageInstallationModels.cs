using System.Text.Json.Serialization;

namespace PS5CatalogBrowser.Core;

public enum InstallationState
{
    Pending, CheckingPrerequisites, Mounting, Scanning, Extracting, Verifying, Finalising,
    RegisteringInLibrary, RemovingOriginal, Completed, CompletedOriginalRetained, Failed, Cancelled, NeedsAttention
}

public sealed class InstallationJob
{
    public string ParentLibraryId { get; set; } = "";
    public bool RequireGameRoot { get; set; } = true;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public InstallationState State { get; set; }
    public string SourcePath { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public long SourceSize { get; set; }
    public string Title { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string CatalogEntryId { get; set; } = "";
    public string CatalogSource { get; set; } = "Local";
    public string InstallationRoot { get; set; } = "";
    public string StagingPath { get; set; } = "";
    public string Destination { get; set; } = "";
    public string GameRootRelative { get; set; } = "";
    public string MountDescription { get; set; } = "";
    public bool DeleteOriginal { get; set; }
    public bool Verified { get; set; }
    public bool Registered { get; set; }
    public bool SourceDeleted { get; set; }
    public bool IsCleared { get; set; }
    public string CleanupResult { get; set; } = "Original preserved";
    public string Error { get; set; } = "";
    public DateTimeOffset? InstalledAt { get; set; }
    public List<ImageFile> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    [JsonIgnore] public long TotalBytes => Files.Sum(f => f.Length);
    [JsonIgnore] public string StateLabel => Label(State);
    public static string Label(InstallationState state) => state switch
    {
        InstallationState.CheckingPrerequisites => "Checking prerequisites",
        InstallationState.RegisteringInLibrary => "Registering in Library",
        InstallationState.RemovingOriginal => "Removing original",
        InstallationState.CompletedOriginalRetained => "Completed — original retained",
        InstallationState.NeedsAttention => "Needs attention",
        _ => state.ToString()
    };
    [JsonIgnore] public bool CanRetryCleanup => Verified && Registered && !SourceDeleted && DeleteOriginal;
    [JsonIgnore] public bool CanClearHistory => State is InstallationState.Completed or InstallationState.CompletedOriginalRetained or
        InstallationState.Failed or InstallationState.Cancelled or InstallationState.NeedsAttention;
}

public sealed record ImageFile(string RelativePath, long Length, long LastWriteTicks, string Sha256 = "");
public sealed record ImageScan(List<ImageFile> Files, List<string> Directories, string GameRootRelative, string Title, string TitleId)
{
    public long TotalBytes => Files.Sum(f => f.Length);
}
public sealed record InstallationProgress(InstallationState State, string File, long Bytes, long TotalBytes,
    double BytesPerSecond, TimeSpan Elapsed)
{
    public string StateLabel => InstallationJob.Label(State);
    public double Percentage => TotalBytes == 0 ? 0 : Math.Min(99.9, Bytes * 100.0 / TotalBytes);
    public TimeSpan? Remaining => BytesPerSecond > 0 ? TimeSpan.FromSeconds(Math.Max(0, TotalBytes - Bytes) / BytesPerSecond) : null;
}
public sealed record InstallationConfirmation(string Title, string FolderName, string SourcePath, string Destination,
    long SourceSize, long ExtractedSize, long AvailableSpace, bool DeleteOriginal);
public sealed record InstallationChoice(string FolderName, bool DeleteOriginal);

public interface IImageMount : IAsyncDisposable
{
    string Root { get; }
    string Description { get; }
    Task ReleaseAsync();
}
public interface IImageMounter
{
    Task CheckAsync(CancellationToken cancellation);
    Task<IImageMount> MountAsync(string source, CancellationToken cancellation);
}
