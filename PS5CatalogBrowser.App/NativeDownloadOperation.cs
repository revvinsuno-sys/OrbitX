using Microsoft.Web.WebView2.Core;

namespace PS5CatalogBrowser.App;

// A narrow forwarding seam for deterministic native lifecycle/fault tests.
internal interface INativeDownloadOperation
{
    string Uri { get; }
    string MimeType { get; }
    ulong? TotalBytesToReceive { get; }
    long BytesReceived { get; }
    CoreWebView2DownloadState State { get; }
    CoreWebView2DownloadInterruptReason InterruptReason { get; }
    bool CanResume { get; }
    event EventHandler<object> BytesReceivedChanged;
    event EventHandler<object> EstimatedEndTimeChanged;
    event EventHandler<object> StateChanged;
    void Cancel();
    void Pause();
    void Resume();
}

internal sealed class NativeDownloadOperation(CoreWebView2DownloadOperation operation) : INativeDownloadOperation
{
    public string Uri => operation.Uri;
    public string MimeType => operation.MimeType;
    public ulong? TotalBytesToReceive => operation.TotalBytesToReceive;
    public long BytesReceived => operation.BytesReceived;
    public CoreWebView2DownloadState State => operation.State;
    public CoreWebView2DownloadInterruptReason InterruptReason => operation.InterruptReason;
    public bool CanResume => operation.CanResume;
    public event EventHandler<object> BytesReceivedChanged { add => operation.BytesReceivedChanged += value; remove => operation.BytesReceivedChanged -= value; }
    public event EventHandler<object> EstimatedEndTimeChanged { add => operation.EstimatedEndTimeChanged += value; remove => operation.EstimatedEndTimeChanged -= value; }
    public event EventHandler<object> StateChanged { add => operation.StateChanged += value; remove => operation.StateChanged -= value; }
    public void Cancel() => operation.Cancel();
    public void Pause() => operation.Pause();
    public void Resume() => operation.Resume();
}
