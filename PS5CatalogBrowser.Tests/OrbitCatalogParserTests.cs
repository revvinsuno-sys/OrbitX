using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PS5CatalogBrowser.Core;

internal static class OrbitCatalogParserTests
{
    private static readonly CatalogSource Source = new() { Id = "synthetic:orbit", Name = "Synthetic Orbit" };

    public static Task RunAsync()
    {
        foreach (var feed in new[] { 3, 4, 5, 6, 7 })
        {
            var result = Parse(Feed(feed, Row("release:1")));
            Check(result.SchemaVersion == 1 && result.FeedVersion == feed && result.Revision == 28, "root metadata");
            var game = result.Entries.Single();
            var release = game.Artifacts.Single();
            Check(game.GameId == "game:1" && game.Platform == "PS5" && game.Title == "Synthetic game", "game fields");
            Check(release.Id == "synthetic%3Aorbit:orbit:release:release%3A1" && release.Size == 1234 && release.FileName == "synthetic.part01.rar", "release identity");
            Check(game.TitleId == "" && game.Version == "" && game.Size is null, "no first-release projection");
            Check(release.PartNumber is null && release.PartCount is null && release.Mirrors.Count == 0, "no multipart or mirrors");
            Check(release.Sha256 == "" && release.UpstreamConsoleVerified is null, "missing verification");
        }

        var v7Provider = Row("v7-provider");
        v7Provider["provider"] = "1fichier"; v7Provider["sourceId"] = "1fichier";
        v7Provider["url"] = "https://1fichier.com/?synthetic";
        v7Provider["browserUrl"] = "https://1fichier.com/?synthetic-browser";
        var v7Artifact = Parse(Feed(7, v7Provider)).Entries.Single().Artifacts.Single();
        Check(v7Artifact.ProviderSourceId == "1fichier" && v7Artifact.Provider == "1fichier" &&
            v7Artifact.Url != v7Artifact.BrowserUrl && OrbitDownloadPolicy.Workflow(v7Artifact) == ReleaseWorkflow.External,
            "v7 provider metadata retains URL roles and existing external workflow");

        var one = Row("a");
        one["version"] = "1.0"; one["region"] = "EUR"; one["format"] = "exFAT";
        var two = Row("b");
        two["version"] = "2.0"; two["region"] = "USA"; two["format"] = "FFPFSC";
        var three = Row("c");
        three["platform"] = "ps4";
        var four = Row("d"); four["gameId"] = "another-game";
        var forward = Parse(Feed(6, one, two, three, four));
        var reverse = Parse(Feed(6, four, three, two, one));
        Check(JsonSerializer.Serialize(forward) == JsonSerializer.Serialize(reverse), "order-independent normalisation");
        Check(forward.Entries.Count == 3 && forward.Entries.Sum(x => x.Artifacts.Count) == 4, "explicit platform/game grouping");
        var alternatives = forward.Entries.Single(x => x.GameId == "game:1" && x.Platform == "PS5");
        Check(alternatives.Artifacts.Count == 2 && alternatives.Artifacts.Select(x => x.TitleId).Distinct().Count() == 1, "same title ID remains separate");
        Check(alternatives.Artifacts.Select(x => x.Version).SequenceEqual(new[] { "1.0", "2.0" }) &&
            alternatives.Artifacts.Select(x => x.Region).SequenceEqual(new[] { "EUR", "USA" }), "release versions and regions");
        var captures = alternatives.Artifacts.Select(x => DownloadAdmission.Capture(alternatives, x)).ToArray();
        Check(captures.Distinct().Count() == 2, "A2 alternatives distinct");
        var again = reverse.Entries.Single(x => x.Id == alternatives.Id);
        Check(captures.SequenceEqual(again.Artifacts.Select(x => DownloadAdmission.Capture(again, x))), "stable A2 captures");
        var regional = Row("regional"); regional["titleId"] = "PPSA00001"; regional["region"] = "JPN";
        var regionalGame = Parse(Feed(6, one, two, regional)).Entries.Single();
        Check(regionalGame.Artifacts.Count == 3 && regionalGame.Artifacts.Single(x => x.Region == "JPN").TitleId == "PPSA00001", "per-release title IDs");
        Check(regionalGame.Artifacts.All(x => x.PartNumber is null && x.PartCount is null && x.Mirrors.Count == 0), "all alternatives independent");
        var otherSource = OrbitCatalogParser.Parse(new CatalogSource { Id = "other" }, Bytes(Feed(6, Row("a"))));
        Check(otherSource.Entries.Single().Id != alternatives.Id && otherSource.Entries.Single().Artifacts.Single().Id != alternatives.Artifacts[0].Id, "source scope");

        var rich = Row("rich");
        rich["publisher"] = "Synthetic publisher"; rich["genre"] = "Adventure"; rich["tagline"] = "Synthetic tagline";
        rich["description"] = "Synthetic description"; rich["releaseDate"] = "2026-01-02";
        rich["addedAt"] = "2026-01-03T12:34:56Z"; rich["newOnOrbitRank"] = 2;
        rich["cover"] = "https://art.invalid/cover"; rich["hero"] = "https://art.invalid/hero";
        rich["coverFallback"] = "https://art.invalid/fallback"; rich["heroFallback"] = "https://art.invalid/hero-fallback";
        rich["artworkLayout"] = "ambient"; rich["browserUrl"] = "https://provider.invalid/page?token=synthetic";
        rich["provider"] = "Synthetic provider"; rich["sourceId"] = "synthetic-provider";
        rich["compression"] = "rar"; rich["extractedBytes"] = 9000; rich["archiveContents"] = "unverified";
        rich["archivePassword"] = "synthetic-secret"; rich["notes"] = "Synthetic notes";
        rich["sourceCheckedAt"] = "2026-01-04T00:00:00+00:00"; rich["sourceVerification"] = "file-metadata";
        rich["consoleVerified"] = true; rich["sha256"] = new string('A', 64);
        var richGame = Parse(Feed(6, rich)).Entries.Single();
        var artifact = richGame.Artifacts.Single();
        Check(richGame.Publisher == "Synthetic publisher" && richGame.Genre == "Adventure" && richGame.Tagline == "Synthetic tagline" &&
            richGame.Description == "Synthetic description" && richGame.ReleaseDate == new DateOnly(2026, 1, 2) &&
            richGame.AddedAt?.Day == 3 && richGame.NewOnOrbitRank == 2 && richGame.CoverUrl.EndsWith("/cover") &&
            richGame.HeroUrl.EndsWith("/hero") && richGame.CoverFallbackUrl.EndsWith("/fallback") &&
            richGame.HeroFallbackUrl.EndsWith("/hero-fallback") && richGame.ArtworkLayout == "ambient", "game metadata");
        Check(artifact.BrowserUrl == rich["browserUrl"]!.GetValue<string>() && artifact.Url != artifact.BrowserUrl && artifact.Mirrors.Count == 0, "address roles");
        Check(artifact.ProviderSourceId == "synthetic-provider" && artifact.Provider == "Synthetic provider" && artifact.SourceVerification == "file-metadata" &&
            artifact.SourceCheckedAt?.Day == 4 && artifact.UpstreamConsoleVerified == true && artifact.Sha256 == new string('a', 64), "upstream metadata");
        Check(artifact.Format == "Unknown" && artifact.Compression == "rar" && artifact.ExtractedBytes == 9000 &&
            artifact.ArchiveContents == "unverified" && artifact.Notes == "Synthetic notes" && artifact.ArchivePassword == "synthetic-secret", "archive fields");
        Check(richGame.Classification == "Unverified download", "upstream console flag does not establish compatibility");
        var serialized = JsonSerializer.Serialize(richGame);
        Check(!serialized.Contains("synthetic-secret") && !serialized.Contains("ArchivePassword"), "password omitted");
        var roundtrip = JsonSerializer.Deserialize<CatalogEntry>(serialized)!;
        Check(roundtrip.Artifacts.Single().ArchivePassword is null && roundtrip.Artifacts.Single().BrowserUrl == artifact.BrowserUrl &&
            roundtrip.Artifacts.Single().UpstreamConsoleVerified == true && roundtrip.ReleaseDate == richGame.ReleaseDate, "metadata round trip");
        Check(JsonSerializer.Deserialize<CatalogArtifact>("""{"ArchivePassword":"synthetic-secret"}""")!.ArchivePassword is null, "password deserialization ignored");
        rich["compression"] = "7z"; rich["browserUrl"] = rich["url"]!.DeepClone(); rich["sourceVerification"] = "range-probe";
        artifact = Parse(Feed(6, rich)).Entries.Single().Artifacts.Single();
        Check(artifact.Compression == "7z" && artifact.Url == artifact.BrowserUrl && artifact.SourceVerification == "range-probe", "same addresses and upstream route assertion");
        rich["sizeBytes"] = false; Reject(Feed(6, rich), "error with sensitive metadata");

        var nullable = Row("nullable");
        foreach (var field in new[] { "genre", "version", "releaseDate", "addedAt", "consoleVerified", "archivePassword", "browserUrl" }) nullable[field] = null;
        Check(Parse(Feed(3, nullable)).Entries.Single().Artifacts.Single().Version == "", "optional nulls");
        Reject(Feed(6, Row("duplicate"), Row("duplicate")), "duplicate IDs");
        var conflict = Row("other"); conflict["title"] = "Conflicting game title";
        Reject(Feed(6, Row("first"), conflict), "conflicting game metadata");
        foreach (var field in new[] { "id", "gameId", "platform", "title", "filename", "url", "sizeBytes" })
        {
            var missing = Row("bad"); missing.Remove(field); Reject(Feed(6, missing), "missing " + field);
            var invalid = Row("bad"); invalid[field] = false; Reject(Feed(6, invalid), "wrong type " + field);
        }
        foreach (var field in new[] { "titleId", "publisher", "genre", "tagline", "description", "version", "region", "provider", "sourceId", "compression",
            "archiveContents", "notes", "archivePassword", "sourceVerification", "cover", "hero", "coverFallback", "heroFallback", "artworkLayout", "sha256",
            "releaseDate", "addedAt", "sourceCheckedAt", "browserUrl" })
        { var invalid = Row("bad"); invalid[field] = new JsonObject(); Reject(Feed(6, invalid), "metadata type " + field); }
        foreach (var (field, value) in new (string, JsonNode?)[] {
            ("sizeBytes", 0), ("sizeBytes", -1), ("sizeBytes", 1.5), ("sizeBytes", "1234"), ("extractedBytes", 0),
            ("extractedBytes", false), ("newOnOrbitRank", "1"),
            ("newOnOrbitRank", 2147483648L), ("newOnOrbitRank", -1), ("consoleVerified", "true"), ("platform", "ps3"),
            ("compression", "exe"), ("artworkLayout", "invalid"), ("archiveContents", "verified"), ("sha256", "short"),
            ("releaseDate", "2026-02-30"), ("addedAt", "2026-01-01"), ("sourceCheckedAt", "2026-01-01T12:00:00"),
            ("url", "file:///synthetic-secret"), ("url", "javascript:synthetic-secret"), ("url", "/relative"),
            ("browserUrl", "ftp://provider.invalid/synthetic-secret"), ("hero", "invalid"), ("title", " ") })
        { var invalid = Row("bad"); invalid[field] = value; Reject(Feed(6, invalid), "invalid " + field); }
        foreach (var feed in new[] { 0, 1, 2, 8 }) Reject(Feed(feed, Row("bad")), "unsupported feed");
        foreach (var (field, value) in new (string, JsonNode?)[] { ("schemaVersion", 2), ("schemaVersion", "1"), ("revision", 1.5), ("revision", -1), ("releases", false) })
        { var invalid = Feed(6, Row("bad")); invalid[field] = value; Reject(invalid, "root " + field); }
        RejectBytes("[]"u8.ToArray()); RejectBytes("{"u8.ToArray());
        RejectBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1}"));
        RejectBytes(new byte[OrbitCatalogParser.MaximumBytes + 1]);
        RejectBytes(Encoding.UTF8.GetBytes(new string('[', 33) + new string(']', 33)));
        var many = Feed(6); var rows = (JsonArray)many["releases"]!;
        for (var i = 0; i <= OrbitCatalogParser.MaximumReleases; i++) rows.Add(Row(i.ToString()));
        Reject(many, "release count");
        Check(Parse(Feed(6)).Entries.Count == 0, "empty feed");

        var legacy = JsonSerializer.Deserialize<CatalogEntry>("""{"Id":"legacy","Title":"Old","Artifacts":[{"Id":"old-file","Url":"https://example.invalid/file"}]}""")!;
        Check(legacy.GameId == "" && legacy.ReleaseDate is null && legacy.Artifacts.Single().BrowserUrl == "" && legacy.Artifacts.Single().UpstreamConsoleVerified is null, "legacy model defaults");
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Downloads":[{"Id":"old-job"}],"Library":[{"Id":"old-library"}]}""")!;
        var settingsAgain = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Check(settingsAgain.Downloads.Single().Id == "old-job" && settingsAgain.Library.Single().Id == "old-library", "legacy settings round trip");
        return Task.CompletedTask;
    }

    private static JsonObject Row(string id) => new()
    {
        ["id"] = id, ["gameId"] = "game:1", ["platform"] = "ps5", ["title"] = "Synthetic game", ["titleId"] = "PPSA00000",
        ["filename"] = "synthetic.part01.rar", ["sizeBytes"] = 1234, ["format"] = "Unknown", ["url"] = "https://provider.invalid/route"
    };
    private static JsonObject Feed(int feed, params JsonObject[] rows) => new()
    {
        ["schemaVersion"] = 1, ["feedVersion"] = feed, ["revision"] = 28,
        ["releases"] = new JsonArray(rows.Select(x => (JsonNode)x.DeepClone()).ToArray())
    };
    private static byte[] Bytes(JsonObject feed) => Encoding.UTF8.GetBytes(feed.ToJsonString());
    private static OrbitCatalogResult Parse(JsonObject feed) => OrbitCatalogParser.Parse(Source, Bytes(feed));
    private static void Reject(JsonObject feed, string reason) => RejectBytes(Bytes(feed), reason);
    private static void RejectBytes(byte[] data, string reason = "invalid JSON")
    {
        try { OrbitCatalogParser.Parse(Source, data); }
        catch (InvalidDataException ex)
        {
            Check(ex.InnerException is null && !ex.Message.Contains("synthetic-secret") && !ex.Message.Contains("provider.invalid"), "redacted error");
            return;
        }
        throw new Exception("Expected schema rejection: " + reason);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
