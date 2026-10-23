using System.Globalization;
using System.Text.Json;

namespace PS5CatalogBrowser.Core;

public sealed record OrbitCatalogResult(int SchemaVersion, int FeedVersion, long Revision, List<CatalogEntry> Entries)
{
    public string ContentFingerprint { get; init; } = "";
}

public static class OrbitCatalogParser
{
    public const int MaximumBytes = CatalogParser.MaximumBytes;
    public const int MaximumReleases = 10000;

    public static OrbitCatalogResult Parse(CatalogSource source, ReadOnlySpan<byte> authenticatedPlaintext)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("A source identity is required.", nameof(source));
        if (authenticatedPlaintext.Length is 0 or > MaximumBytes) throw Error("input length");
        try
        {
            using var document = JsonDocument.Parse(authenticatedPlaintext.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            Object(root);
            var schema = Integer(root, "schemaVersion", true);
            var feed = Integer(root, "feedVersion", true);
            var revision = Integer(root, "revision", true);
            if (schema != 1) throw Error("unsupported schemaVersion");
            if (feed is not (3 or 4 or 5 or 6 or 7)) throw Error("unsupported feedVersion");
            if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) throw Error("releases");
            if (releases.GetArrayLength() > MaximumReleases) throw Error("release count");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var games = new Dictionary<string, (CatalogEntry Entry, string Metadata)>(StringComparer.Ordinal);
            foreach (var row in releases.EnumerateArray())
            {
                Object(row);
                var releaseId = Text(row, "id", true);
                if (!ids.Add(releaseId)) throw Error("duplicate release id");
                var gameId = Text(row, "gameId", true);
                var platform = Text(row, "platform", true);
                if (platform is not ("ps4" or "ps5")) throw Error("platform");
                var entry = new CatalogEntry
                {
                    Id = Identity(source.Id, "game", platform, gameId), GameId = gameId,
                    CatalogFormat = "Orbit", SourceId = source.Id, SourceName = source.Name,
                    Platform = platform.ToUpperInvariant(), Title = Text(row, "title", true),
                    Description = Text(row, "description"), Publisher = Text(row, "publisher"),
                    Genre = Text(row, "genre"), Tagline = Text(row, "tagline"),
                    ReleaseDate = Date(row, "releaseDate"), AddedAt = Timestamp(row, "addedAt"),
                    NewOnOrbitRank = Rank(row), CoverUrl = Url(row, "cover"), HeroUrl = Url(row, "hero"),
                    CoverFallbackUrl = Url(row, "coverFallback"), HeroFallbackUrl = Url(row, "heroFallback"),
                    ArtworkLayout = Choice(row, "artworkLayout", "wide", "ambient")
                };
                var artifact = new CatalogArtifact
                {
                    Id = Identity(source.Id, "release", releaseId), TitleId = Text(row, "titleId"),
                    FileName = Text(row, "filename", true), Url = Url(row, "url", true), BrowserUrl = Url(row, "browserUrl"),
                    Size = Integer(row, "sizeBytes", true, 1), Format = Choice(row, "format", "FFPFSC", "exFAT", "FPKG", "PKG", "Game folder", "Unknown"),
                    Version = Text(row, "version"), Region = Choice(row, "region", "EUR", "USA", "JPN", "ASIA"),
                    ProviderSourceId = Text(row, "sourceId"), Provider = Text(row, "provider"),
                    Sha256 = Sha(row), Compression = Choice(row, "compression", "zip", "rar", "7z"),
                    ExtractedBytes = Integer(row, "extractedBytes", false, 1), ArchiveContents = Choice(row, "archiveContents", "unverified"),
                    Notes = Text(row, "notes"), SourceCheckedAt = Timestamp(row, "sourceCheckedAt"),
                    SourceVerification = Text(row, "sourceVerification"), UpstreamConsoleVerified = Boolean(row, "consoleVerified"),
                    ArchivePassword = Optional(row, "archivePassword") is null ? null : Text(row, "archivePassword")
                };
                artifact.Name = artifact.FileName;
                var metadata = JsonSerializer.Serialize(entry);
                if (!games.TryGetValue(entry.Id, out var existing))
                    games.Add(entry.Id, existing = (entry, metadata));
                else if (existing.Metadata != metadata) throw Error("conflicting game metadata");
                existing.Entry.Artifacts.Add(artifact);
            }
            var entries = games.Values.Select(x => x.Entry).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            foreach (var entry in entries) entry.Artifacts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            return new(1, (int)feed!.Value, revision!.Value, entries)
            {
                ContentFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(releases.EnumerateArray()
                        .OrderBy(row => row.GetProperty("id").GetString(), StringComparer.Ordinal)
                        .Select(row => row.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                            .ToDictionary(p => p.Name, p => p.Value)))))
            };
        }
        catch (JsonException) { throw Error("invalid JSON"); }
    }

    private static string Identity(string source, string kind, params string[] components) =>
        Uri.EscapeDataString(source) + ":orbit:" + kind + ":" + string.Join(":", components.Select(Uri.EscapeDataString));

    private static InvalidDataException Error(string field) => new("Invalid Orbit catalogue: " + field + ".");

    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error("expected object");
        // Do not report untrusted property names, which could contain secrets.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw Error("duplicate property");
    }

    private static JsonElement? Optional(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string Text(JsonElement row, string field, bool required = false)
    {
        var value = Optional(row, field);
        if (value is null) { if (required) throw Error(field); return ""; }
        if (value.Value.ValueKind != JsonValueKind.String) throw Error(field);
        var text = value.Value.GetString()!;
        if (required && string.IsNullOrWhiteSpace(text)) throw Error(field);
        return text;
    }

    private static long? Integer(JsonElement row, string field, bool required = false, long minimum = 0)
    {
        var value = Optional(row, field);
        if (value is null) { if (required) throw Error(field); return null; }
        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt64(out var number) || number < minimum) throw Error(field);
        return number;
    }

    private static int? Rank(JsonElement row)
    {
        var value = Integer(row, "newOnOrbitRank");
        if (value > int.MaxValue) throw Error("newOnOrbitRank");
        return (int?)value;
    }

    private static string Choice(JsonElement row, string field, params string[] allowed)
    {
        var text = Text(row, field);
        if (text.Length > 0 && !allowed.Contains(text, StringComparer.Ordinal)) throw Error(field);
        return text;
    }

    private static string Url(JsonElement row, string field, bool required = false)
    {
        var text = Text(row, field, required);
        if (text.Length > 0 && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)) throw Error(field);
        return text;
    }

    private static DateOnly? Date(JsonElement row, string field)
    {
        var text = Text(row, field);
        if (text.Length == 0) return null;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw Error(field);
        return date;
    }

    private static DateTimeOffset? Timestamp(JsonElement row, string field)
    {
        var text = Text(row, field);
        if (text.Length == 0) return null;
        if (!text.Contains('T') || !(text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw Error(field);
        return date;
    }

    private static bool? Boolean(JsonElement row, string field)
    {
        var value = Optional(row, field);
        if (value is null) return null;
        if (value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error(field);
        return value.Value.GetBoolean();
    }

    private static string Sha(JsonElement row)
    {
        var text = Text(row, "sha256");
        if (text.Length > 0 && (text.Length != 64 || !text.All(char.IsAsciiHexDigit))) throw Error("sha256");
        return text.ToLowerInvariant();
    }
}
