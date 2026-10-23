using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

// WebView2 can drop an in-flight throttle when its profile changes. Reserve one
// network slot before provider navigation or HTTP transfer and freeze its rate.
// Writer ownership and verification remain with the existing download managers.
internal sealed class DownloadBandwidthCoordinator(double megabytes)
{
    private readonly SemaphoreSlim slot = new(1, 1);
    private double limit = megabytes;
    private long rate;
    private CoreWebView2? browser;
    private bool direct;
    private int occupied;
    internal long Share => Volatile.Read(ref rate);
    internal bool IsBusy => Volatile.Read(ref occupied) != 0;
    internal DownloadBandwidthLimiter CreateLimiter() => new(() => Share);
    internal Task AttachAsync(CoreWebView2 core, CancellationToken cancellation) => BeginBrowserAsync(core, cancellation);
    internal async Task BeginBrowserAsync(CoreWebView2 core, CancellationToken cancellation)
    {
        if (ReferenceEquals(browser, core)) return;
        await slot.WaitAsync(cancellation);
        Volatile.Write(ref occupied, 1);
        try
        {
            SetActiveRate();
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions", JsonSerializer.Serialize(new {
                offline = false, latency = 0, downloadThroughput = Share == 0 ? -1 : Share, uploadThroughput = -1
            }));
            cancellation.ThrowIfCancellationRequested();
            browser = core;
        }
        catch { Volatile.Write(ref rate, 0); Volatile.Write(ref occupied, 0); slot.Release(); throw; }
    }
    internal void EndBrowser(CoreWebView2? core)
    { if (browser is not null && ReferenceEquals(browser, core)) { browser = null; Volatile.Write(ref rate, 0); Volatile.Write(ref occupied, 0); slot.Release(); } }
    internal void Detach(CoreWebView2 core) => EndBrowser(core);
    internal async Task BeginDirectAsync(CancellationToken cancellation)
    {
        await slot.WaitAsync(cancellation);
        Volatile.Write(ref occupied, 1); direct = true; SetActiveRate();
    }
    internal void EndDirect() { if (direct) { direct = false; Volatile.Write(ref rate, 0); Volatile.Write(ref occupied, 0); slot.Release(); } }
    internal void SetLimit(double value) => Volatile.Write(ref limit, value);
    private void SetActiveRate() => Volatile.Write(ref rate, limit <= 0 ? 0 : Math.Max(1, (long)(limit * 1_000_000)));
}
