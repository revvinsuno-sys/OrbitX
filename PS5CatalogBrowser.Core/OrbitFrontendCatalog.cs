using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PS5CatalogBrowser.Core;

public sealed record OrbitFrontendIdentity(string SourceId, string EntryId, string? ArtifactId, string Platform,
    string TitleId, string Version, string Region, string Provider, string Format);
public sealed record OrbitFrontendRelease(string Id, string GameId, string Platform, string TitleId, string Title,
    string? Genre, string? Tagline, string? Description, long SizeBytes, string Provider, string SourceId, string Format,
    string? Compression, long? ExtractedBytes, string? Notes, string? Region, string? Version, string Filename,
    string Cover, string Hero, string? CoverFallback, string? HeroFallback, string ArtworkLayout,
    string? Publisher, string? ReleaseDate, string? AddedAt, int? NewOnOrbitRank, bool ConsoleVerified,
    bool BrowserAvailable, bool DirectAvailable, [property: JsonIgnore] OrbitFrontendIdentity WindowsIdentity)
{
    public bool WindowsAvailable => WindowsIdentity.ArtifactId is not null;
    public string WindowsCompatibility { get; init; } = "unavailable";
    public bool WindowsCompatibilityLocal { get; init; }
    public string WindowsCompatibilityVersion { get; init; } = "";
    public string WindowsCompatibilityVersionMatch { get; init; } = "unknown";
    public string WindowsWebsiteCompatibility { get; init; } = "unavailable";
    public string WindowsKytyLocalCompatibility { get; init; } = "untested";
    public string WindowsSharpEmuCompatibility { get; init; } = "unavailable";
    public string WindowsSharpEmuLocalCompatibility { get; init; } = "untested";
    public string WindowsSharpEmuCompatibilityVersion { get; init; } = "";
}
public sealed record OrbitFrontendSource(string Id, string Label, int ReleaseCount, bool DownloadReady);
public sealed record OrbitFrontendSnapshot(List<OrbitFrontendRelease> Releases, List<OrbitFrontendSource> Sources, uint Revision, List<string> Issues);

// Presentation only. Never reads catalogue files/keys, creates jobs or derives identity from titles/filenames.
public static class OrbitFrontendCatalog
{
    public static OrbitFrontendSnapshot Create(IEnumerable<CatalogEntry> entries, IEnumerable<CatalogSource> sources, IReadOnlyDictionary<string, string>? compatibility = null,
        IReadOnlyDictionary<string, string>? compatibilityOverrides = null,
        IReadOnlyDictionary<string, CompatibilityTestVersion>? compatibilityVersions = null, string currentKytyVersion = "",
        IReadOnlyDictionary<string, string>? sharpEmuCompatibility = null,
        IReadOnlyDictionary<string, string>? sharpEmuCompatibilityOverrides = null,
        IReadOnlyDictionary<string, CompatibilityTestVersion>? sharpEmuCompatibilityVersions = null)
    {
        var enabled = sources.Where(source => source.Enabled).Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var releases = new List<OrbitFrontendRelease>(); var issues = new List<string>();
        var providers = new Dictionary<(string SourceId, string Provider), (string Id, string Label)>();
        var groups = entries.Where(entry => enabled.Contains(entry.SourceId)).GroupBy(entry => Key(entry.SourceId, entry.Id, entry.Platform));
        foreach (var group in groups)
        {
            var entry = group.First();
            if (group.Count() != 1 || string.IsNullOrEmpty(entry.SourceId) || string.IsNullOrEmpty(entry.Id))
            { issues.Add("An ambiguous source/entry identity was omitted. Inspect it in Classic Windows."); continue; }
            var platform = entry.Platform.ToLowerInvariant();
            if (platform is not ("ps4" or "ps5"))
            { issues.Add("A platform unsupported by Orbit's PS4/PS5 layouts is available in Classic Windows."); continue; }
            if (entry.Artifacts.Any(artifact => string.IsNullOrEmpty(artifact.Id)) || entry.Artifacts.GroupBy(artifact => artifact.Id, StringComparer.Ordinal).Any(set => set.Count() != 1))
            { issues.Add("An entry with ambiguous release IDs was omitted. Inspect it in Classic Windows."); continue; }
            var gameId = "win-game-" + group.Key;
            var cover = Art(entry.CoverUrl); var hero = Art(entry.HeroUrl);
            var coverFallback = Optional(Art(entry.CoverFallbackUrl)); var heroFallback = Optional(Art(entry.HeroFallbackUrl));
            foreach (var artifact in entry.Artifacts.Count == 0 ? new CatalogArtifact?[] { null } : entry.Artifacts.Cast<CatalogArtifact?>())
            {
                var provider = First(artifact?.Provider, artifact?.Host, entry.SourceName, "Source not specified");
                if (!providers.TryGetValue((entry.SourceId, provider), out var source))
                    providers.Add((entry.SourceId, provider), source = ("win-source-" + Key(entry.SourceId, provider), First(entry.SourceName, "Catalogue") + " · " + provider));
                var format = First(artifact?.Format, entry.Format, "Unknown");
                var titleId = First(artifact?.TitleId, entry.TitleId);
                var version = First(artifact?.Version, entry.Version); var region = First(artifact?.Region, entry.Region);
                releases.Add(new("win-release-" + Key(entry.SourceId, entry.Id, entry.Platform, artifact?.Id ?? "@metadata"), gameId,
                    platform, titleId, First(entry.Title, "Untitled catalogue entry"), Optional(entry.Genre), Optional(entry.Tagline), Optional(entry.Description),
                    artifact?.Size ?? entry.Size ?? -1, source.Label, source.Id, format, Optional(artifact?.Compression), artifact?.ExtractedBytes,
                    Optional(artifact?.Notes), Optional(region), Optional(version), artifact?.FileName ?? "", cover, hero,
                    coverFallback, heroFallback, entry.ArtworkLayout == "wide" ? "wide" : "ambient",
                    Optional(entry.Publisher), entry.ReleaseDate?.ToString("yyyy-MM-dd"), entry.AddedAt?.ToString("O"), entry.NewOnOrbitRank,
                    false, false, false, new(entry.SourceId, entry.Id, artifact?.Id, entry.Platform, titleId, version, region, provider, format))
                    { WindowsCompatibility = compatibilityOverrides?.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId)) is { } local && KytyCompatibility.Statuses.Contains(local)
                        ? local : compatibility is null ? "unavailable" : compatibility.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId), "untested"),
                      WindowsCompatibilityLocal = compatibilityOverrides?.TryGetValue(KytyCompatibility.NormalizeId(titleId), out var overridden) == true && KytyCompatibility.Statuses.Contains(overridden),
                      WindowsCompatibilityVersion = compatibilityVersions?.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId))?.Version ?? "",
                      WindowsCompatibilityVersionMatch = KytyVersion.Comparison(compatibilityVersions?.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId))?.Version ?? "", currentKytyVersion, compatibilityOverrides?.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId)) ?? "untested"),
                      WindowsWebsiteCompatibility = compatibility is null ? "unavailable" : compatibility.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId), "untested"),
                      WindowsKytyLocalCompatibility = compatibilityOverrides?.GetValueOrDefault(KytyCompatibility.NormalizeId(titleId)) is { } kytyLocal && KytyCompatibility.Statuses.Contains(kytyLocal)
                        ? kytyLocal : "untested",
                      WindowsSharpEmuCompatibility = sharpEmuCompatibility is null ? "unavailable" : sharpEmuCompatibility.GetValueOrDefault(SharpEmuCompatibility.NormalizeId(titleId), "untested"),
                      WindowsSharpEmuLocalCompatibility = sharpEmuCompatibilityOverrides?.GetValueOrDefault(SharpEmuCompatibility.NormalizeId(titleId)) is { } sharpLocal && SharpEmuCompatibility.Statuses.Contains(sharpLocal, StringComparer.Ordinal)
                        ? sharpLocal : "untested",
                      WindowsSharpEmuCompatibilityVersion = sharpEmuCompatibilityVersions?.GetValueOrDefault(SharpEmuCompatibility.NormalizeId(titleId))?.Version ?? "" });
            }
        }
        var options = releases.GroupBy(release => release.SourceId).Select(group => new OrbitFrontendSource(group.Key, group.First().Provider, group.Count(), false)).ToList();
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(releases));
        return new(releases, options, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash), issues.Distinct().ToList());
    }
    private static string Key(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts)))).ToLowerInvariant();
    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static string Art(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return NetworkPolicy.Validate(value).AbsoluteUri; } catch { return ""; }
    }
}
