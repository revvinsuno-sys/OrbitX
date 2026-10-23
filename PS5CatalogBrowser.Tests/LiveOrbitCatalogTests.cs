using System.IO;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PS5CatalogBrowser.Core;
using static LocalOrbitCatalogTests;

internal static class LiveOrbitCatalogTests
{
    internal sealed class Fixture : HttpMessageHandler
    {
        public int Calls;
        public Func<CancellationToken, Task<HttpResponseMessage>> Response = _ => Task.FromResult(Ok(Seal(Feed(generation: 7))));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            LocalOrbitCatalogTests.Check(request.RequestUri!.AbsoluteUri is LiveOrbitCatalog.OfficialUrl || request.RequestUri.AbsoluteUri.EndsWith("catalogue-v6.enc"), "only catalogue requests");
            LocalOrbitCatalogTests.Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "no credentials");
            return Response(token);
        }
    }
    internal static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "OrbitLiveCore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = Path.Combine(root, "key.bin"); File.WriteAllBytes(key, Key);
            var source = new CatalogSource { Id = "live", Name = "Orbit Store", SourceType = CatalogSourceType.OrbitLive,
                Url = LiveOrbitCatalog.OfficialUrl, KeyFilePath = key };
            var fixture = new Fixture(); using var http = new HttpTransfer(fixture);
            source.Enabled = false;
            try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("disabled source fetched"); } catch (InvalidOperationException) { }
            LocalOrbitCatalogTests.Check(fixture.Calls == 0, "disabled loader never fetches"); source.Enabled = true;
            var first = await LiveOrbitCatalog.LoadAsync(source, http);
            LocalOrbitCatalogTests.Check(first.FeedVersion == 7 && first.Entries.Count == 2 && first.Entries.Sum(e => e.Artifacts.Count) == 3, "supported live feed");
            var newer = OrbitCatalogParser.Parse(source, Feed(generation: 7, revision: 28));
            LiveOrbitCatalog.ValidateUpdate(first, newer);
            LiveOrbitCatalog.ValidateUpdate(first, OrbitCatalogParser.Parse(source, Feed(generation: 7, reverse: true)));
            foreach (var invalid in new[] { first, OrbitCatalogParser.Parse(source, Feed(generation: 6, revision: 29)) })
                try { LiveOrbitCatalog.ValidateUpdate(newer, invalid); throw new Exception("accepted rollback"); } catch (InvalidDataException) { }
            try { LiveOrbitCatalog.ValidateUpdate(first, OrbitCatalogParser.Parse(source, Feed(generation: 7, remove: true))); throw new Exception("accepted changed same revision"); } catch (InvalidDataException) { }

            async Task Fails(byte[] envelope, string expected)
            {
                fixture.Response = _ => Task.FromResult(Ok(envelope));
                try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("accepted invalid feed"); }
                catch (InvalidDataException ex) { LocalOrbitCatalogTests.Check(ex.Message.Contains(expected), expected); }
            }
            await Fails(Seal(Feed(generation: 8)), "unsupported feedVersion");
            await Fails(Seal(Feed(generation: 6)), "does not match");
            await Fails(Seal(Feed(schema: 2, generation: 7)), "unsupported schemaVersion");
            File.WriteAllBytes(key, new byte[32]); await Fails(Seal(Feed(generation: 7)), "authentication failed");
            File.WriteAllBytes(key, Key);
            var corrupt = Seal(Feed(generation: 7)); corrupt[^1] ^= 1; await Fails(corrupt, "authentication failed");
            await Fails([1, 2], "Orbit envelope");
            await Fails(Seal(Feed(generation: 7))[..^1], "authentication failed");
            await Fails(new byte[OrbitCatalogDecoder.MaximumPlaintextLength + 41], "exceeds");
            // Unknown content length must obey the same streaming bound.
            fixture.Response = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new NonSeekable(new byte[OrbitCatalogDecoder.MaximumPlaintextLength + 41])) });
            try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("unbounded stream"); } catch (InvalidDataException) { }
            fixture.Response = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("accepted HTTP error"); } catch (HttpRequestException) { }
            foreach (var destination in new[] { "http://localhost/catalog", "https://untrusted.invalid/feed", LiveOrbitCatalog.OfficialUrl, "/saawant12/orbit-store-ps5/main/catalogue-v6.enc" })
            {
                fixture.Response = _ => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(destination, UriKind.RelativeOrAbsolute); return Task.FromResult(r); };
                var before = fixture.Calls;
                try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("followed redirect"); } catch (HttpRequestException) { }
                LocalOrbitCatalogTests.Check(fixture.Calls == before + 1, "redirect never requested");
            }
            fixture.Response = async token => { await Task.Delay(Timeout.Infinite, token); return Ok([]); };
            using (var cancellation = new CancellationTokenSource(40))
                try { await LiveOrbitCatalog.LoadAsync(source, http, cancellation.Token); throw new Exception("ignored cancellation"); } catch (OperationCanceledException) { }
            fixture.Response = _ => throw new TaskCanceledException("Synthetic network timeout");
            try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("ignored timeout"); } catch (OperationCanceledException) { }
            foreach (var url in new[] { "http://raw.githubusercontent.com/saawant12/orbit-store-ps5/main/catalogue-v7.enc", LiveOrbitCatalog.OfficialUrl + "?token=secret", "https://evil.invalid/feed", "https://user:pass@raw.githubusercontent.com/feed" })
            {
                source.Url = url; var before = fixture.Calls;
                try { await LiveOrbitCatalog.LoadAsync(source, http); throw new Exception("accepted endpoint"); } catch (ArgumentException) { }
                LocalOrbitCatalogTests.Check(before == fixture.Calls, "invalid URL rejected before request");
            }
            source.Url = LiveOrbitCatalog.OfficialUrl;
            var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(new() { Sources = [source] });
            var restored = store.Load().Sources.Single();
            LocalOrbitCatalogTests.Check(restored.SourceType == CatalogSourceType.OrbitLive && restored.AutoRefresh && restored.KeyFilePath == key, "configuration persistence");
            LocalOrbitCatalogTests.Check(!File.ReadAllText(store.FilePath).Contains("synthetic-password") && !File.ReadAllText(store.FilePath).Contains(Convert.ToHexString(Key)), "no key/content persistence");
            LocalOrbitCatalogTests.Check(!File.Exists(new CatalogCache().FilePath(source)) && !Directory.Exists(Path.Combine(root, "catalogues")), "no live cache");
            var cache = new CatalogCache(Path.Combine(root, "catalogues"));
            try { await cache.SaveAsync(source, Feed(generation: 7), default); throw new Exception("cached Orbit plaintext"); } catch (InvalidOperationException) { }
            try { await cache.LoadAsync(source, default); throw new Exception("loaded Orbit plaintext cache"); } catch (InvalidOperationException) { }
            LocalOrbitCatalogTests.Check(!Directory.Exists(cache.DirectoryPath), "cache guard precedes disk writes");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
