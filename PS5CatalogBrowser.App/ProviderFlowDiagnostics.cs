using System.Text.Json;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

// UI-thread only. Raw URLs are classified immediately and never retained.
internal sealed class ProviderFlowDiagnostics(string selection)
{
    internal sealed record FlowEvent(long Sequence, DateTimeOffset Timestamp, string Event, string Host,
        string Route, bool? UserInitiated, string Decision, string State, ulong? NavigationId);
    private readonly Queue<FlowEvent> events = new();
    private long sequence;
    public bool Enabled { get; set; }
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public string Selection { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(selection)));

    public void Record(string name, string? url = null, bool? userInitiated = null,
        string decision = "", string state = "", ulong? navigationId = null)
    {
        if (!Enabled) return;
        var host = ""; var route = "none";
        if (url is not null)
        {
            try
            {
                var uri = NetworkPolicy.Validate(url); host = uri.IdnHost;
                route = uri.IsLoopback ? "local-fixture" : OrbitDownloadPolicy.DirectUrl(url) ? "catalogue-direct-route" :
                    OrbitDownloadPolicy.Workflow(new() { Url = url }) == ReleaseWorkflow.Browser ? "provider-page-route" : "https-unclassified";
            }
            catch { route = "unsafe-or-unsupported"; }
        }
        if (events.Count == 256) events.Dequeue();
        events.Enqueue(new(++sequence, DateTimeOffset.UtcNow, name, host, route, userInitiated, decision, state, navigationId));
    }

    public FlowEvent[] Snapshot() => events.ToArray();
    public string Export() => JsonSerializer.Serialize(new { Session, Selection, Events = Snapshot() }, new JsonSerializerOptions { WriteIndented = true });
}
