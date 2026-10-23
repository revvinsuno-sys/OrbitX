namespace PS5CatalogBrowser.Core;

public static class ArchiveExtraction
{
    public static List<string> DefaultPasswords() => ["DLPSGAME.COM", "[DLPSGAME.COM]", "SuperPSX", "www.DLPSGAME.COM", "downloadgameps3.com", "hako"];

    public static string[] Candidates(string? supplied, IEnumerable<string> configured) =>
        (supplied is null ? configured : new[] { supplied }.Concat(configured))
        .Where(password => !string.IsNullOrEmpty(password)).Distinct(StringComparer.Ordinal).ToArray();

    public static async Task<string> ExtractAsync(string source, string destination, IEnumerable<string> passwords,
        CancellationToken cancellation, IProgress<double>? progress = null)
    {
        // First handle unencrypted content without applying a password. Every failed
        // attempt owns and removes its own staging directory before the next starts.
        foreach (var password in new string?[] { null }.Concat(passwords.Distinct(StringComparer.Ordinal)))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                return Path.GetExtension(source).Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    ? await SafeRarExtractor.ExtractAsync(source, destination, cancellation, progress, password)
                    : await SafeZipExtractor.ExtractAsync(source, destination, cancellation, progress, password);
            }
            catch (Exception ex) when (ArchivePasswordException.IsPasswordFailure(ex)) { }
        }
        throw new ArchivePasswordException();
    }
}
