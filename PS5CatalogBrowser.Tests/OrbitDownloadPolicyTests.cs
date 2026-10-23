using PS5CatalogBrowser.Core;
using static LocalOrbitCatalogTests;

internal static class OrbitDownloadPolicyTests
{
    private static void Check(bool value, string message) => LocalOrbitCatalogTests.Check(value, message);
    public static Task RunAsync()
    {
        var entry = OrbitCatalogParser.Parse(new() { Id = "local" }, Feed()).Entries.Single(e => e.Platform == "PS5");
        var a = entry.Artifacts[0]; var b = entry.Artifacts[1];
        Check(a.FileName == b.FileName && DownloadAdmission.Capture(entry, a) != DownloadAdmission.Capture(entry, b), "same filename, distinct release identity/version/region");
        foreach (var url in new[] { "https://archive.org/download/item/file.exfat", "https://archive.org/download/item/folder/file.zip" })
        {
            a.Url = url; a.BrowserUrl = "https://vikingfile.com/f/synthetic";
            Check(OrbitDownloadPolicy.Workflow(a) == ReleaseWorkflow.Direct && DownloadMirror.ForEntry(entry)[0].Url == url, "direct role takes precedence");
        }
        foreach (var url in new[] { "https://archive.org/details/item", "https://archive.org/download/item", "https://archive.org/download/item/",
            "https://archive.org.evil.invalid/download/item/file", "https://user:password@archive.org/download/item/file", "http://archive.org/download/item/file",
            "https://archive.org:444/download/item/file", "https://unknown.invalid/file.pkg" })
            Check(!OrbitDownloadPolicy.DirectUrl(url), "not an explicit direct route");
        a.Provider = "Archive.org"; a.Url = "https://unknown.invalid/file.pkg"; a.BrowserUrl = "";
        Check(OrbitDownloadPolicy.Workflow(a) == ReleaseWorkflow.External, "provider label is not authority");
        foreach (var page in new[] { "https://vikingfile.com/f/synthetic", "https://akirabox.com/file/synthetic", "https://akirabox.com/synthetic/download",
            "https://akirabox.com/AbC123/file", "https://www.akirabox.com/AbC123/file", "https://akirabox.com/AbC123/file?source=share",
            "https://akirabox.to/AbC123/file", "https://www.akirabox.to/AbC123/file",
            "https://datanodes.to/8k2m9xq4w7v1" })
        {
            a.Url = "https://unknown.invalid/file.pkg";
            a.BrowserUrl = page;
            Check(OrbitDownloadPolicy.Workflow(a) == ReleaseWorkflow.Browser && OrbitDownloadPolicy.BrowserUrl(a) == page && a.Url.Contains("unknown.invalid"), "distinct browser URL role");
            a.Url = page; a.BrowserUrl = "";
            Check(OrbitDownloadPolicy.Workflow(a) == ReleaseWorkflow.Browser, "landing URL without browserUrl");
            Check(!OrbitDownloadPolicy.DirectUrl(page), "landing page is never a direct file");
        }
        foreach (var page in new[] { "https://akirabox.com/", "https://akirabox.com/login", "https://akirabox.com/AbC123/unknown",
            "https://akirabox.com/AbC123/file/extra", "https://akirabox.com/AbC123//file", "https://akirabox.com//AbC123/file",
            "https://akirabox.com/abc-123/file", "https://akirabox.com/abc_123/file", "https://akirabox.com/%C3%A9/file",
            "https://akirabox.com/AbC123/File", "https://akirabox.com/download/AbC123/fixture.pkg?token=generated",
            "https://download.akirabox.com/AbC123/file", "https://akirabox.com.evil.invalid/AbC123/file",
            "https://www.akirabox.com.evil.invalid/AbC123/file", "https://evilakirabox.com/AbC123/file",
            "https://user:password@akirabox.com/AbC123/file", "http://akirabox.com/AbC123/file", "https://akirabox.com:444/AbC123/file",
            "https://vikingfile.com/login", "https://vikingfile.com/f/id/extra", "https://datanodes.to/short",
            "https://filekeeper.net/abc123/fixture.pkg", "https://datavaults.co/synthetic" })
        {
            a.Url = page; a.BrowserUrl = "";
            Check(OrbitDownloadPolicy.Workflow(a) == ReleaseWorkflow.External && !OrbitDownloadPolicy.DirectUrl(page), "unknown or unsafe route retains external fallback: " + page);
        }
        const string selected = "https://akirabox.to/AbC123/file";
        const string ready = "https://akirabox.com/download/token/fixture.pkg?expiration=123&t=temporary";
        Check(AkiraBoxLink.Ready(selected, "https://akirabox.com/AbC123/file", ready, "fixture.pkg"), "same file across official hosts permits ready-link navigation");
        Check(AkiraBoxLink.Ready(selected, selected, ready.Replace("fixture.pkg", "fixture+%2B+package.pkg"), "fixture + package.pkg"), "Akira form-encoded spaces and literal plus match the expected filename");
        Check(AkiraBoxLink.Ready(selected, selected, ready.Replace("fixture.pkg", "fixture%20package.pkg"), "fixture package.pkg"), "percent-encoded spaces still match");
        Check(!AkiraBoxLink.Ready(selected, selected, ready.Replace("fixture.pkg", "fixture+package.pkg"), "fixture+package.pkg"), "unescaped plus cannot match a literal plus filename");
        foreach (var candidate in new[] { ready.Replace("fixture.pkg", "other.pkg"), ready.Replace("akirabox.com", "akirabox.com.evil.invalid"),
            ready.Replace("https://", "http://"), ready.Replace("/token/", "/token/extra/"), ready + "#fragment",
            ready.Replace("fixture.pkg", "folder%2Ffixture.pkg"), ready.Replace("fixture.pkg", "folder%5Cfixture.pkg"),
            ready.Replace("akirabox.com", "user:password@akirabox.com"), ready.Replace("akirabox.com", "akirabox.com:444") })
            Check(!AkiraBoxLink.Ready(selected, selected, candidate, "fixture.pkg"), "unsafe or mismatched ready link rejected");
        Check(!AkiraBoxLink.Ready(selected, selected.Replace("AbC123", "Other"), ready, "fixture.pkg") &&
            !AkiraBoxLink.Ready(selected, selected, ready, ""), "current page and explicit filename required");
        var generic = new DownloadMirror { Artifact = a, Url = selected, Provider = "Arbitrary label" };
        Check(!generic.DirectEnabled && generic.BrowserEnabled && generic.BrowserInstructions == AkiraBoxLink.Instructions, "Akira guidance and browser routing use URL, including non-Orbit catalogues");
        a.Url = "https://vikingfile.com/login";
        Check(OrbitDownloadPolicy.Workflow(a) == ReleaseWorkflow.External, "provider root/account routes are not file pages");
        var original = DownloadAdmission.Capture(entry, a);
        a.BrowserUrl = "https://vikingfile.com/f/changed";
        Check(original != DownloadAdmission.Capture(entry, a), "browser role bound to fingerprint");
        var admission = new DownloadAdmission();
        var owner = admission.Admit([DownloadAdmission.Capture(entry, a)], () => [new() { SourceId = entry.SourceId, EntryId = entry.Id,
            ArtifactId = a.Id, OrbitRelease = OrbitReleaseSelection.Capture(entry, a) }], _ => { }, _ => { })[0];
        a.Size++;
        var rollback = false;
        try
        {
            admission.Admit([DownloadAdmission.Capture(entry, a)], () => [new() { SourceId = entry.SourceId, EntryId = entry.Id,
                ArtifactId = a.Id, OrbitRelease = OrbitReleaseSelection.Capture(entry, a) }], _ => throw new Exception("Duplicate persisted"), _ => rollback = true);
            throw new Exception("Refreshed release duplicated active writer");
        }
        catch (IOException) { Check(rollback && admission.Owns(owner), "refreshed same release retains active owner"); }
        Check(OrbitReleaseSelection.Capture(entry, a)!.Size != owner.OrbitRelease!.Size, "immutable selection snapshot");
        Check(!entry.DownloadAllEnabled && new CatalogEntry().DownloadAllEnabled, "Orbit-only batch restriction");
        return Task.CompletedTask;
    }
}
