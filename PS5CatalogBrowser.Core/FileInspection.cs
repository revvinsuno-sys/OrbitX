namespace PS5CatalogBrowser.Core;

public static class FileInspection
{
    public static string Classify(string path)
    {
        if (Directory.Exists(path)) return FindGameRoot(path) is not null ? "Ready for launch attempt" : "No eboot.bin found";
        if (!File.Exists(path)) return "Missing local content";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".elf" => HasMagic(path, [0x7f, (byte)'E', (byte)'L', (byte)'F']) ? "Direct ELF launch attempt" : "Invalid ELF file",
            ".zar" => "Requires a confirmed KytyPS5 ZArchive build",
            ".zip" => HasMagic(path, [(byte)'P', (byte)'K']) ? "ZIP requires extraction and inspection" : "Invalid ZIP file",
            ".exfat" => "exFAT image — installation requires OSFMount",
            ".rar" => "RAR requires extraction and installation",
            ".7z" => "7z archive — extraction unsupported",
            ".pkg" => "PKG — install a complete PS4/PS5 debug or fake package",
            ".ffpkg" or ".ffpfs" or ".ffpfsc" => "Filesystem image — requires conversion or external extraction",
            _ => "Unsupported format"
        };
    }

    private static bool HasMagic(string path, byte[] magic)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return stream.Read(header) >= magic.Length && header[..magic.Length].SequenceEqual(magic);
    }

    public static string? FindGameRoot(string root)
    {
        if (!Directory.Exists(root)) return null;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            if (File.Exists(Path.Combine(path, "eboot.bin"))) return path;
            if (depth >= 8) continue;
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) queue.Enqueue((child, depth + 1));
            }
        }
        return null;
    }
}
