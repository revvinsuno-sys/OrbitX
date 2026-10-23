using System.Security.Cryptography;

namespace PS5CatalogBrowser.Core;

/// <summary>Official encrypted feed only; keys are read from local files and catalogues remain in memory.</summary>
public static class LiveOrbitCatalog
{
    public const string OfficialUrl = "https://raw.githubusercontent.com/saawant12/orbit-store-ps5/main/catalogue-v7.enc";
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(6);

    public static void ValidateUrl(string url)
    {
        // Pin the published repository and feed paths. No credentials, query strings, mirrors or provider pages.
        if (url != OfficialUrl && url != OfficialUrl.Replace("-v7.enc", "-v6.enc", StringComparison.Ordinal))
            throw new ArgumentException("Use the official Orbit HTTPS catalogue-v6.enc or catalogue-v7.enc endpoint on raw.githubusercontent.com.");
    }

    public static async Task<OrbitCatalogResult> LoadAsync(CatalogSource source, HttpTransfer transport, CancellationToken token = default)
    {
        if (!source.Enabled) throw new InvalidOperationException("This Orbit source is disabled.");
        ValidateUrl(source.Url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var envelope = await transport.LoadOrbitEnvelopeAsync(source.Url, timeout.Token).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => LocalOrbitCatalog.Decode(source, envelope, timeout.Token), timeout.Token).ConfigureAwait(false);
            var expected = source.Url == OfficialUrl ? 7 : 6;
            if (result.FeedVersion != expected) throw new InvalidDataException("Orbit feed generation does not match the configured endpoint.");
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(envelope); }
    }

    public static void ValidateUpdate(OrbitCatalogResult? current, OrbitCatalogResult next)
    {
        if (current is null) return;
        if (next.Revision < current.Revision || next.FeedVersion < current.FeedVersion)
            throw new InvalidDataException("Older Orbit generation or revision received. Keeping the current catalogue.");
        if (next.Revision == current.Revision && next.FeedVersion == current.FeedVersion && next.ContentFingerprint != current.ContentFingerprint)
            throw new InvalidDataException("Orbit catalogue changed without a new revision. Keeping the current catalogue.");
    }
}
