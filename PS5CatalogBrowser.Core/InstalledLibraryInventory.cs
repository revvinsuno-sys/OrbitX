namespace PS5CatalogBrowser.Core;

public sealed class InstalledInventoryResult
{
    public List<string> Present { get; } = [];
    public List<string> Added { get; } = [];
    public List<string> Restored { get; } = [];
    public List<string> Missing { get; } = [];
    public List<string> Excluded { get; } = [];
    public List<string> Issues { get; } = [];
    public bool Deferred { get; set; }
}

public static class InstalledLibraryInventory
{
    public static List<string> Scan(AppSettings settings)
    {
        var root = settings.InstalledGamesRoot;
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return [];
        ImageFileSystem.EnsureNoLinks(root);
        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (Path.GetFileName(path).StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(path))
            {
                if (HasContent(path)) paths.Add(path);
            }
            else if (Path.GetExtension(path).Equals(".elf", StringComparison.OrdinalIgnoreCase) ||
                settings.Library.Any(item => ImageFileSystem.SamePath(item.ContentPath, path))) paths.Add(path);
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool HasContent(string root)
    {
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
        while (pending.TryDequeue(out var current))
        {
            if (File.Exists(Path.Combine(current.Path, "eboot.bin")) || Directory.EnumerateFiles(current.Path, "*.pkg").Any()) return true;
            if (current.Depth >= 8) continue;
            foreach (var child in Directory.EnumerateDirectories(current.Path))
                if (!Path.GetFileName(child).StartsWith('.') && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Enqueue((child, current.Depth + 1));
        }
        return false;
    }

    public static bool Covers(string root, string path) => Path.IsPathFullyQualified(path) &&
        (ImageFileSystem.SamePath(root, path) || ImageFileSystem.IsWithin(root, path));

    public static async Task<InstalledInventoryResult> ReconcileAsync(AppSettings settings, IReadOnlyCollection<InstallationJob> installations,
        Action<AppSettings> persist, CancellationToken cancellation = default, Func<bool>? canCommit = null)
    {
        var root = settings.InstalledGamesRoot;
        var result = new InstalledInventoryResult();
        if (!Path.IsPathFullyQualified(root)) return result;
        result.Present.AddRange(Scan(settings));
        var proposed = new List<(string Path, LocalLibraryEntry Item, InstallationJob? Journal)>();
        foreach (var path in result.Present)
        {
            cancellation.ThrowIfCancellationRequested();
            if (settings.Library.Any(item => Covers(path, item.ContentPath) && (File.Exists(item.ContentPath) || Directory.Exists(item.ContentPath)))) continue;
            InstallationJob? restored = null;
            var matching = installations.Where(job => job.ParentLibraryId.Length == 0 && job.Verified && job.Registered &&
                job.State is (InstallationState.Completed or InstallationState.CompletedOriginalRetained) && ImageFileSystem.SamePath(job.Destination, path)).ToArray();
            if (matching.Length == 1)
            {
                try { await ImageFileSystem.VerifyFinalAsync(path, matching[0], cancellation); restored = matching[0]; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { result.Issues.Add(path + ": journal verification failed; discovered as unverified. " + ex.Message); }
            }
            else if (matching.Length > 1) result.Issues.Add(path + ": several installation journals claim this folder; discovered as unverified.");
            var title = Path.GetFileName(path);
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "eboot.bin")))
            {
                try { var metadata = await ImageFileSystem.ReadGameMetadataAsync(path, cancellation); if (metadata.Title.Length > 0) title = metadata.Title; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { result.Issues.Add(path + ": metadata unavailable. " + ex.Message); }
            }
            proposed.Add((path, new LocalLibraryEntry { ContentPath = path, Title = title, Source = "Local", Extraction = "Existing local content",
                Classification = FileInspection.Classify(path), Integrity = "Discovered in Installed; not verified" }, restored));
        }
        cancellation.ThrowIfCancellationRequested();
        if (settings.InstalledGamesRoot != root || canCommit?.Invoke() == false) { result.Deferred = true; return result; }
        // Build the save from current state after the asynchronous reads, never from an old snapshot.
        var candidate = SettingsStore.Snapshot(settings);
        var changed = false;
        foreach (var (path, discovered, journal) in proposed)
        {
            if (!File.Exists(path) && !Directory.Exists(path) || candidate.Library.Any(item => Covers(path, item.ContentPath) &&
                (File.Exists(item.ContentPath) || Directory.Exists(item.ContentPath)))) continue;
            if (journal is not null) { InstalledLibrary.Register(candidate, journal); result.Restored.Add(path); }
            else { candidate.Library.Add(discovered); result.Added.Add(path); }
            changed = true;
        }
        foreach (var item in candidate.Library.Where(item => Covers(root, item.ContentPath) && item.DeletionTargets is null &&
            item.ContentMove is not { Phase: not "Completed" }))
        {
            if (!File.Exists(item.ContentPath) && !Directory.Exists(item.ContentPath))
            {
                result.Missing.Add(item.ContentPath);
                if (item.Classification != "Missing local content") { item.Classification = "Missing local content"; changed = true; }
            }
            else if (!result.Present.Any(path => Covers(path, item.ContentPath))) result.Excluded.Add(item.ContentPath);
            else if (item.Classification == "Missing local content")
            { item.Classification = FileInspection.Classify(item.ContentPath); changed = true; }
        }
        if (changed)
        {
            persist(candidate);
            // Preserve live object identity for unchanged records and operation owners.
            foreach (var saved in candidate.Library)
            {
                var existing = settings.Library.FirstOrDefault(item => item.Id == saved.Id);
                if (existing is null) settings.Library.Add(saved);
                else if (result.Restored.Any(path => Covers(path, saved.ContentPath))) settings.Library[settings.Library.IndexOf(existing)] = saved;
                else existing.Classification = saved.Classification;
            }
        }
        return result;
    }
}
