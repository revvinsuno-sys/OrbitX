namespace PS5CatalogBrowser.Core;

internal static class ExtractionPaths
{
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 1_000_000;

    internal static string Relative(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\'))
            throw new InvalidDataException("Archive contains an absolute or empty path.");
        var parts = name.Replace('\\', '/').TrimEnd('/').Split('/');
        if (parts.Any(part => part.Length == 0 || part != ImageFileSystem.FolderName(part)))
            throw new InvalidDataException("Archive contains an unsafe Windows filename: " + name);
        return Path.Combine(parts);
    }

    internal static long Check(IEnumerable<(string Name, long Size)> entries, string destination, int spaceCopies = 1)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (name, size) in entries)
        {
            var relative = Relative(name);
            if (size < 0 || !paths.Add(relative) || paths.Count > MaximumEntries)
                throw new InvalidDataException("Archive has invalid sizes, duplicate paths or too many entries.");
            total = checked(total + size);
            if (total > MaximumBytes) throw new InvalidDataException("Expanded content exceeds the 2 TiB safety limit.");
        }
        if (checked(total * spaceCopies + 64L * 1024 * 1024) > new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace)
            throw new IOException("Insufficient free space for extraction and verification.");
        return total;
    }
}
