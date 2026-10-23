namespace PS5CatalogBrowser.Core;

public enum ReleaseWorkflow { Direct, Browser, External }

public static class OrbitDownloadPolicy
{
    public static bool DirectUrl(string value)
    {
        if (!SafeUri(value, out var uri)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return uri.Host == "archive.org" && uri.Port == 443 && parts.Length >= 3 && parts[0] == "download" && !uri.AbsolutePath.EndsWith('/');
    }

    public static string BrowserUrl(CatalogArtifact artifact) => artifact.BrowserUrl.Length > 0 ? artifact.BrowserUrl : artifact.Url;

    public static ReleaseWorkflow Workflow(CatalogArtifact artifact)
    {
        if (DirectUrl(artifact.Url)) return ReleaseWorkflow.Direct;
        if (AkiraBoxLink.FileId(BrowserUrl(artifact)) is not null) return ReleaseWorkflow.Browser;
        if (!SafeUri(BrowserUrl(artifact), out var uri)) return ReleaseWorkflow.External;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Only page routes, never provider labels or an arbitrary URL with a file extension.
        if (uri.Port == 443 && ((uri.Host is "vikingfile.com" or "www.vikingfile.com") && parts.Length == 2 && parts[0] == "f" ||
            (uri.Host is "datanodes.to" or "www.datanodes.to") && parts.Length >= 1 && parts[0].Length == 12 && parts[0].All(char.IsAsciiLetterOrDigit)))
            return ReleaseWorkflow.Browser;
        return ReleaseWorkflow.External;
    }

    private static bool SafeUri(string value, out Uri uri)
    {
        uri = null!;
        try { uri = NetworkPolicy.Validate(value); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public static void ValidateSelection(CatalogEntry entry, CatalogArtifact artifact)
    {
        if (entry.CatalogFormat != "Orbit") return;
        if (!entry.Artifacts.Contains(artifact) || entry.SourceId.Length == 0 || entry.Id.Length == 0 || artifact.Id.Length == 0 ||
            artifact.FileName.Length == 0 || artifact.Size is not > 0)
            throw new InvalidOperationException("Select a release belonging to this Orbit game.");
    }

    public static void ValidateFileName(CatalogEntry entry, CatalogArtifact artifact, string offered)
    {
        ValidateSelection(entry, artifact);
        if (entry.CatalogFormat == "Orbit" && !Path.GetFileName(offered).Equals(artifact.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("The offered filename does not match the selected Orbit release. Use its matching completed file.");
    }
}

// Only the selected release's transfer metadata is persisted; no catalogue, artwork, key or password.
public sealed record OrbitReleaseSelection(string GameId, string Platform, string ReleaseId, string ProviderSourceId,
    string Provider, string FileName, string Version, string Region, string ContentFormat, string ContainerFormat,
    string Url, string BrowserUrl, long? Size, string Sha256)
{
    public static OrbitReleaseSelection? Capture(CatalogEntry entry, CatalogArtifact artifact) => entry.CatalogFormat != "Orbit" ? null :
        new(entry.GameId, entry.Platform, artifact.Id, artifact.ProviderSourceId, artifact.Provider, artifact.FileName,
            artifact.Version, artifact.Region, artifact.Format, artifact.Compression, artifact.Url, artifact.BrowserUrl, artifact.Size, artifact.Sha256);
}
