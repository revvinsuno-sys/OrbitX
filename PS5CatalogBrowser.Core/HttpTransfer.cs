using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PS5CatalogBrowser.Core;

public sealed class HttpTransfer : IDisposable
{
    public DownloadBandwidthLimiter? DownloadLimiter { get; set; }
    private readonly HttpClient client;
    private static readonly ConcurrentDictionary<string, byte> Writers = new(StringComparer.OrdinalIgnoreCase);
    public HttpTransfer(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PS5CatalogBrowser/1.0");
    }

    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token)
        => await GetAsync(url, token, null, null);

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token, long? offset, string? etag, bool binary = false, bool allowRedirects = true)
    {
        var current = NetworkPolicy.Validate(url);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (binary) request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset is long start)
            {
                request.Headers.Range = new RangeHeaderValue(start, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag!));
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (!allowRedirects) throw new HttpRequestException("Orbit catalogue redirects are not allowed. Use the verified official HTTPS endpoint.");
                if (location is null) throw new HttpRequestException("Redirect has no destination.");
                current = NetworkPolicy.Validate(new Uri(current, location).AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException($"Host returned HTTP {(int)code} ({code})."); }
            return response;
        }
        throw new HttpRequestException("Too many redirects.");
    }

    public async Task<(string Format, List<CatalogEntry> Entries, byte[] Data)> LoadCatalogAsync(CatalogSource source, CancellationToken token)
    {
        using var response = await GetAsync(source.Url, token);
        if (response.Content.Headers.ContentLength > CatalogParser.MaximumBytes) throw new InvalidDataException("Catalogue exceeds 8 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + count > CatalogParser.MaximumBytes) throw new InvalidDataException("Catalogue exceeds 8 MiB.");
            buffer.Write(block, 0, count);
        }
        var data = buffer.ToArray();
        var (format, entries) = await Task.Run(() => CatalogParser.Parse(source, data), token);
        return (format, entries, data);
    }

    public async Task<byte[]> LoadOrbitEnvelopeAsync(string url, CancellationToken token)
    {
        LiveOrbitCatalog.ValidateUrl(url);
        using var response = await GetAsync(url, token, null, null, true, false);
        const int maximum = OrbitCatalogDecoder.MaximumPlaintextLength + 40;
        if (response.Content.Headers.ContentLength > maximum || response.Content.Headers.ContentEncoding.Count > 0)
            throw new InvalidDataException("Orbit catalogue exceeds 8 MiB or uses unsupported transport encoding.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("Orbit catalogue exceeds 8 MiB.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    public Task DownloadAsync(DownloadJob job, IProgress<DownloadJob>? progress, CancellationToken token,
        Func<HttpResumeCheckpoint, Task>? persist = null, bool resume = false, Action<string, DownloadJob>? checkpoint = null)
        => Task.Run(() => DownloadOwnedAsync(job, progress, token, persist, resume, checkpoint));

    private async Task DownloadOwnedAsync(DownloadJob job, IProgress<DownloadJob>? progress, CancellationToken token,
        Func<HttpResumeCheckpoint, Task>? persist, bool resume, Action<string, DownloadJob>? checkpoint)
    {
        if (job.Method != "Direct" || job.Completion is not null || job.CleanupPending || job.IsCleared)
            throw new IOException("This job is not available for direct HTTP transfer.");
        if (!resume && job.HttpCheckpoint is not null) throw new IOException("Retained HTTP data requires Resume or explicit Restart.");
        var writerKey = Path.GetFullPath(job.Destination);
        if (!Writers.TryAdd(writerKey, 0)) throw new IOException("Another HTTP writer owns this destination.");
        var verified = false;
        FileStream? target = null;
        FileStream? marker = null;
        var localValidated = !resume;
        try
        {
            if (File.Exists(job.Destination) || Directory.Exists(job.Destination)) throw new IOException("Destination already exists. Choose a different filename.");
            var directory = Path.GetDirectoryName(job.Destination) ?? throw new IOException("Invalid destination.");
            ImageFileSystem.EnsureNoLinks(directory); Directory.CreateDirectory(directory);
            if (job.FileName.Length == 0) job.FileName = Path.GetFileName(job.Destination);
            if (job.StagingPath.Length == 0) BrowserDownloadFiles.PrepareStaging(job);
            BrowserDownloadFiles.ValidateCompletionOwnership(job);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            if (resume)
            {
                HttpResume.ValidateState(job);
                if (persist is null || !HttpResume.Eligible(job)) throw new IOException("This partial is not eligible for safe resume. Explicit Restart is required.");
                marker = HttpResume.OpenMarker(job); target = HttpResume.OpenPartial(job);
                var saved = job.HttpCheckpoint!;
                await HttpResume.HashPrefixAsync(target, saved, hash, token);
                localValidated = true;
                offset = saved.Cursor;
                // Only an exact, exclusively held owned file with a verified prefix may lose trailing bytes.
                if (target.Length > offset) { target.SetLength(offset); target.Flush(true); }
                target.Position = offset; job.DownloadedBytes = offset; job.TotalBytes = saved.TotalBytes;
            }
            using var response = await GetAsync(job.Url, token, resume ? offset : null, resume ? job.HttpCheckpoint!.StrongETag : null, true);
            if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml" || response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("Host returned HTML or encoded content instead of an unencoded downloadable file.");
            long? total;
            if (resume)
            {
                var saved = job.HttpCheckpoint!;
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes" ||
                    range.From != offset || range.Length != saved.TotalBytes || range.To != saved.TotalBytes - 1 ||
                    response.Content.Headers.ContentLength is long length && length != saved.TotalBytes - offset ||
                    response.Headers.ETag?.ToString() != saved.StrongETag || !HttpResume.Strong(response.Headers.ETag?.ToString() ?? ""))
                    throw new InvalidDataException("Server did not prove the saved range and representation. Partial retained; explicit Restart is required.");
                total = saved.TotalBytes;
            }
            else
            {
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
                    throw new InvalidDataException("Initial download did not return a complete HTTP 200 representation.");
                total = response.Content.Headers.ContentLength;
                job.DownloadedBytes = 0;
            }
            job.TotalBytes = total;
            if (job.ExpectedSize is > 0 && total is long advertisedSize && advertisedSize != job.ExpectedSize.Value)
                throw new InvalidDataException($"Download size mismatch: catalogue expected {job.ExpectedSize.Value:N0} bytes; HTTP Content-Length advertised {advertisedSize:N0} bytes.");
            if (total is long size && DriveFree(directory) < size - offset) throw new IOException("Insufficient disk space for download.");
            if (!resume)
            {
                marker = HttpResume.OpenMarker(job);
                target = persist is null ? new FileStream(job.StagingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true) : HttpResume.OpenPartial(job, true);
                if (persist is not null)
                {
                    job.HttpCheckpoint = new() { FileIdentity = HttpResume.FileIdentity(target),
                        StrongETag = HttpResume.Strong(response.Headers.ETag?.ToString() ?? "") ? response.Headers.ETag!.ToString() : "", TotalBytes = total,
                        PrefixSha256 = Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant() };
                    job.HttpCheckpoint.Identity = HttpResume.Identity(job, job.HttpCheckpoint);
                    await SaveCheckpointAsync(); // Identity/cursor zero is durable before the first network byte is written.
                }
            }
            job.CanPause = job.HttpCheckpoint is { TotalBytes: > 0 } state && HttpResume.Strong(state.StrongETag) && job.SelectionIdentity.Length > 0;
            job.CanResume = false; progress?.Report(job);
            await using var source = await response.Content.ReadAsStreamAsync(token);
            using var sample = new MemoryStream(256);
            // Inspect original prefix on resumes, never mistake an interior range for a file header.
            if (resume)
            {
                var header = new byte[(int)Math.Min(offset, 256)]; target!.Position = 0;
                await target.ReadExactlyAsync(header, token); target.Position = offset;
                if (LooksLikeHtml(header)) throw new InvalidDataException("Retained file is HTML instead of content.");
            }
            var block = new byte[81920]; var clock = System.Diagnostics.Stopwatch.StartNew();
            var lastSave = clock.Elapsed; long lastCursor = offset;
            try
            {
                int count;
                while ((count = await source.ReadAsync(block, token)) > 0)
                {
                    if (job.ExpectedSize is > 0 && count > job.ExpectedSize.Value - job.DownloadedBytes)
                        throw new InvalidDataException($"Download size mismatch: expected {job.ExpectedSize.Value:N0} bytes; received at least {job.DownloadedBytes + count:N0} bytes.");
                    if (total is long maximum && count > maximum - job.DownloadedBytes)
                        throw new InvalidDataException("Server returned bytes beyond the representation length.");
                    if (!resume && sample.Length < 256)
                    {
                        sample.Write(block, 0, Math.Min(count, 256 - (int)sample.Length));
                        if (LooksLikeHtml(sample.ToArray())) throw new InvalidDataException("Host returned an HTML page instead of a downloadable file.");
                    }
                    if (total is null && DriveFree(directory) < count + 64L * 1024 * 1024) throw new IOException("Insufficient disk space for download.");
                    if (DownloadLimiter is { } limiter) await limiter.WaitAsync(count, token);
                    await target!.WriteAsync(block.AsMemory(0, count), token);
                    hash.AppendData(block, 0, count); job.DownloadedBytes += count;
                    job.BytesPerSecond = (job.DownloadedBytes - offset) / Math.Max(.001, clock.Elapsed.TotalSeconds);
                    progress?.Report(job);
                    if (persist is not null && (job.DownloadedBytes - lastCursor >= 8L * 1024 * 1024 || clock.Elapsed - lastSave >= TimeSpan.FromSeconds(2)))
                    { await SaveCheckpointAsync(); lastCursor = job.DownloadedBytes; lastSave = clock.Elapsed; }
                }
                if (job.ExpectedSize is > 0 && job.DownloadedBytes != job.ExpectedSize.Value)
                    throw new InvalidDataException($"Download size mismatch: expected {job.ExpectedSize.Value:N0} bytes; received {job.DownloadedBytes:N0} bytes.");
                if (total is long expected && job.DownloadedBytes != expected) throw new InvalidDataException("Download ended before the representation length.");
                job.ActualSha256 = Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
                if (job.ExpectedSha256.Length > 0 && !job.ActualSha256.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 checksum mismatch.");
                await SaveCheckpointAsync();
            }
            catch (OperationCanceledException)
            {
                await SaveCheckpointAsync(); // No cancelled token: pause must durably settle accepted bytes before releasing ownership.
                throw;
            }
            verified = true; job.State = "Verified"; progress?.Report(job);

            async Task SaveCheckpointAsync()
            {
                await target!.FlushAsync(CancellationToken.None); target.Flush(true);
                checkpoint?.Invoke("AfterFlush", job);
                if (persist is null) return;
                var previous = job.HttpCheckpoint!;
                var next = new HttpResumeCheckpoint { Identity = previous.Identity, FileIdentity = previous.FileIdentity,
                    StrongETag = previous.StrongETag, TotalBytes = previous.TotalBytes, Cursor = job.DownloadedBytes,
                    PrefixSha256 = Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant() };
                job.HttpCheckpoint = next;
                try { await persist(next); }
                catch { job.HttpCheckpoint = previous; throw; }
                checkpoint?.Invoke("AfterCheckpoint", job);
            }
        }
        catch (OperationCanceledException) { job.State = HttpResume.Eligible(job) ? "Paused" : "Cancelled"; throw; }
        catch (Exception ex)
        {
            // Protocol/integrity errors require an explicit restart; local save faults may retry the last trusted checkpoint.
            var resumable = job.HttpCheckpoint is { TotalBytes: > 0 } state && HttpResume.Strong(state.StrongETag) && job.SelectionIdentity.Length > 0;
            if (job.HttpCheckpoint is not null && (ex is HttpRequestException or HttpIOException or InvalidDataException)) job.HttpCheckpoint.RestartRequired = true;
            job.State = resumable ? "Needs attention" : ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase) ? "Integrity failed" : "Failed";
            job.Error = ex is HttpRequestException ? "HTTP transfer failed. Keep the partial or explicitly Restart; no provider link was refreshed." : ex.Message;
            throw;
        }
        finally
        {
            try
            {
                try { target?.Dispose(); }
                finally { marker?.Dispose(); }
                job.CanPause = false;
                job.CanResume = localValidated && HttpResume.Eligible(job) && job.State is ("Paused" or "Needs attention");
                if (!resume && !verified && job.HttpCheckpoint is null && !DownloadCompletion.Unresolved(job) && job.StagingPath.Length > 0 &&
                    (persist is null || !File.Exists(job.StagingPath)))
                {
                    BrowserDownloadFiles.DiscardStaging(job); job.StagingPath = job.StagingRoot = job.StagingOwner = "";
                }
            }
            finally { Writers.TryRemove(writerKey, out _); }
        }
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
    {
        var text = System.Text.Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 256)]).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').ToLowerInvariant();
        return text.StartsWith("<!doctype html") || text.StartsWith("<html");
    }
    private static long DriveFree(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    public void Dispose() => client.Dispose();
}
