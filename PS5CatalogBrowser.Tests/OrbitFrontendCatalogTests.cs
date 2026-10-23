using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class OrbitFrontendCatalogTests
{
    public static Task RunAsync()
    {
        var source = new CatalogSource { Id = "source", Name = "Fixture catalogue" };
        var reports = KytyCompatibility.ParseReports(System.Text.Encoding.UTF8.GetBytes("""
            {"version":2,"titleId":"PPSA03647","reports":[{"titleId":"PPSA03647","status":"logo","testedVersion":"KytyPS5-2026-10-16-5dec523","os":"windows","testedDate":"2026-10-16"},{"titleId":"PPSA03647","status":"in-game","os":"linux"}]}
            """), "PPSA03647");
        Check(reports[0].TestedVersion == "KytyPS5-2026-10-16-5dec523" && reports[0].Status == "logo" && reports[0].Os == "windows" && reports[1].TestedVersion == "", "website versions remain associated with their own OS/status; missing version stays unknown");
        try { KytyCompatibility.ParseReports(System.Text.Encoding.UTF8.GetBytes("""{"version":2,"titleId":"PPSA00001","reports":[]}"""), "PPSA03647"); throw new Exception("Wrong title report accepted"); } catch (InvalidDataException) { }
        Check(KytyVersion.Comparison("KytyPS5-2026-10-16-5dec523 (source build)", "KytyPS5-2026-10-16-5dec523", "logo") == "current" &&
            KytyVersion.Comparison("KytyPS5-2026-10-15-1234567", "KytyPS5-2026-10-16-5dec523", "in-game") == "older" &&
            KytyVersion.Comparison("KytyPS5-2026-10-17-1234567", "KytyPS5-2026-10-16-5dec523", "in-game") == "newer" &&
            KytyVersion.Comparison("KytyPS5-2026-10-16-1234567", "KytyPS5-2026-10-16-5dec523", "in-game") == "different" &&
            KytyVersion.Comparison("0.3.0", "KytyPS5-2026-10-16-5dec523", "in-game") == "unknown" &&
            KytyVersion.Comparison("0.3.0", "0.3.0", "untested") == "unknown", "dated builds distinguish older/newer from same-day differences; only matching tested versions are current");
        var compatibility = KytyCompatibility.Parse(System.Text.Encoding.UTF8.GetBytes("""
            {"version":2,"games":[{"titleId":"PPSA-03647","overall":"in-game"},{"titleId":"PPSA00001","overall":"logo"}]}
            """));
        Check(compatibility["PPSA03647"] == "in-game" && KytyCompatibility.NormalizeId("ppsa-03647") == "PPSA03647" &&
            KytyCompatibility.NormalizeId("game PPSA03647") == "", "only exact normalized PPSA identities match compatibility");
        var compatibilityEntry = new CatalogEntry { SourceId = source.Id, Id = "compatibility", Title = "Different title", TitleId = "PPSA03647", Platform = "PS5" };
        var withCompatibility = OrbitFrontendCatalog.Create([compatibilityEntry], [source], compatibility);
        Check(withCompatibility.Releases.Single().WindowsCompatibility == "in-game", "exact ID supplies reported overall status without title guessing");
        Check(OrbitFrontendCatalog.Create([compatibilityEntry], [source]).Releases.Single().WindowsCompatibility == "unavailable", "unavailable feed does not imply untested");
        compatibilityEntry.TitleId = "PPSA99999";
        Check(OrbitFrontendCatalog.Create([compatibilityEntry], [source], compatibility).Releases.Single().WindowsCompatibility == "untested", "unmatched ID explicitly untested after successful feed load");
        Check(withCompatibility.Revision != OrbitFrontendCatalog.Create([compatibilityEntry], [source], compatibility).Revision, "compatibility is included in catalogue revision");
        foreach (var invalid in new[] { "{\"version\":9,\"games\":[]}", "{\"version\":2,\"games\":[{\"titleId\":\"PPSA03647\",\"overall\":\"playable\"}]}" })
        {
            try { KytyCompatibility.Parse(System.Text.Encoding.UTF8.GetBytes(invalid)); throw new Exception("Invalid compatibility feed accepted"); }
            catch (System.IO.InvalidDataException) { }
        }
        var entries = OrbitCatalogParser.Parse(source, LocalOrbitCatalogTests.Feed()).Entries;
        var snapshot = OrbitFrontendCatalog.Create(entries, [source]);
        Check(snapshot.Releases.Count == 3 && snapshot.Releases.Select(release => release.GameId).Distinct().Count() == 2, "platforms and alternatives are preserved");
        Check(snapshot.Releases.Select(release => release.Id).Distinct().Count() == 3, "distinct releases");
        var reversed = OrbitFrontendCatalog.Create(entries.AsEnumerable().Reverse(), [source]);
        Check(snapshot.Releases.Select(release => release.Id).Order().SequenceEqual(reversed.Releases.Select(release => release.Id).Order()), "refresh ordering cannot change IDs");
        foreach (var release in snapshot.Releases)
        {
            var entry = entries.Single(entry => entry.Id == release.WindowsIdentity.EntryId);
            var artifact = entry.Artifacts.Single(artifact => artifact.Id == release.WindowsIdentity.ArtifactId);
            Check(release.WindowsIdentity.SourceId == source.Id && release.Version == artifact.Version && release.Region == artifact.Region && release.Format == artifact.Format, "exact source/release metadata");
            Check(!release.ConsoleVerified && !release.DirectAvailable && !release.BrowserAvailable, "browsing grants no console or download capability");
        }
        var json = JsonSerializer.Serialize(snapshot);
        Check(!json.Contains("synthetic-password") && !json.Contains("provider.invalid"), "transfer URLs and credentials are not frontend data");
        Check(!json.Contains("WindowsIdentity") && snapshot.Releases.All(release => release.WindowsAvailable), "opaque release IDs route available native artifacts without exporting duplicate identity records");
        var otherSource = new CatalogSource { Id = "other", Name = source.Name };
        var other = OrbitCatalogParser.Parse(otherSource, LocalOrbitCatalogTests.Feed()).Entries;
        Check(OrbitFrontendCatalog.Create(entries.Concat(other), [source, otherSource]).Releases.Select(release => release.GameId).Distinct().Count() == 4, "same titles in different sources cannot merge");
        source.Enabled = false;
        Check(OrbitFrontendCatalog.Create(entries, [source]).Releases.Count == 0, "disabled sources stay hidden"); source.Enabled = true;
        var missing = new CatalogEntry { SourceId = source.Id, Id = "missing", CoverUrl = "file:///C:/secret", HeroUrl = "javascript:alert(1)" };
        var metadata = OrbitFrontendCatalog.Create([missing], [source]).Releases.Single();
        Check(metadata.SizeBytes == -1 && metadata.Title == "Untitled catalogue entry" && metadata.Cover == "" && metadata.Hero == "" && metadata.WindowsIdentity.ArtifactId is null && !metadata.WindowsAvailable, "missing metadata and unsafe art are explicit");
        Check(OrbitFrontendCatalog.Create([missing, missing], [source]).Releases.Count == 0, "ambiguous entry IDs are omitted");
        missing.Artifacts = [new() { Id = "duplicate" }, new() { Id = "duplicate" }];
        Check(OrbitFrontendCatalog.Create([missing], [source]).Issues.Count == 1, "ambiguous release IDs are reported");
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
}
