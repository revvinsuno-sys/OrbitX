namespace PS5CatalogBrowser.Core;

public static class OSFMountProtocol
{
    public static string FreeDrive(Func<string, bool> unavailable) => Enumerable.Range('D', 'Z' - 'D' + 1).Reverse()
        .Select(value => ((char)value) + ":").FirstOrDefault(drive => !unavailable(drive)) ?? throw new IOException("No unused drive letters are available.");

    // OSFMount 3.3 lists the mount point and image on separate lines. Bind both exactly;
    // a filename containing "ReadOnly" is not a drive property.
    public static bool IsOwnedDescription(string output, string drive, string source)
    {
        // The CLI replaces a separating space with CR CR LF when soft-wrapping a long word/path.
        // Restore only that observed marker; ordinary line breaks remain distinct descriptor fields.
        var lines = output.Replace("\r\r\n", " ").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 3 &&
            lines.Count(line => line.Equals("Mount point: " + drive, StringComparison.OrdinalIgnoreCase)) == 1 &&
            lines.Count(line => line.Equals("Image file: \\??\\" + source, StringComparison.OrdinalIgnoreCase)) == 1 &&
            lines.Count(line => System.Text.RegularExpressions.Regex.IsMatch(line,
                @"^Size: [0-9]+ bytes \([^\r\n)]+\), ReadOnly, File Type Virtual Disk, HDD\.$")) == 1;
    }
}
