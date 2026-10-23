using System.Diagnostics;

namespace PS5CatalogBrowser.Core;

public sealed class DownloadBandwidthLimiter(Func<long> bytesPerSecond)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private double next;
    public async Task WaitAsync(int bytes, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var rate = bytesPerSecond();
            if (rate <= 0) { next = 0; return; }
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            next = Math.Max(now - .05, next) + bytes / (double)rate;
            while (next > now)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(.1, next - now)), cancellation);
                now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                var updated = bytesPerSecond();
                if (updated <= 0) { next = 0; return; }
                if (updated != rate) { next = now + Math.Max(0, next - now) * rate / updated; rate = updated; }
            }
        }
        catch (OperationCanceledException) { next = 0; throw; }
        finally { gate.Release(); }
    }
}
