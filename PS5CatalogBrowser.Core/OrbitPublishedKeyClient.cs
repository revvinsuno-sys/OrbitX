using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

/// <summary>Retrieves only the official release's digest-verified source bundle, never provider content.</summary>
public sealed class OrbitPublishedKeyClient : IDisposable
{
    public const string ReleaseUrl = "https://api.github.com/repos/saawant12/orbit-store-ps5/releases/latest";
    public const long MaximumBundleBytes = 512L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    public OrbitPublishedKeyClient(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PS5CatalogBrowser/1.0");
    }

    public async Task<OrbitManagedKey> DownloadAsync(OrbitKeyStore store, IProgress<string>? progress = null, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5)); token = timeout.Token;
            progress?.Report("Checking the official Orbit release…");
            using var metadataResponse = await GetAsync(new Uri(ReleaseUrl), false, token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            await CopyBoundedAsync(metadataResponse, metadata, 1024 * 1024, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(metadata.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var release = root.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(release, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.NonBacktracking) ||
                root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("Unsupported official Orbit release metadata.");
            var version = release[1..]; var assetName = $"orbit-store-{version}-source-bundle.zip";
            var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("The official release does not publish one supported source bundle.");
            var asset = assets[0];
            var origin = $"https://github.com/saawant12/orbit-store-ps5/releases/download/{release}/{assetName}";
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (asset.GetProperty("browser_download_url").GetString() != origin || size is < 1 or > MaximumBundleBytes ||
                !Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.NonBacktracking))
                throw new InvalidDataException("The official source bundle URL, size or SHA-256 digest is unsupported.");
            var sha256 = digest[7..];
            var cached = store.Load().FirstOrDefault(k => k.Release == release && k.BundleSha256 == sha256 && k.Origin == origin);
            if (cached is not null) { progress?.Report("The verified published Orbit key is already available."); return cached; }

            progress?.Report($"Downloading {release} source bundle ({size / (1024 * 1024)} MiB) to retrieve its published key…");
            temporary = Path.Combine(Path.GetTempPath(), "PS5OrbitKey-" + Guid.NewGuid().ToString("N") + ".zip");
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var response = await GetAsync(new Uri(origin), true, token).ConfigureAwait(false);
                await CopyBoundedAsync(response, target, size, token).ConfigureAwait(false);
                if (target.Length != size) throw new InvalidDataException("Orbit source bundle length does not match its release metadata.");
                target.Position = 0;
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(target, token).ConfigureAwait(false)).ToLowerInvariant();
                if (actual != sha256) throw new InvalidDataException("Orbit source bundle SHA-256 verification failed. No key was imported.");
                target.Position = 0;
                var key = await Task.Run(() => ExtractKey(target, version, token), token).ConfigureAwait(false);
                try { return await store.SaveAsync(key, release, sha256, origin, token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Unsupported official Orbit release or key format. Import a key manually."); }
        catch (HttpRequestException) { throw new HttpRequestException("Official Orbit key download failed. Check connectivity and retry, or import a key manually."); }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, bool asset, CancellationToken token)
    {
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location; response.Dispose();
                if (!asset || location is null) throw new InvalidDataException("Unexpected Orbit release metadata redirect.");
                var next = new Uri(url, location);
                if (next.Scheme != "https" || next.Host != "release-assets.githubusercontent.com" || next.Port != 443 ||
                    next.UserInfo.Length > 0 || next.Fragment.Length > 0 || !next.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal))
                    throw new InvalidDataException("Orbit key download redirect is outside the verified GitHub release host.");
                url = next; continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) { response.Dispose(); throw new HttpRequestException("Official Orbit release is unavailable."); }
            return response;
        }
        throw new InvalidDataException("Too many Orbit key download redirects.");
    }

    private static async Task CopyBoundedAsync(HttpResponseMessage response, Stream target, long maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum || response.Content.Headers.ContentEncoding.Count > 0)
            throw new InvalidDataException("Orbit release response exceeds the supported size or encoding.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var block = new byte[81920]; long length = 0; int count;
        while ((count = await source.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            length += count; if (length > maximum) throw new InvalidDataException("Orbit release response exceeds the supported size.");
            await target.WriteAsync(block.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private static byte[] ExtractKey(Stream bundle, string version, CancellationToken token)
    {
        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read, true);
        var archives = zip.Entries.Where(e => e.FullName == $"orbit-store-{version}-source.tar.gz").ToArray();
        if (archives.Length != 1 || archives[0].Length is < 1 or > 64 * 1024 * 1024)
            throw new InvalidDataException("The Orbit bundle has no supported application source archive.");
        using var gzip = new GZipStream(archives[0].Open(), CompressionMode.Decompress);
        using var bounded = new BoundedSourceStream(gzip, token);
        using var tar = new TarReader(bounded);
        byte[]? key = null;
        try
        {
            while (tar.GetNextEntry() is { } entry)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Name != $"orbit-store-{version}/backend/catalog_key.h") continue;
                if (key is not null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length is < 1 or > 4096)
                    throw new InvalidDataException("The Orbit bundle contains an ambiguous or unsupported key header.");
                var material = new byte[(int)entry.Length];
                try { entry.DataStream!.ReadExactly(material); key = LocalOrbitCatalog.ParseKey(material); }
                finally { CryptographicOperations.ZeroMemory(material); }
            }
            return key ?? throw new InvalidDataException("The official Orbit source bundle contains no supported published key header.");
        }
        catch { if (key is not null) CryptographicOperations.ZeroMemory(key); throw; }
    }

    // TarReader's skip operations also pass through this bound; archive paths are never extracted to disk.
    private sealed class BoundedSourceStream(Stream inner, CancellationToken token) : Stream
    {
        private long consumed;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested(); var count = inner.Read(buffer); consumed += count;
            if (consumed > 256L * 1024 * 1024) throw new InvalidDataException("Orbit application source archive exceeds the supported expanded size.");
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    public void Dispose() => client.Dispose();
}
