namespace PS5CatalogBrowser.Core;

public static class DownloadSize
{
    // Decimal gigabytes: display only. Integrity/admission continue to use exact byte counts.
    public static string Gigabytes(long? bytes) => bytes switch
    {
        null or < 0 => "Size unknown",
        > 0 and < 10_000_000 => "<0.01 GB",
        _ => $"{bytes.Value / 1_000_000_000d:N2} GB"
    };

    public static string ReleaseRange(IEnumerable<CatalogArtifact> artifacts)
    {
        var sizes = artifacts.Where(a => a.Size is >= 0).Select(a => a.Size!.Value).ToArray();
        if (sizes.Length == 0) return "Size unknown";
        var minimum = Gigabytes(sizes.Min()); var maximum = Gigabytes(sizes.Max());
        return minimum == maximum ? minimum : minimum + " – " + maximum;
    }
}
