using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS5CatalogBrowser.Core;

if (args.Contains("--sharpemu"))
{
    try { await SharpEmuTests.RunAsync(); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}
if (args.Contains("--sharpemu-managed-update"))
{
    try
    {
        var patch = Path.GetFullPath("integrations/sharpemu/f10-menu.patch");
        var updater = new SharpEmuUpdater(SharpEmuUpdater.DefaultRoot, patch, Console.WriteLine);
        var revision = await updater.CheckAsync();
        var installed = await updater.UpdateAsync(revision);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(installed)); return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}

if (args.Contains("--installed-inventory"))
{
    try { await InstalledInventoryTests.RunAsync(); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}
if (args.Contains("--installed-layout"))
{
    try { await InstalledFolderLayoutTests.RunAsync(); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}
if (args.Contains("--streaming-readers"))
{
    try { await StreamingPackageReaderTests.RunAsync(); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}

if (args.Length == 2 && args[0] == "--inspect-package")
{
    try
    {
        var info = LibProsperoPkg.PKG.ProsperoPackageExtractor.Inspect(args[1]);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(info));
        var key = info.OuterEncrypted ? LibProsperoPkg.PKG.ProsperoExtractionKey.FromPasscode(new string('0', 32)) : LibProsperoPkg.PKG.ProsperoExtractionKey.None;
        var files = LibProsperoPkg.PKG.ProsperoPackageExtractor.ListFiles(args[1], key);
        Console.WriteLine($"Files: {files.Count}; bytes: {files.Sum(file => file.Size)}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}

if (args.Length == 3 && args[0] == "--validate-package-extraction")
{
    try
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var mount = await new PackageImageMounter(args[2]).MountAsync(args[1], cancellation.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
        Console.WriteLine("PASS full package extraction and cleanup: " + Path.GetFileName(args[1]));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}

if (args.Contains("--launch-log-worker"))
{
    Console.Write("STDOUT_BEGIN\n" + new string('o', 512 * 1024));
    Console.Error.Write("STDERR_BEGIN\n" + new string('e', 512 * 1024));
    Console.Write("\nOUTPUT_READY\n"); Console.Out.Flush(); Console.Error.Flush();
    await Task.Delay(2000);
    Console.Error.Write("CRASH_TAIL_WITHOUT_NEWLINE"); Console.Error.Flush(); return 37;
}
if (args.Contains("--launch-log-quick")) { Console.Write("QUICK_EXIT"); return 7; }

if (args.Length == 3 && args[0] == "--http-resume-crash")
    return await HttpResumeTests.CrashWorkerAsync(args[1], args[2]);

if (args.Contains("--library-deletion"))
{
    try { await LibraryDeletionTests.RunAsync(); return 0; }
    catch (Exception ex) { Console.Error.WriteLine("FAIL library deletion: " + ex); return 1; }
}

if (args.Length == 1 && args[0] == "--orbit-decoder")
{
    try { await OrbitCatalogDecoderTests.RunAsync(); Console.WriteLine("PASS Orbit catalogue envelope decoder"); return 0; }
    catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit catalogue envelope decoder: " + ex); return 1; }
}

if (args.Length == 1 && args[0] == "--orbit-parser")
{
    try { await OrbitCatalogParserTests.RunAsync(); Console.WriteLine("PASS Orbit catalogue normalisation"); return 0; }
    catch (Exception ex) { Console.Error.WriteLine("FAIL Orbit catalogue normalisation: " + ex); return 1; }
}

var cases = new (string, Func<Task>)[] { ("atomic download admission", AdmissionTests.RunAsync), ("safe download cleanup", DownloadCleanupTests.RunAsync), ("catalogues", Catalogues), ("OptiStore catalogues", OptiStoreCatalogues), ("cached catalogue fallback", CachedCatalogues), ("source failure isolation", SourceFailures), ("persistence", Persistence), ("downloads", Downloads), ("browser file safety and mirrors", BrowserFiles), ("extraction", Extraction), ("launcher", Launcher), ("OSFMount dependency validation", OSFMountDependencyTests.RunAsync), ("image installation, verification and source preservation", InstallationTests.RunAsync) };
cases = [("catalogue presentation", CatalogPresentationTests.RunAsync), ("managed Orbit decoding keys", OrbitKeyTests.RunAsync), ("live Orbit catalogue retrieval", LiveOrbitCatalogTests.RunAsync), ("Orbit selected release policy", OrbitDownloadPolicyTests.RunAsync), ("local Orbit catalogue loading", LocalOrbitCatalogTests.RunAsync), .. cases];
cases = [("library deletion", LibraryDeletionTests.RunAsync), ("durable HTTP resume", HttpResumeTests.RunAsync), .. cases];
cases = [("dual emulator and SharpEmu updater", SharpEmuTests.RunAsync), .. cases];
var failed = 0;
cases = [("Archive passwords", ArchivePasswordTests.RunAsync), ("RAR and PKG installation", PackageArchiveTests.RunAsync), ("runnable content movement", RunnableContentMoveTests.RunAsync), ("installed archive cleanup", InstalledArchiveCleanupTests.RunAsync), .. cases];
cases = [("persistent Kyty arguments", KytyArgumentTests.RunAsync), .. cases];
if (args.Contains("--arguments")) cases = [("persistent Kyty arguments", KytyArgumentTests.RunAsync)];
else if (args.Contains("--launch-logs")) cases = [("launcher validation", Launcher), ("durable launch log capture", LaunchLogTests.RunAsync)];
else if (args.Contains("--orbit-frontend")) cases = [("Orbit frontend catalogue contract", OrbitFrontendCatalogTests.RunAsync)];
else if (args.Contains("--provider-policy")) cases = [("Orbit selected release policy", OrbitDownloadPolicyTests.RunAsync)];
else if (args.Contains("--archive-passwords")) cases = [("Archive passwords", ArchivePasswordTests.RunAsync)];
else if (args.Contains("--packages")) cases = [("RAR and PKG installation", PackageArchiveTests.RunAsync)];
else if (args.Contains("--presentation")) cases = [("catalogue presentation", CatalogPresentationTests.RunAsync)];
else if (args.Contains("--extraction")) cases = [("extraction", Extraction)];
else if (args.Contains("--runnable-content")) cases = [("runnable content movement", RunnableContentMoveTests.RunAsync)];
else if (args.Contains("--orbit-keys")) cases = [("managed Orbit decoding keys", OrbitKeyTests.RunAsync)];
else if (args.Contains("--orbit-live")) cases = [("live Orbit catalogue retrieval", LiveOrbitCatalogTests.RunAsync)];
else if (args.Contains("--resume")) cases = [("durable HTTP resume", HttpResumeTests.RunAsync)];
else if (args.Contains("--completion")) cases = [("crash-safe download completion", DownloadCompletionTests.RunAsync)];
else if (args.Contains("--cleanup")) cases = [("safe download cleanup", DownloadCleanupTests.RunAsync)];
else if (args.Contains("--archive-cleanup")) cases = [("installed archive cleanup", InstalledArchiveCleanupTests.RunAsync)];
else if (args.Contains("--installations")) cases = [("image installation, verification and source preservation", InstallationTests.RunAsync)];
else cases = [("Orbit frontend catalogue contract", OrbitFrontendCatalogTests.RunAsync), ("Orbit catalogue normalisation", OrbitCatalogParserTests.RunAsync), ("Orbit catalogue envelope decoder", OrbitCatalogDecoderTests.RunAsync), ("crash-safe download completion", DownloadCompletionTests.RunAsync), .. cases];
foreach (var (name, run) in cases)
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
if (args.Contains("--windows-exfat-integration"))
    try { await WindowsImageIntegration.RunAsync(); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL Windows exFAT integration: " + ex); }
try { await MountHostTests.RunAsync(); }
catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL mount helper IPC: " + ex); }
if (args.Length == 2 && args[0] == "--optistore-file")
    try
    {
        var bytes = File.ReadAllBytes(args[1]);
        using var raw = JsonDocument.Parse(bytes);
        var packages = raw.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        var expectedLinks = packages.Sum(p => p.GetProperty("downloadLinks").GetArrayLength());
        var live = CatalogParser.Parse(new CatalogSource { Id = "opti", Name = "OptiStore" }, bytes);
        Check(live.Format == "OptiStore" && live.Entries.Count == packages.Length && live.Entries.Sum(x => x.Artifacts.Count) == expectedLinks);
        Console.WriteLine($"PASS live OptiStore: {live.Entries.Count} entries, {live.Entries.Sum(x => x.Artifacts.Count)} links");
    }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL live OptiStore: " + ex); }
return failed == 0 ? 0 : 1;

static Task Catalogues()
{
    var home = new CatalogSource { Id = "home", Name = "Homebrew" };
    var a = CatalogParser.Parse(home, B("{\"apps\":[{\"titleid\":\"PPSA1\",\"name\":\"ccleste\",\"artifact_url\":\"https://example.test/game.zip\",\"sha256\":\"" + new string('a', 64) + "\"},{\"name\":\"Missing fields\"}]}"));
    Check(a.Format == "PS5 Homebrew" && a.Entries.Count == 2 && a.Entries[0].Artifacts[0].Sha256.Length == 64);
    var b = CatalogParser.Parse(new CatalogSource { Id = "evo", Name = "evoX" }, B("{\"payloads\":[{\"name\":\"Payload\",\"url\":\"https://example.test/a.elf\",\"checksum\":\"bad\"}]}"));
    Check(b.Format == "evoX Payloads" && b.Entries[0].Artifacts[0].Sha256 == "" && b.Entries[0].Classification == "Console payload");
    var c = CatalogParser.Parse(new CatalogSource { Id = "library" }, B("{\"entries\":[{\"id\":\"x\",\"title\":\"Game\",\"title_id\":\"PPSA1\",\"files\":[{\"url\":\"https://a/x.001\"},{\"url\":\"https://a/x.002\"}],\"source_sets\":[{\"host\":\"mirror\",\"files\":[{\"url\":\"https://b/x.001\"}]}]}]}"));
    Check(c.Format == "PS5 Library" && c.Entries[0].Artifacts.Count >= 2 && c.Entries[0].Id != a.Entries[0].Id);
    Throws<Exception>(() => CatalogParser.Parse(home, B("not json")));
    Throws<InvalidDataException>(() => CatalogParser.Parse(home, B("{\"unknown\":[]}")));
    return Task.CompletedTask;
}
static Task OptiStoreCatalogues()
{
    const string links = """
        {"name":"OptiStore","packages":[
          {"titleId":"PPSA1","title":"Example","version":"01.011","category":"game","posterUrl":"https://example.test/poster.jpg","description":"Region: EUR\nExample game","formatLabel":"FPKG · PKG","sizeBytes":10000,"downloadLinks":[
            {"name":"[01/02 GAME PKG]","url":"https://a.test/part1","group":"Standard","version":"01.011","mirror":"Host A","sizeBytes":5000,"fileName":"game.part01.rar","editionId":"PPSA1"},
            {"name":"[02/02 GAME PKG]","url":"https://a.test/part2","group":"Standard","version":"01.011","mirror":"Host A","sizeBytes":5000},
            {"name":"[GAME PKG]","url":"https://b.test/whole","group":"Standard","mirror":"Host B"},
            {"name":"[UPDATE PKG]","url":"https://a.test/update","group":"Update"},
            {"name":"[DLC PKG]","url":"https://a.test/dlc","group":"DLC"}]},
          null,{"downloadLinks":[1]},{"title":"No links","downloadLinks":[{"url":12}]}
        ]}
        """;
    var source = new CatalogSource { Id = "opti", Name = "OptiStore" };
    var parsed = CatalogParser.Parse(source, B(links));
    Check(parsed.Format == "OptiStore" && parsed.Entries.Count == 2);
    var entry = parsed.Entries[0];
    Check(entry.TitleId == "PPSA1" && entry.Region == "EUR" && entry.Size == 10000 && entry.CoverUrl.EndsWith("poster.jpg"));
    Check(entry.Artifacts.Count == 5 && entry.Artifacts[0].PartNumber == 1 && entry.Artifacts[0].PartCount == 2 && entry.Artifacts[1].PartNumber == 2);
    Check(entry.Artifacts[0].Host == "a.test" && entry.Artifacts[0].FileName == "game.part01.rar" && entry.Artifacts[0].EditionId == "PPSA1" && entry.Artifacts[0].Size == 5000 && entry.Artifacts[3].SourceSet.Contains("Update") && entry.Artifacts[4].SourceSet.Contains("DLC"));
    Check(entry.Artifacts.All(x => x.Mirrors.Count == 0) && entry.Id != CatalogParser.Parse(new CatalogSource { Id = "other" }, B(links)).Entries[0].Id);
    var map = CatalogParser.Parse(source, B("""{"packages":{"first":{"title":"Map","titleId":"PPSA2","files":[{"label":"File","link":"https://a.test/map.pkg"}]}}}"""));
    Check(map.Entries.Count == 1 && map.Entries[0].Artifacts.Single().Url.EndsWith("map.pkg"));
    var data = CatalogParser.Parse(source, B("""{"DATA":{"https://a.test/data.pkg":{"name":"Data","title_id":"PPSA3"}}}"""));
    Check(data.Entries.Count == 1 && data.Entries[0].Artifacts.Single().Url.EndsWith("data.pkg"));
    var bare = CatalogParser.Parse(source, B("""[{"title":"Bare","downloadLinks":["https://a.test/one","https://b.test/two"]}]"""));
    Check(bare.Entries.Count == 1 && bare.Entries[0].Artifacts.Count == 2);
    Throws<InvalidDataException>(() => CatalogParser.Parse(source, B("""{"packages":"new format"}""")));
    return Task.CompletedTask;
}
static async Task SourceFailures()
{
    using var server = new MockServer(B("{\"apps\":[{\"name\":\"Healthy\"}]}"));
    using var transfer = new HttpTransfer();
    var healthy = new CatalogSource { Id = "healthy", Name = "Healthy", Url = server.Url("/catalog") };
    var offline = new CatalogSource { Id = "offline", Name = "Offline", Url = server.Url("/offline") };
    var entries = (await transfer.LoadCatalogAsync(healthy, CancellationToken.None)).Entries;
    await ThrowsAsync<HttpRequestException>(async () => { await transfer.LoadCatalogAsync(offline, CancellationToken.None); });
    Check(entries.Count == 1 && entries[0].Title == "Healthy");
}
static async Task CachedCatalogues()
{
    var root = Temp();
    var data = B("""{"packages":[{"title":"Saved title","downloadLinks":["https://example.test/file.pkg"]}]}""");
    var source = new CatalogSource { Id = "saved", Name = "OptiStore" };
    try
    {
        using (var server = new MockServer(data))
        using (var transfer = new HttpTransfer())
        {
            source.Url = server.Url("/catalog");
            var loaded = await transfer.LoadCatalogAsync(source, CancellationToken.None);
            Check(loaded.Format == "OptiStore" && loaded.Entries.Count == 1);
            var cache = new CatalogCache(root);
            await cache.SaveAsync(source, loaded.Data, CancellationToken.None);
            Check(File.ReadAllBytes(cache.FilePath(source)).SequenceEqual(data));
        }
        using (var transfer = new HttpTransfer())
            await ThrowsAsync<HttpRequestException>(async () => { await transfer.LoadCatalogAsync(source, CancellationToken.None); });
        var saved = await new CatalogCache(root).LoadAsync(source, CancellationToken.None);
        Check(saved is not null && saved.Value.Format == "OptiStore" && saved.Value.Entries.Single().Title == "Saved title");
        var other = new CatalogSource { Id = "other", Name = source.Name, Url = source.Url };
        Check(await new CatalogCache(root).LoadAsync(other, CancellationToken.None) is null);
        source.Url += "?edited=1";
        Check(await new CatalogCache(root).LoadAsync(source, CancellationToken.None) is null);
        source.Url = source.Url[..^"?edited=1".Length];
        Throws<Exception>(() => CatalogParser.Parse(source, B("not json")));
        Check((await new CatalogCache(root).LoadAsync(source, CancellationToken.None))?.Entries.Single().Title == "Saved title");
        var updated = B("""{"packages":[{"title":"New saved title"}]}""");
        Check(CatalogParser.Parse(source, updated).Entries.Count == 1);
        await new CatalogCache(root).SaveAsync(source, updated, CancellationToken.None);
        Check((await new CatalogCache(root).LoadAsync(source, CancellationToken.None))?.Entries.Single().Title == "New saved title");
        Check(Directory.GetFiles(root, "*.tmp").Length == 0);
        File.WriteAllText(new CatalogCache(root).FilePath(source), "not json");
        await ThrowsAsync<JsonException>(async () => { await new CatalogCache(root).LoadAsync(source, CancellationToken.None); });
    }
    finally { Directory.Delete(root, true); }
}
static Task Persistence()
{
    var root = Temp();
    try
    {
        var store = new SettingsStore(root); var settings = AppSettings.CreateDefault();
        settings.Sources.Add(new CatalogSource { Name = "OptiStore", Url = "https://example.test/catalog.json", Format = "OptiStore", LastSuccessfulRefresh = DateTimeOffset.UtcNow });
        settings.Downloads.Add(new DownloadJob { State = "Downloading", Destination = Path.Combine(root, "incomplete.zip") });
        settings.Downloads.Add(new DownloadJob { State = "Downloaded", Destination = Path.Combine(root, "missing.zip") });
        var changedPath = Path.Combine(root, "changed.zip"); File.WriteAllText(changedPath, "new size");
        settings.Downloads.Add(new DownloadJob { State = "Downloaded", Destination = changedPath, DownloadedBytes = 2 });
        settings.Library.Add(new LocalLibraryEntry { ContentPath = Path.Combine(root, "missing") });
        store.Save(settings); var loaded = store.Load();
        Check(loaded.Sources.Count == 3 && loaded.Downloads[0].State == "Interrupted" && loaded.Downloads[1].State == "Missing" && loaded.Downloads[2].State == "Changed");
        Check(loaded.Sources[2].Name == "OptiStore" && loaded.Sources[2].Format == "OptiStore" && loaded.Sources[2].LastSuccessfulRefresh is not null);
        Check(loaded.Library[0].Classification == "Missing local content" && !File.Exists(store.FilePath + ".tmp"));
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}
static async Task Downloads()
{
    var root = Temp(); var bytes = B("test homebrew content");
    using var server = new MockServer(bytes); using var transfer = new HttpTransfer();
    try
    {
        var job = new DownloadJob { Url = server.Url("/file"), Destination = Path.Combine(root, "test.zip"), ExpectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        var progressCount = 0;
        await transfer.DownloadAsync(job, new SyncProgress<DownloadJob>(_ => progressCount++), CancellationToken.None);
        var store = new SettingsStore(Path.Combine(root, "settings"));
        var settings = new AppSettings { DownloadRoot = root, Downloads = [job] };
        await DownloadCompletion.CompleteAsync(job, settings, store.Save, CancellationToken.None);
        Check(job.State == "Downloaded" && File.ReadAllBytes(job.Destination).SequenceEqual(bytes));
        Check(progressCount > 0);
        foreach (var (name, path, expected, succeeds) in new (string, string, long?, bool)[]
        {
            ("matching", "/file", bytes.Length, true),
            ("conflicting-header", "/file", bytes.Length + 1, false),
            ("chunked-matching", "/chunked", bytes.Length, true),
            ("chunked-short", "/chunked", bytes.Length + 1, false),
            ("chunked-excess", "/chunked", bytes.Length - 1, false),
            ("unknown-size", "/chunked", null, true),
            ("zero-size", "/file", 0, true),
            ("negative-size", "/chunked", -1, true)
        })
        {
            var sized = new DownloadJob { Url = server.Url(path), Destination = Path.Combine(root, name + ".zip"), ExpectedSize = expected };
            var otherTemporary = sized.Destination + ".other-job.part";
            File.WriteAllText(otherTemporary, "preserve other job");
            if (succeeds)
            {
                await transfer.DownloadAsync(sized, null, CancellationToken.None);
                settings.Downloads.Add(sized);
                await DownloadCompletion.CompleteAsync(sized, settings, store.Save, CancellationToken.None);
                Check(sized.State == "Downloaded" && sized.DownloadedBytes == bytes.Length && File.ReadAllBytes(sized.Destination).SequenceEqual(bytes));
            }
            else
            {
                await ThrowsAsync<InvalidDataException>(() => transfer.DownloadAsync(sized, null, CancellationToken.None));
                Check(sized.Error.Contains($"{expected:N0} bytes") && sized.Error.Contains($"{bytes.Length:N0} bytes"));
                Check(sized.State == "Failed" && !File.Exists(sized.Destination));
                if (path == "/file") Check(sized.DownloadedBytes == 0);
                if (name == "chunked-excess") Check(sized.DownloadedBytes <= expected);
            }
            Check(!File.Exists(sized.Destination + "." + sized.Id + ".part") && File.ReadAllText(otherTemporary) == "preserve other job");
        }
        await ThrowsAsync<IOException>(() => transfer.DownloadAsync(new DownloadJob { Url = job.Url, Destination = job.Destination }, null, CancellationToken.None));
        var mismatch = new DownloadJob { Url = job.Url, Destination = Path.Combine(root, "bad.zip"), ExpectedSize = bytes.Length, ExpectedSha256 = new string('0', 64) };
        await ThrowsAsync<InvalidDataException>(() => transfer.DownloadAsync(mismatch, null, CancellationToken.None));
        Check(mismatch.State == "Integrity failed" && !File.Exists(mismatch.Destination));
        var html = new DownloadJob { Url = server.Url("/html"), Destination = Path.Combine(root, "html.zip") };
        await ThrowsAsync<InvalidDataException>(() => transfer.DownloadAsync(html, null, CancellationToken.None));
        var cancelled = new DownloadJob { Url = job.Url, Destination = Path.Combine(root, "cancel.zip") };
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => transfer.DownloadAsync(cancelled, null, cts.Token));
        Check(!File.Exists(html.Destination) && !File.Exists(cancelled.Destination));
        var midCancel = new DownloadJob { Url = server.Url("/slow"), Destination = Path.Combine(root, "midcancel.zip") };
        using var midCts = new CancellationTokenSource();
        await ThrowsAsync<OperationCanceledException>(() => transfer.DownloadAsync(midCancel, new SyncProgress<DownloadJob>(_ => midCts.Cancel()), midCts.Token));
        Check(midCancel.State == "Cancelled" && !File.Exists(midCancel.Destination) && !Directory.GetFiles(root, "*midcancel*.part").Any());
        var truncated = new DownloadJob { Url = server.Url("/truncated"), Destination = Path.Combine(root, "truncated.zip") };
        await ThrowsAsync<Exception>(() => transfer.DownloadAsync(truncated, null, CancellationToken.None));
        Check(!File.Exists(truncated.Destination) && truncated.State == "Failed");
        await ThrowsAsync<InvalidOperationException>(() => transfer.DownloadAsync(new DownloadJob { Url = server.Url("/redirect-http"), Destination = Path.Combine(root, "redirect.zip") }, null, CancellationToken.None));
    }
    finally { Directory.Delete(root, true); }
}
static async Task BrowserFiles()
{
    var root = Temp(); var data = B("harmless local browser fixture");
    try
    {
        var entry = new CatalogEntry { Id = "browser", Title = "Fixture", Artifacts =
        [
            new() { Id = "a", FileName = "fixture.pkg", Url = "https://a.test/file", SourceSet = "Base · Host A", Size = data.Length, Mirrors = ["https://mirror.test/file"] },
            new() { Id = "b", FileName = "fixture.pkg", Url = "https://b.test/file", SourceSet = "Base · Host B" },
            new() { Id = "c", FileName = "fixture.part2.pkg", Url = "https://a.test/part2", SourceSet = "Base · Host A" }
        ] };
        var mirrors = DownloadMirror.ForEntry(entry);
        Check(mirrors.Count == 4 && mirrors[0].GroupLabel == mirrors[2].GroupLabel && mirrors[0].GroupLabel != mirrors[3].GroupLabel);
        Check(mirrors[0].Provider == "Host A" && mirrors[1].Hostname == "mirror.test");
        var first = BrowserDownloadFiles.Prepare(entry, mirrors[0], root, "../fixture.pkg", []);
        var second = BrowserDownloadFiles.Prepare(entry, mirrors[2], root, "fixture.pkg", [first]);
        Check(first.Destination != second.Destination && Path.GetDirectoryName(first.Destination) == root);
        Check(BrowserDownloadFiles.SafeName("CON.pkg").StartsWith('_'));
        first.ExpectedSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        File.WriteAllBytes(first.StagingPath, data); first.DownloadedBytes = data.Length;
        var store = new SettingsStore(Path.Combine(root, "settings"));
        var settings = new AppSettings { DownloadRoot = root, Downloads = [first] };
        var library = await DownloadCompletion.CompleteAsync(first, settings, store.Save, CancellationToken.None);
        Check(first.State == "Downloaded" && library.InstalledGame is null && library.ContentMove is null && File.ReadAllBytes(first.Destination).SequenceEqual(data));
        BrowserDownloadFiles.DiscardStaging(first);
        var mismatch = BrowserDownloadFiles.Prepare(entry, mirrors[0], root, "bad.pkg", [first, second]);
        File.WriteAllBytes(mismatch.StagingPath, data); mismatch.DownloadedBytes = data.Length; mismatch.ExpectedSha256 = new string('0', 64);
        await ThrowsAsync<InvalidDataException>(() => BrowserDownloadFiles.VerifyAsync(mismatch, CancellationToken.None));
        Check(!File.Exists(mismatch.Destination));
        mismatch.ExpectedSha256 = ""; mismatch.ExpectedSize = data.Length + 1;
        await ThrowsAsync<InvalidDataException>(() => BrowserDownloadFiles.VerifyAsync(mismatch, CancellationToken.None));
        mismatch.ExpectedSize = data.Length; File.WriteAllText(mismatch.Destination, "existing");
        settings.Downloads.Add(mismatch);
        await ThrowsAsync<IOException>(() => DownloadCompletion.CompleteAsync(mismatch, settings, store.Save, CancellationToken.None));
        Check(File.ReadAllText(mismatch.Destination) == "existing");
        var unsafeJob = new DownloadJob { Id = mismatch.Id, Destination = mismatch.Destination, StagingPath = Path.Combine(root, "unowned") };
        await ThrowsAsync<IOException>(() => BrowserDownloadFiles.VerifyAsync(unsafeJob, CancellationToken.None));
        var html = BrowserDownloadFiles.Prepare(entry, mirrors[2], root, "html.pkg", []);
        File.WriteAllText(html.StagingPath, "<html>not content</html>"); html.DownloadedBytes = new FileInfo(html.StagingPath).Length;
        await ThrowsAsync<InvalidDataException>(() => BrowserDownloadFiles.VerifyAsync(html, CancellationToken.None));
    }
    finally { Directory.Delete(root, true); }
}
static async Task Extraction()
{
    var root = Temp();
    try
    {
        var valid = Path.Combine(root, "valid.zip");
        using (var archive = ZipFile.Open(valid, ZipArchiveMode.Create)) { var e = archive.CreateEntry("nested/game/eboot.bin"); await using var s = e.Open(); await s.WriteAsync(B("elf")); }
        var started = new TaskCompletionSource(); var finished = new TaskCompletionSource();
        var progress = new Progress<double>(value => { if (value == 0) started.TrySetResult(); if (value == 100) finished.TrySetResult(); });
        var dest = await SafeZipExtractor.ExtractAsync(valid, Path.Combine(root, "output"), CancellationToken.None, progress);
        await Task.WhenAll(started.Task, finished.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Check(FileInspection.FindGameRoot(dest) == Path.Combine(dest, "nested", "game"));
        await ThrowsAsync<OperationCanceledException>(() => SafeZipExtractor.ExtractAsync(valid, Path.Combine(root, "cancelled"), new CancellationToken(true), progress));
        Check(!Directory.Exists(Path.Combine(root, "cancelled")) && !Directory.EnumerateDirectories(root, ".ps5catalog-extract-*").Any());
        var bad = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(bad, ZipArchiveMode.Create)) { var e = archive.CreateEntry("../escape.txt"); await using var s = e.Open(); await s.WriteAsync(B("bad")); }
        await ThrowsAsync<InvalidDataException>(() => SafeZipExtractor.ExtractAsync(bad, Path.Combine(root, "bad-output"), CancellationToken.None));
        Check(!Directory.Exists(Path.Combine(root, "bad-output")) && !File.Exists(Path.Combine(root, "escape.txt")));
        foreach (var (name, unsafePath) in new[] { ("absolute", "C:/outside.txt"), ("device", "CON.txt"), ("backslash", "a\\..\\outside.txt") })
        {
            var archivePath = Path.Combine(root, name + ".zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)) archive.CreateEntry(unsafePath);
            await ThrowsAsync<InvalidDataException>(() => SafeZipExtractor.ExtractAsync(archivePath, Path.Combine(root, name + "-out"), CancellationToken.None));
            Check(!Directory.Exists(Path.Combine(root, name + "-out")));
        }
        var duplicate = Path.Combine(root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { archive.CreateEntry("A.txt"); archive.CreateEntry("a.txt"); }
        await ThrowsAsync<InvalidDataException>(() => SafeZipExtractor.ExtractAsync(duplicate, Path.Combine(root, "duplicate-out"), CancellationToken.None));
        var symlink = Path.Combine(root, "symlink.zip");
        using (var archive = ZipFile.Open(symlink, ZipArchiveMode.Create)) { var e = archive.CreateEntry("link"); e.ExternalAttributes = 0xA000 << 16; }
        await ThrowsAsync<InvalidDataException>(() => SafeZipExtractor.ExtractAsync(symlink, Path.Combine(root, "symlink-out"), CancellationToken.None));
        File.WriteAllText(bad, "corrupt");
        await ThrowsAsync<InvalidDataException>(() => SafeZipExtractor.ExtractAsync(bad, Path.Combine(root, "corrupt-output"), CancellationToken.None));
        Check(!Directory.Exists(Path.Combine(root, "corrupt-output")));
    }
    finally { Directory.Delete(root, true); }
}
static Task Launcher()
{
    var root = Temp();
    try
    {
        var exe = Path.Combine(root, "kyty_emulator.exe"); File.WriteAllText(exe, "fixture");
        var game = Path.Combine(root, "game with spaces"); Directory.CreateDirectory(game); File.WriteAllText(Path.Combine(game, "eboot.bin"), "fixture");
        var info = KytyLauncher.BuildStartInfo(exe, game);
        Check(info.ArgumentList.SequenceEqual(["--game", game]) && info.WorkingDirectory == root && !info.UseShellExecute);
        Throws<FileNotFoundException>(() => KytyLauncher.BuildStartInfo(Path.Combine(root, "missing.exe"), game));
        Throws<InvalidOperationException>(() => KytyLauncher.BuildStartInfo(exe, Path.Combine(root, "missing")));
        var pkg = Path.Combine(root, "game.pkg"); File.WriteAllText(pkg, "fixture"); Throws<InvalidOperationException>(() => KytyLauncher.BuildStartInfo(exe, pkg));
        var zar = Path.Combine(root, "game.zar"); File.WriteAllText(zar, "fixture"); Throws<InvalidOperationException>(() => KytyLauncher.BuildStartInfo(exe, zar));
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}
static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
static string Temp() { var path = Path.Combine(Path.GetTempPath(), "PS5CatalogTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
static void Check(bool ok) { if (!ok) throw new Exception("Assertion failed."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

sealed class MockServer : IDisposable
{
    private readonly HttpListener listener = new(); private readonly CancellationTokenSource stop = new(); private readonly byte[] bytes; private readonly Task loop; private readonly int port;
    public MockServer(byte[] data)
    {
        bytes = data; var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start(); loop = Task.Run(Serve);
    }
    public string Url(string path) => $"http://127.0.0.1:{port}{path}";
    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context; try { context = await listener.GetContextAsync(); } catch { break; }
            try
            {
                if (context.Request.Url?.AbsolutePath == "/redirect-http") { context.Response.StatusCode = 302; context.Response.RedirectLocation = "http://example.com/file"; }
                else if (context.Request.Url?.AbsolutePath == "/offline") context.Response.StatusCode = 503;
                else
                {
                    var path = context.Request.Url?.AbsolutePath;
                    var html = path == "/html";
                    var data = html ? Encoding.UTF8.GetBytes("<html>login</html>") : path == "/slow" ? new byte[2 * 1024 * 1024] : bytes;
                    context.Response.ContentType = html ? "text/html" : "application/octet-stream";
                    if (path == "/chunked") context.Response.SendChunked = true;
                    else context.Response.ContentLength64 = path == "/truncated" ? data.Length * 2 : data.Length;
                    if (path == "/slow")
                        for (var offset = 0; offset < data.Length; offset += 8192) { await context.Response.OutputStream.WriteAsync(data.AsMemory(offset, 8192)); await Task.Delay(5); }
                    else await context.Response.OutputStream.WriteAsync(data);
                }
                context.Response.Close();
            }
            catch { context.Response.Abort(); }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); listener.Close(); try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { } stop.Dispose(); }
}

sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
