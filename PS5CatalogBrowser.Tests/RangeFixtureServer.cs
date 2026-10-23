using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

internal sealed class RangeFixtureServer : IDisposable
{
    public const string ETag = "\"synthetic-v1\"";
    public byte[] Bytes { get; }
    public string Digest => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task serving;
    private readonly ConcurrentBag<Task> connections = [];
    private readonly int delay;
    public ConcurrentQueue<(string Range, string IfRange)> Requests { get; } = new();
    public RangeFixtureServer(int size = 512 * 1024, int delayMilliseconds = 3)
    { Bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 37 + 11)).ToArray(); delay = delayMilliseconds; listener.Start(); serving = Task.Run(ServeAsync); }
    public string Url(string mode) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/{mode}";
    private async Task ServeAsync()
    {
        try { while (!stopping.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stopping.Token); connections.Add(RespondAsync(client)); } }
        catch (OperationCanceledException) { }
    }
    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var first = await reader.ReadLineAsync(stopping.Token); if (first is null) return;
            var mode = first.Split(' ')[1].TrimStart('/').Split('?')[0];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stopping.Token)))
            { var n = line.IndexOf(':'); headers[line[..n]] = line[(n + 1)..].Trim(); }
            var range = headers.GetValueOrDefault("Range", ""); var ifRange = headers.GetValueOrDefault("If-Range", ""); Requests.Enqueue((range, ifRange));
            var resumed = range.Length > 0; var offset = resumed ? int.Parse(range[6..].TrimEnd('-')) : 0;
            if (mode == "redirect-good")
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {Url("good")}?temporary_capability=DO_NOT_PERSIST\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stopping.Token);
                return;
            }
            var status = resumed ? 206 : 200; var start = offset; var total = Bytes.Length;
            if (resumed && mode == "ignored") { status = 200; start = 0; }
            if (resumed && mode is "412" or "416") status = int.Parse(mode);
            if (resumed && mode == "start") start++;
            if (resumed && mode == "total") total++;
            if (resumed && mode == "redirect-unsafe")
            { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://example.test/unsafe\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stopping.Token); return; }
            var tag = mode == "weak" || resumed && mode == "weak-resume" ? "W/" + ETag : resumed && mode == "etag" ? "\"synthetic-v2\"" : ETag;
            var missingTag = mode == "missing" || resumed && mode == "missing-resume";
            var unknownLength = mode == "unknown" || resumed && mode is "excess" or "resume-unknown";
            var count = Bytes.Length - start;
            var advertised = count + (resumed && mode == "length" ? 1 : 0);
            var response = $"HTTP/1.1 {status} Fixture\r\nConnection: close\r\nAccept-Ranges: bytes\r\nLast-Modified: Wed, 14 Oct 2026 12:00:00 GMT\r\nContent-Type: {(resumed && mode == "html" ? "text/html" : "application/octet-stream")}\r\n";
            if (!missingTag) response += "ETag: " + tag + "\r\n";
            if (!unknownLength) response += $"Content-Length: {advertised}\r\n";
            if (resumed && status == 206) response += $"Content-Range: bytes {start}-{total - 1}/{total}\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), stopping.Token);
            if (resumed && mode == "short") count /= 2;
            var body = resumed && mode == "etag" ? Bytes.Select(b => (byte)(b ^ 255)).ToArray() : Bytes;
            for (var position = start; position < start + count; position += 8192)
            {
                await stream.WriteAsync(body.AsMemory(position, Math.Min(8192, start + count - position)), stopping.Token);
                if (delay > 0) await Task.Delay(delay, stopping.Token);
            }
            if (resumed && mode == "excess") await stream.WriteAsync("extra bytes"u8.ToArray(), stopping.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
    public void Dispose()
    { stopping.Cancel(); listener.Stop(); serving.GetAwaiter().GetResult(); Task.WhenAll(connections).GetAwaiter().GetResult(); stopping.Dispose(); }
}
