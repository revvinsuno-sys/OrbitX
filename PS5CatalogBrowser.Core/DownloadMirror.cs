namespace PS5CatalogBrowser.Core;

public sealed class DownloadMirror
{
    public required CatalogArtifact Artifact { get; init; }
    public required string Url { get; init; }
    public string Hostname => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "Invalid URL";
    public string Provider { get; init; } = "";
    public string Group { get; init; } = "";
    public string ExpectedFileName { get; init; } = "";
    public string GroupLabel { get; init; } = "";
    public bool DownloadsEnabled { get; init; } = true;
    public bool IsOrbit { get; init; }
    public ReleaseWorkflow Workflow => OrbitDownloadPolicy.Workflow(Artifact);
    public bool DirectEnabled => DownloadsEnabled && AkiraBoxLink.FileId(Url) is null && (!IsOrbit || Workflow == ReleaseWorkflow.Direct);
    public bool BrowserEnabled => DownloadsEnabled && (!IsOrbit || Workflow == ReleaseWorkflow.Browser);
    public string BrowserUrl => IsOrbit ? OrbitDownloadPolicy.BrowserUrl(Artifact) : Url;
    public string BrowserInstructions => AkiraBoxLink.FileId(BrowserUrl) is not null ? AkiraBoxLink.Instructions :
        Uri.TryCreate(BrowserUrl, UriKind.Absolute, out var uri) && uri.Host is "vikingfile.com" or "www.vikingfile.com"
            ? "Complete any verification, then click Download on Vikingfile. Matching file offers continue automatically; review other offers in this app. Keep the provider window open while it downloads." :
        Uri.TryCreate(BrowserUrl, UriKind.Absolute, out uri) && uri.Host is "datanodes.to" or "www.datanodes.to"
            ? "Complete DataNodes verification and any countdown, then use the site's download option. Matching file offers continue automatically; review other offers in this app. Keep the provider window open while transferring." :
        "Complete any verification or sign-in on the provider page, then use its download button. Matching file offers continue automatically; review other offers in this app. Keep that window open while transferring.";
    public string ContainerLabel => Artifact.Compression.Length > 0 ? Artifact.Compression : "None specified";
    public string Availability => !IsOrbit ? "" : Workflow switch {
        ReleaseWorkflow.Direct => "Direct candidate — response and integrity validation required",
        ReleaseWorkflow.Browser => "Browser workflow — provider operation unverified; external import available",
        _ => "Unknown or unsupported — external browser and completed-file import" };
    public string Format => Artifact.Format.Length > 0 ? Artifact.Format : "Unknown";
    public string ExpectedFileLabel => ExpectedFileName.Length > 0 ? ExpectedFileName : "Not provided";
    public string SizeLabel => DownloadSize.Gigabytes(Artifact.Size);

    public static List<DownloadMirror> ForEntry(CatalogEntry entry)
    {
        var result = new List<DownloadMirror>();
        var linkNumber = 0;
        foreach (var artifact in entry.Artifacts)
        {
            if (entry.CatalogFormat == "Orbit")
            {
                result.Add(new DownloadMirror { Artifact = artifact, Url = artifact.Url, Provider = artifact.Provider,
                    ExpectedFileName = artifact.FileName, IsOrbit = true,
                    Group = string.Join(" · ", new[] { artifact.Version, artifact.Region, artifact.Format }.Where(x => x.Length > 0)),
                    GroupLabel = "Release " + artifact.Id });
                continue;
            }
            linkNumber++;
            var parts = artifact.SourceSet.Split(" · ", StringSplitOptions.RemoveEmptyEntries);
            var group = parts.FirstOrDefault() ?? "";
            var provider = parts.Skip(1).FirstOrDefault(p => p != artifact.Version && p != artifact.Region && p != artifact.EditionId);
            var file = artifact.FileName;
            if (file.Length == 0 && entry.CatalogFormat != "OptiStore" && Path.HasExtension(artifact.Name)) file = artifact.Name;
            if (file.Length == 0 && Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) && Path.HasExtension(uri.LocalPath))
                file = Path.GetFileName(uri.LocalPath);
            var release = string.Join(" · ", new[] { group, artifact.Version, artifact.Region, artifact.EditionId }.Where(x => x.Length > 0).Distinct());
            // Unknown filenames cannot safely establish that two links are alternatives.
            var label = file.Length > 0 ? $"Alternatives for {file}" : $"Link {linkNumber}: {artifact.Name} (filename unknown)";
            if (release.Length > 0) label += " · " + release;
            foreach (var url in new[] { artifact.Url }.Concat(artifact.Mirrors).Distinct(StringComparer.Ordinal))
                result.Add(new DownloadMirror { Artifact = artifact, Url = url, Provider = url == artifact.Url && provider is { Length: > 0 } ? provider :
                    Uri.TryCreate(url, UriKind.Absolute, out var host) ? host.Host : "Unknown provider",
                    Group = release, ExpectedFileName = file, GroupLabel = label });
        }
        return result;
    }
}
