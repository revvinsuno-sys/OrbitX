using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PS5CatalogBrowser.Core;

internal static class OrbitKeyTests
{
    internal static readonly byte[] Key = Enumerable.Range(1, 32).Select(n => (byte)n).ToArray();
    internal const string Origin = "https://github.com/saawant12/orbit-store-ps5/releases/download/v1.2.3/orbit-store-1.2.3-source-bundle.zip";
    internal const string Cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/123/synthetic?signature=synthetic";

    internal sealed class Fixture : HttpMessageHandler
    {
        public int Calls, AssetCalls;
        public byte[] Bundle = BuildBundle();
        public string? Redirect = Cdn;
        public Action<JsonObject>? ChangeMetadata;
        public Func<CancellationToken, Task<HttpResponseMessage>>? Download;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "credential-free key transport");
            if (request.RequestUri!.AbsoluteUri == OrbitPublishedKeyClient.ReleaseUrl)
            {
                var root = JsonSerializer.SerializeToNode(new { tag_name = "v1.2.3", draft = false, prerelease = false,
                    assets = new[] { new { name = "orbit-store-1.2.3-source-bundle.zip", size = Bundle.Length,
                        digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Bundle)).ToLowerInvariant(), browser_download_url = Origin } } })!.AsObject();
                ChangeMetadata?.Invoke(root);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString()) });
            }
            AssetCalls++;
            if (request.RequestUri.AbsoluteUri == Origin)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                if (Redirect is not null) response.Headers.Location = new Uri(Redirect);
                return Task.FromResult(response);
            }
            Check(request.RequestUri.AbsoluteUri == Cdn, "only verified CDN requested");
            return Download is not null ? Download(token) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle) });
        }
    }

    internal static byte[] BuildBundle(string keyPath = "orbit-store-1.2.3/backend/catalog_key.h", bool duplicate = false, string? text = null, bool duplicateArchive = false)
    {
        text ??= "/* synthetic */\n#ifndef ORBIT_CATALOG_KEY_H\n#define ORBIT_CATALOG_KEY_H\nstatic const unsigned char catalog_key[32] = {" + string.Join(",", Key.Select(b => "0x" + b.ToString("x2"))) + "};\n#endif";
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            for (var i = 0; i < (duplicate ? 2 : 1); i++)
            {
                using var content = new MemoryStream(Encoding.ASCII.GetBytes(text));
                var entry = new PaxTarEntry(TarEntryType.RegularFile, keyPath) { DataStream = content };
                tar.WriteEntry(entry);
            }
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            for (var i = 0; i < (duplicateArchive ? 2 : 1); i++)
            {
                using var stream = zip.CreateEntry("orbit-store-1.2.3-source.tar.gz").Open(); stream.Write(compressed.ToArray());
            }
        return output.ToArray();
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5KeyTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var temporaryBefore = Directory.GetFiles(Path.GetTempPath(), "PS5OrbitKey-*.zip");
        try
        {
            var store = new OrbitKeyStore(Path.Combine(root, "keys")); var fixture = new Fixture();
            using var client = new OrbitPublishedKeyClient(fixture);
            var key = await client.DownloadAsync(store);
            Check(File.ReadAllBytes(key.FilePath).SequenceEqual(Key) && key.Release == "v1.2.3" && key.Origin == Origin && key.Description.Contains(key.Fingerprint[..12]), "published key and descriptive provenance");
            Check(store.Load().Single().FilePath == key.FilePath && Directory.GetFiles(store.DirectoryPath).Length == 2, "only key and provenance retained");
            await client.DownloadAsync(store); Check(fixture.AssetCalls == 2, "verified existing release key reused after metadata check");
            Check(new OrbitKeyStore(store.DirectoryPath).Load().Single().Fingerprint == key.Fingerprint, "restart inventory");
            var manual = Path.Combine(root, "manual.hex"); await File.WriteAllTextAsync(manual, new string('a', 64));
            var imported = await store.ImportAsync(manual);
            Check(imported.FilePath != key.FilePath && imported.Description.Contains("Manual import") && store.Load().Count == 2 && File.ReadAllBytes(key.FilePath).SequenceEqual(Key), "manual import preserves existing key");
            await store.ImportAsync(manual); Check(store.Load().Count == 2, "manual key deduplicated");
            await File.WriteAllBytesAsync(key.FilePath, new byte[32]);
            Check(store.Load().All(k => k.Fingerprint != key.Fingerprint), "damaged keys excluded from dropdown");
            await Fails(() => client.DownloadAsync(store), "corrupted managed key never overwritten");
            Check(File.ReadAllBytes(key.FilePath).All(b => b == 0), "damaged file preserved for attention");
            await File.WriteAllTextAsync(Path.Combine(store.DirectoryPath, "invalid.json"), "{}");
            Check(store.Load().Count == 1, "malformed inventory record isolated");

            async Task Reject(Action<Fixture> configure)
            {
                var bad = new Fixture(); configure(bad);
                using var badClient = new OrbitPublishedKeyClient(bad);
                var empty = new OrbitKeyStore(Path.Combine(root, Guid.NewGuid().ToString("N")));
                await Fails(() => badClient.DownloadAsync(empty), "invalid published input rejected");
                Check(empty.Load().Count == 0 && !Directory.Exists(empty.DirectoryPath), "failed retrieval publishes no key");
            }
            await Reject(f => f.ChangeMetadata = o => o["assets"]![0]!["digest"] = "sha256:" + new string('0', 64));
            await Reject(f => f.ChangeMetadata = o => o["assets"]![0]!.AsObject().Remove("digest"));
            await Reject(f => f.ChangeMetadata = o => o["assets"]![0]!["size"] = OrbitPublishedKeyClient.MaximumBundleBytes + 1);
            await Reject(f => f.ChangeMetadata = o => o["assets"]![0]!["size"] = f.Bundle.Length + 1);
            await Reject(f => f.ChangeMetadata = o => o["assets"]![0]!["browser_download_url"] = "https://evil.invalid/bundle");
            await Reject(f => f.ChangeMetadata = o => o["tag_name"] = "v1.2.3/../../escape");
            await Reject(f => f.ChangeMetadata = o => o["draft"] = true);
            await Reject(f => f.Bundle = BuildBundle(text: "unsupported material"));
            await Reject(f => f.Bundle = BuildBundle(text: new string('x', 4097)));
            await Reject(f => f.Bundle = BuildBundle(keyPath: "../backend/catalog_key.h"));
            await Reject(f => f.Bundle = BuildBundle(duplicate: true));
            await Reject(f => f.Bundle = BuildBundle(duplicateArchive: true));
            await Reject(f => f.Bundle = [1, 2, 3]);
            foreach (var destination in new[] { "http://release-assets.githubusercontent.com/github-production-release-asset/test", "https://evil.invalid/bundle", "https://user:pass@release-assets.githubusercontent.com/github-production-release-asset/test", "https://release-assets.githubusercontent.com/unexpected", "https://release-assets.githubusercontent.com:8443/github-production-release-asset/test" })
                await Reject(f => f.Redirect = destination);
            await Reject(f => f.Redirect = null);
            await Reject(f => f.Download = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            await Reject(f => f.Download = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Bundle.Concat(new byte[] { 0 }).ToArray()) }));
            var cancelling = new Fixture { Download = async token => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); } };
            using var cancelClient = new OrbitPublishedKeyClient(cancelling); using var cancellation = new CancellationTokenSource(100);
            await Fails(() => cancelClient.DownloadAsync(new OrbitKeyStore(Path.Combine(root, "cancel")), token: cancellation.Token), "cancelled transfer");
            Check(!Directory.GetFiles(Path.GetTempPath(), "PS5OrbitKey-*.zip").Except(temporaryBefore).Any(), "temporary archives removed after success/failure/cancellation");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Fails(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or OperationCanceledException or IOException) { return; }
        throw new Exception(label);
    }
    internal static void Check(bool value, string label) { if (!value) throw new Exception(label); }
}
