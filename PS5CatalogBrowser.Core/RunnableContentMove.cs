using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PS5CatalogBrowser.Core;

public sealed class ContentMoveIntent
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string InstalledRoot { get; set; } = "";
    public bool Directory { get; set; }
    public string Phase { get; set; } = "Pending";
    public List<ImageFile> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public InstalledFolderLayout? Layout { get; set; }
    public ContentMoveIntent? PreviousMove { get; set; }
}

// Relocation is a separate, journalled operation after durable download completion.
// A single no-overwrite rename is used; cross-volume copying is deliberately unavailable.
public static class RunnableContentMove
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LocalLibraryEntry, SemaphoreSlim> Owners = new();
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Rename(string source, string destination, uint flags);

    public static async Task<bool> MoveAsync(AppSettings settings, LocalLibraryEntry item, Action<AppSettings> persist,
        Func<DownloadJob, bool> active, CancellationToken cancellation = default, IProgress<InstallationProgress>? progress = null)
    {
        var owner = Owners.GetValue(item, _ => new SemaphoreSlim(1, 1));
        if (!await owner.WaitAsync(0, cancellation)) throw new IOException("This content already has an active move.");
        try { return await MoveCoreAsync(settings, item, persist, active, cancellation, progress); }
        finally { owner.Release(); }
    }

    private static async Task<bool> MoveCoreAsync(AppSettings settings, LocalLibraryEntry item, Action<AppSettings> persist,
        Func<DownloadJob, bool> active, CancellationToken cancellation, IProgress<InstallationProgress>? progress)
    {
        if (item.ContentMove is { Phase: not "Completed" }) throw new IOException("A previous content move needs reconciliation before retrying.");
        if (item.ContentMove is not null || item.InstalledGame is not null || item.DeletionTargets is not null || item.AdditionalPaths.Count > 0 ||
            !Path.IsPathFullyQualified(settings.DownloadRoot) || !Path.IsPathFullyQualified(settings.InstalledGamesRoot) || !Path.IsPathFullyQualified(item.ContentPath) ||
            !ImageFileSystem.IsWithin(settings.DownloadRoot, item.ContentPath) || ImageFileSystem.IsWithin(settings.InstalledGamesRoot, item.ContentPath)) return false;
        if (!settings.Library.Contains(item)) throw new IOException("The content record is no longer retained.");
        var source = Path.GetFullPath(item.ContentPath);
        if (Path.GetRelativePath(settings.DownloadRoot, source).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part == ".ps5catalog-staging" || part.StartsWith(".ps5catalog-extract-", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Download and extraction staging cannot be relocated as installed content.");
        var directory = Directory.Exists(source);
        if (!directory && (!File.Exists(source) || !Path.GetExtension(source).Equals(".elf", StringComparison.OrdinalIgnoreCase))) return false;
        ImageFileSystem.EnsureNoLinks(source);
        var owned = settings.Downloads.Where(job => ImageFileSystem.SamePath(job.Destination, source) ||
            ImageFileSystem.SamePath(job.Destination, item.DownloadPath) || item.DownloadArtifacts.Any(a => a.JobId == job.Id) ||
            Overlaps(source, job.Destination) || Overlaps(source, job.StagingPath)).ToArray();
        if (owned.Any(job => active(job) || DownloadCompletion.Unresolved(job) || job.CleanupPending || job.State is not ("Downloaded" or "Imported")))
            throw new IOException("An active or incomplete download owns this content.");
        var conflict = settings.Library.Where(other => other != item).SelectMany(other =>
            new[] { other.ContentPath, other.DownloadPath, other.InstalledGame?.Directory ?? "" }.Concat(other.AdditionalPaths)
                .Concat(other.DownloadArtifacts.Select(a => a.Destination)).Where(path => Overlaps(source, path))
                .Select(path => (Item: other, Path: path))).FirstOrDefault();
        if (conflict.Item is not null)
            throw new IOException($"Library item '{conflict.Item.Title}' references '{conflict.Path}', which overlaps '{source}'. No files were moved.");
        InstalledFolderLayout? layout = null;
        if (directory)
        {
            layout = await InstalledFolderLayout.PlanAsync(source, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (layout is not null) await ApplyLayoutAsync(settings, item, layout, persist, cancellation, progress);
        }
        var intent = new ContentMoveIntent { Source = source, InstalledRoot = Path.GetFullPath(settings.InstalledGamesRoot), Directory = directory };
        if (ImageFileSystem.SamePath(source, intent.InstalledRoot) || ImageFileSystem.IsWithin(source, intent.InstalledRoot))
            throw new IOException("The installed directory cannot be inside the content being moved.");
        if (directory)
        {
            progress?.Report(new(InstallationState.Scanning, "Scanning extracted files", 0, 0, 0, TimeSpan.Zero));
            var scan = await ImageFileSystem.ScanAsync(source, cancellation, requireGame: false);
            var executables = scan.Files.Where(f => Path.GetFileName(f.RelativePath).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (scan.Files.Count == 0 || executables.Length != 1 && item.Extraction != "Extracted") return false;
            intent.Directories = scan.Directories;
            var report = HashProgress(progress, scan.TotalBytes, "Hashing extracted files");
            foreach (var file in scan.Files) intent.Files.Add(file with { Sha256 = await ImageFileSystem.HashAsync(Path.Combine(source, file.RelativePath), cancellation, bytes => report(file.RelativePath, bytes)) });
        }
        else
        {
            var info = new FileInfo(source);
            var hash = await ImageFileSystem.HashAsync(source, cancellation);
            if (owned.Any(job => job.DownloadedBytes != info.Length || job.ActualSha256.Length != 64 || !job.ActualSha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Completed download verification no longer matches this executable.");
            intent.Files = [new(Path.GetFileName(source), info.Length, info.LastWriteTimeUtc.Ticks, hash)];
        }
        ImageFileSystem.EnsureNoLinks(intent.InstalledRoot);
        var name = Path.GetFileName(source);
        if (directory && File.Exists(Path.Combine(source, "eboot.bin")))
        {
            if (layout is not null) name = ImageFileSystem.FolderName(Path.GetFileName(layout.GameRoot));
            try
            {
                var metadata = await ImageFileSystem.ReadGameMetadataAsync(source, cancellation);
                if (metadata.Title.Length > 0) name = ImageFileSystem.FolderName(metadata.Title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = directory ? "" : Path.GetExtension(name);
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            var candidate = Path.Combine(intent.InstalledRoot, suffix == 0 ? name : $"{stem} ({suffix}){extension}");
            if (File.Exists(candidate) || Directory.Exists(candidate) || settings.Library.Any(other => other != item &&
                new[] { other.ContentPath, other.InstalledGame?.Directory ?? "", other.ContentMove?.Destination ?? "" }
                    .Any(path => path.Length > 0 && ImageFileSystem.SamePath(path, candidate)))) continue;
            intent.Destination = candidate; break;
        }
        if (intent.Destination.Length == 0) throw new IOException("No available Installed destination. Existing content was preserved.");
        await VerifyAsync(intent, source, cancellation, progress);
        cancellation.ThrowIfCancellationRequested();
        item.ContentMove = intent;
        try { persist(settings); } catch { item.ContentMove = null; throw; }
        Directory.CreateDirectory(intent.InstalledRoot);
        ImageFileSystem.EnsureNoLinks(source); ImageFileSystem.EnsureNoLinks(intent.Destination);
        progress?.Report(new(InstallationState.Finalising, "Moving verified folder into Installed", 0, 0, 0, TimeSpan.Zero));
        if (!Rename(source, intent.Destination, 0))
        {
            var error = Marshal.GetLastWin32Error();
            item.ContentMove = null;
            try { persist(settings); } catch { item.ContentMove = intent; }
            throw new IOException("Content was kept in Downloads. Automatic moves require an available destination on the same volume: " + new Win32Exception(error).Message);
        }
        await FinishAsync(settings, item, persist, CancellationToken.None, progress);
        return true;
    }
    private static bool Overlaps(string source, string path) => path.Length > 0 &&
        (ImageFileSystem.SamePath(source, path) || ImageFileSystem.IsWithin(source, path) || ImageFileSystem.IsWithin(path, source));

    public static async Task RecoverAsync(AppSettings settings, LocalLibraryEntry item, Action<AppSettings> persist)
    {
        if (item.ContentMove is { Phase: "Normalizing", Layout: { } layout } normalizing)
        {
            if (!ImageFileSystem.SamePath(layout.Root, item.ContentPath) ||
                !(ImageFileSystem.IsWithin(settings.DownloadRoot, layout.Root) || ImageFileSystem.IsWithin(settings.InstalledGamesRoot, layout.Root)) ||
                !ImageFileSystem.IsWithin(layout.Root, Path.Combine(layout.Root, layout.GameRoot)) ||
                !ImageFileSystem.SamePath(Path.GetDirectoryName(layout.Root)!, Path.GetDirectoryName(layout.Temporary)!))
                throw new IOException("Layout recovery paths no longer match the saved installation.");
            ImageFileSystem.EnsureNoLinks(layout.Root); ImageFileSystem.EnsureNoLinks(layout.Temporary);
            layout.Restore();
            item.ContentMove = normalizing.PreviousMove;
            persist(settings);
            return;
        }
        if (item.ContentMove is not { Phase: "Pending" } intent) return;
        // Recovery only registers an already-renamed, fully verified destination. Never delete or resume a move.
        if (File.Exists(intent.Source) || Directory.Exists(intent.Source) || (!File.Exists(intent.Destination) && !Directory.Exists(intent.Destination)))
        { item.Classification = "Content move needs attention — files preserved"; return; }
        try { await FinishAsync(settings, item, persist, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { item.Classification = "Content move needs attention — " + ex.Message; }
    }

    public static async Task<bool> NormalizeInstalledAsync(AppSettings settings, LocalLibraryEntry item, Action<AppSettings> persist,
        CancellationToken cancellation = default, IProgress<InstallationProgress>? progress = null)
    {
        if (!settings.Library.Contains(item) || item.ContentMove is not { Phase: "Completed", Directory: true } previous ||
            item.InstalledGame is not null || item.PackageInstallations.Count > 0 || item.DeletionTargets is not null ||
            !ImageFileSystem.IsWithin(settings.InstalledGamesRoot, item.ContentPath) || !ImageFileSystem.SamePath(item.ContentPath, previous.Destination)) return false;
        if (settings.Library.Any(other => other != item && new[] { other.ContentPath, other.DownloadPath }.Concat(other.AdditionalPaths)
            .Any(path => Overlaps(item.ContentPath, path)))) throw new IOException("Another library record shares this installation.");
        if (settings.Downloads.Any(job => (Overlaps(item.ContentPath, job.Destination) || Overlaps(item.ContentPath, job.StagingPath)) &&
            (DownloadCompletion.Unresolved(job) || job.CleanupPending || HttpResume.Reserves(job) || job.State is "Queued" or "Downloading" or "Paused")))
            throw new IOException("An incomplete download owns this installation.");
        var layout = await InstalledFolderLayout.PlanAsync(item.ContentPath, cancellation);
        if (layout is null) return false;
        await VerifyAsync(previous, item.ContentPath, cancellation, progress);
        await ApplyLayoutAsync(settings, item, layout, persist, cancellation, progress);
        return true;
    }

    private static async Task ApplyLayoutAsync(AppSettings settings, LocalLibraryEntry item, InstalledFolderLayout layout,
        Action<AppSettings> persist, CancellationToken cancellation, IProgress<InstallationProgress>? progress)
    {
        var previous = item.ContentMove;
        cancellation.ThrowIfCancellationRequested();
        var marker = new ContentMoveIntent { Phase = "Normalizing", Source = layout.Root, Directory = true, Layout = layout, PreviousMove = previous };
        item.ContentMove = marker;
        try { persist(settings); } catch { item.ContentMove = previous; throw; }
        try
        {
            layout.Apply();
            ContentMoveIntent? updated = null;
            if (previous is not null)
            {
                var scan = await ImageFileSystem.ScanAsync(layout.Root, cancellation, requireGame: false);
                updated = new ContentMoveIntent { Phase = "Completed", Source = previous.Source, Destination = previous.Destination,
                    InstalledRoot = previous.InstalledRoot, Directory = true, Directories = scan.Directories,
                    Files = previous.Files.Select(file => file with { RelativePath = layout.FilePaths[file.RelativePath] }).OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList() };
                await VerifyAsync(updated, layout.Root, cancellation, progress);
            }
            var candidate = SettingsStore.Snapshot(settings);
            candidate.Library.Single(record => record.Id == item.Id).ContentMove = updated;
            persist(candidate);
            item.ContentMove = updated;
        }
        catch
        {
            layout.Restore(); item.ContentMove = previous;
            try { persist(settings); } catch { item.ContentMove = marker; }
            throw;
        }
    }

    private static async Task FinishAsync(AppSettings settings, LocalLibraryEntry item, Action<AppSettings> persist, CancellationToken cancellation, IProgress<InstallationProgress>? progress = null)
    {
        var intent = item.ContentMove!;
        if (!ImageFileSystem.SamePath(settings.InstalledGamesRoot, intent.InstalledRoot) || !ImageFileSystem.IsWithin(intent.InstalledRoot, intent.Destination) ||
            !ImageFileSystem.IsWithin(settings.DownloadRoot, intent.Source) || !ImageFileSystem.SamePath(item.ContentPath, intent.Source))
            throw new IOException("The saved move paths no longer match this content or configured roots.");
        await VerifyAsync(intent, intent.Destination, cancellation, progress);
        var candidate = SettingsStore.Snapshot(settings);
        var target = candidate.Library.Single(record => record.Id == item.Id);
        Apply(candidate, target);
        persist(candidate);
        Apply(settings, item);
    }

    private static void Apply(AppSettings settings, LocalLibraryEntry item)
    {
        var intent = item.ContentMove!;
        item.ContentPath = intent.Destination; item.DownloadPath = "";
        item.Classification = intent.Directory ? intent.Files.Count(file => Path.GetFileName(file.RelativePath).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)) == 1
            ? "Ready for launch attempt" : "Extracted folder — inspect contents" : "Direct ELF launch attempt";
        item.Integrity = "Installed content SHA-256 verified";
        foreach (var job in settings.Downloads.Where(job => ImageFileSystem.SamePath(job.Destination, intent.Source) ||
            intent.Directory && job.Method == "Scan" && ImageFileSystem.IsWithin(intent.Source, job.Destination)))
        {
            // Keep the original completion reservation immutable for A4/A5 staging cleanup.
            job.State = "Installed"; job.IsCleared = true; job.Error = "";
        }
        foreach (var artifact in item.DownloadArtifacts.Where(a => ImageFileSystem.SamePath(a.Destination, intent.Source))) artifact.Destination = intent.Destination;
        intent.Phase = "Completed";
    }

    public static async Task VerifyAsync(ContentMoveIntent intent, string path, CancellationToken cancellation, IProgress<InstallationProgress>? progress = null)
    {
        ImageFileSystem.EnsureNoLinks(path);
        if (intent.Files.Count == 0) throw new InvalidDataException("Content verification manifest is empty.");
        if (intent.Directory)
        {
            var actual = await ImageFileSystem.ScanAsync(path, cancellation, requireGame: false);
            if (!actual.Directories.SequenceEqual(intent.Directories) || actual.Files.Count != intent.Files.Count ||
                actual.Files.Where((f, i) => f.RelativePath != intent.Files[i].RelativePath || f.Length != intent.Files[i].Length).Any())
                throw new InvalidDataException("Content file set changed.");
        }
        else if (intent.Files.Count != 1 || new FileInfo(path).Length != intent.Files[0].Length) throw new InvalidDataException("Executable size changed.");
        var report = HashProgress(progress, intent.Files.Sum(file => file.Length), "Verifying " + path);
        foreach (var file in intent.Files)
        {
            if (file.Sha256.Length != 64 || (intent.Directory && (!ImageFileSystem.IsWithin(path, Path.Combine(path, file.RelativePath)) || Path.IsPathRooted(file.RelativePath))))
                throw new InvalidDataException("Unsafe content verification manifest.");
            if (await ImageFileSystem.HashAsync(intent.Directory ? Path.Combine(path, file.RelativePath) : path, cancellation, bytes => report(file.RelativePath, bytes)) != file.Sha256)
                throw new InvalidDataException("Content checksum changed.");
        }
    }

    internal static Action<string, int> HashProgress(IProgress<InstallationProgress>? progress, long total, string stage)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew(); long processed = 0, last = -250;
        progress?.Report(new(InstallationState.Verifying, stage, 0, total, 0, TimeSpan.Zero));
        return (file, bytes) =>
        {
            processed += bytes;
            if (clock.ElapsedMilliseconds - last < 250 && processed != total) return;
            last = clock.ElapsedMilliseconds;
            progress?.Report(new(InstallationState.Verifying, stage + "\n" + file, processed, total, processed / Math.Max(.001, clock.Elapsed.TotalSeconds), clock.Elapsed));
        };
    }
}
