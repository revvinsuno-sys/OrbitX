using System.Globalization;
using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class CatalogPresentationTests
{
    public static Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        try
        {
            foreach (var (bytes, expected) in new (long?, string)[] { (null, "Size unknown"), (-1, "Size unknown"), (0, "0.00 GB"),
                (1234, "<0.01 GB"), (10_000_000, "0.01 GB"), (1_000_000_000, "1.00 GB"), (45_678_000_000, "45.68 GB") })
                Check(DownloadSize.Gigabytes(bytes) == expected, "decimal GB and unknown/small boundaries");
            var entry = new CatalogEntry { CatalogFormat = "Orbit", Id = "game", SourceId = "source", GameId = "game", CoverUrl = "https://art.invalid/primary.png",
                CoverFallbackUrl = "https://art.invalid/fallback.png", Artifacts = [new() { Id = "a", FileName = "fixture.pkg", Size = 2_000_000_000, Url = "https://archive.org/download/synthetic/fixture.pkg" }, new() { Id = "b", Size = 5_000_000_000 }] };
            var fingerprint = DownloadAdmission.Capture(entry, entry.Artifacts[0]);
            Check(entry.ThumbnailUrl == entry.CoverUrl && entry.BrowseSizeLabel == "2.00 GB – 5.00 GB", "catalogue artwork and alternative size range");
            Check(DownloadMirror.ForEntry(entry)[0].SizeLabel == "2.00 GB" && entry.Size is null && entry.Artifacts[0].Size == 2_000_000_000 &&
                DownloadAdmission.Capture(entry, entry.Artifacts[0]) == fingerprint, "formatting preserves alternatives, byte size and selected identity");
            var job = new DownloadJob { DownloadedBytes = 1_250_000_000, ExpectedSize = 5_000_000_000 };
            var saved = JsonSerializer.Serialize(job);
            Check(job.DownloadedSizeLabel == "1.25 GB" && job.TotalSizeLabel == "5.00 GB", "download progress and expected total in GB");
            Check(saved == JsonSerializer.Serialize(job) && !saved.Contains("SizeLabel"), "presentation never changes persisted job data");
            return Task.CompletedTask;
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
