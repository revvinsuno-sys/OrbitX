namespace PS5CatalogBrowser.Core;

public static class LibraryPackageFolder
{
    public static async Task<(List<string> Packages, List<string> LaunchPaths)> InspectAsync(LocalLibraryEntry item,
        IReadOnlyCollection<InstallationJob> installations, CancellationToken cancellation)
    {
        var scan = await ImageFileSystem.ScanAsync(item.ContentPath, cancellation, requireGame: false);
        var completed = item.PackageInstallations.Where(package => installations.Any(job => job.Id == package.InstallationId &&
            job.ParentLibraryId == item.Id && job.Verified && job.Registered &&
            job.State is InstallationState.Completed or InstallationState.CompletedOriginalRetained) && Directory.Exists(package.Directory)).ToArray();
        var packages = new List<string>();
        foreach (var file in scan.Files.Where(file => !Private(file.RelativePath) && Path.GetExtension(file.RelativePath).Equals(".pkg", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(item.ContentPath, file.RelativePath);
            if (item.PackageInstallations.Any(package => ImageFileSystem.IsWithin(package.Directory, path))) continue;
            var previous = completed.LastOrDefault(package => ImageFileSystem.SamePath(package.SourcePath, path));
            // A retained PKG is already installed only while it still matches the recorded source bytes.
            if (previous is not null && file.Length == previous.SourceSize &&
                (await ImageFileSystem.HashAsync(path, cancellation)).Equals(previous.SourceSha256, StringComparison.OrdinalIgnoreCase)) continue;
            packages.Add(path);
        }
        var launches = scan.Files.Where(file => !Private(file.RelativePath) && Path.GetFileName(file.RelativePath).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetDirectoryName(Path.Combine(item.ContentPath, file.RelativePath))!)
            .Where(path => !item.PackageInstallations.Any(package => (ImageFileSystem.SamePath(package.Directory, path) || ImageFileSystem.IsWithin(package.Directory, path)) &&
                (!package.LaunchEligible || !completed.Contains(package))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return (packages.Order(StringComparer.OrdinalIgnoreCase).ToList(), launches);
    }

    private static bool Private(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.StartsWith('.'));

    public static async Task VerifyAsync(LocalLibraryEntry item, IReadOnlyCollection<InstallationJob> installations, CancellationToken cancellation)
    {
        var jobs = item.PackageInstallations.Select(package => installations.SingleOrDefault(job => job.Id == package.InstallationId)
            ?? throw new IOException("Package verification manifest is missing.")).ToArray();
        foreach (var job in jobs)
        {
            if (job.ParentLibraryId != item.Id || !job.Verified || !job.Registered || !ImageFileSystem.IsWithin(item.ContentPath, job.Destination))
                throw new IOException("Package installation registration is incomplete.");
            await ImageFileSystem.VerifyFinalAsync(job.Destination, job, cancellation);
        }
        if (item.ContentMove is not { Phase: "Completed" } move) return;
        var files = move.Files.Where(file => !item.PackageInstallations.Any(package => package.SourceDeleted &&
            ImageFileSystem.SamePath(package.SourcePath, Path.Combine(item.ContentPath, file.RelativePath))))
            .ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var directories = move.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            var prefix = Path.GetRelativePath(item.ContentPath, job.Destination);
            directories.Add(prefix);
            foreach (var directory in job.Directories) directories.Add(Path.Combine(prefix, directory));
            foreach (var file in job.Files) files.Add(Path.Combine(prefix, file.RelativePath), file with { RelativePath = Path.Combine(prefix, file.RelativePath) });
        }
        var actual = await ImageFileSystem.ScanAsync(item.ContentPath, cancellation, requireGame: false);
        if (actual.Files.Count != files.Count || !directories.SetEquals(actual.Directories)) throw new IOException("Library folder contents changed after installation.");
        foreach (var file in actual.Files)
            if (!files.TryGetValue(file.RelativePath, out var expected) || file.Length != expected.Length ||
                !(await ImageFileSystem.HashAsync(Path.Combine(item.ContentPath, file.RelativePath), cancellation)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Library folder file failed SHA-256 verification: " + file.RelativePath);
    }
}
