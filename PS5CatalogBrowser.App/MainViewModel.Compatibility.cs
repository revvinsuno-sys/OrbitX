using System.Net.Http;
using System.IO;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    public string CurrentKytyVersion { get; private set; } = "";
    private void RefreshKytyVersion()
    {
        CurrentKytyVersion = KytyVersion.Read(Settings.EmulatorPath);
        Changed(nameof(CurrentKytyVersion)); Changed(nameof(Compatibility));
        RefreshKytyUpdateAvailability();
    }
    internal CompatibilityTestVersion? LibraryTestVersion(LocalLibraryEntry item) =>
        Settings.CompatibilityTestVersions.GetValueOrDefault(Settings.CompatibilityOverrides.ContainsKey("library:" + item.Id) ? "library:" + item.Id : CompatibilityKey(item));
    internal string LibraryWebsiteCompatibility(LocalLibraryEntry item) => Compatibility is null ? "unavailable" : Compatibility.GetValueOrDefault(LibraryTitleId(item), "untested");
    internal IReadOnlyList<KytyCommunityReport> LibraryWebsiteReports(LocalLibraryEntry item) => CommunityReports.GetValueOrDefault(LibraryTitleId(item)) ?? [];
    internal Dictionary<string, IReadOnlyList<KytyCommunityReport>> CommunityReports { get; } = [];
    internal CatalogEntry? LibraryCatalogEntry(LocalLibraryEntry item)
    {
        var matches = Entries.Where(entry => entry.Id == item.CatalogEntryId &&
            (entry.SourceId == item.SourceId || item.SourceId.Length == 0 && item.DownloadArtifacts.Any(a => a.SourceId == entry.SourceId))).Take(2).ToArray();
        if (matches.Length == 1) return matches[0];
        // An inspected package ID can also supply artwork when no catalogue registration exists.
        // Never match the display title or filename, and leave ambiguous IDs unresolved.
        var titleId = InstalledTitleId(item);
        if (!System.Text.RegularExpressions.Regex.IsMatch(titleId, "^(PPSA|CUSA)[0-9]{5}$")) return null;
        var byTitleId = Entries.Where(entry => (item.SourceId.Length == 0 || entry.SourceId == item.SourceId) &&
            new[] { entry.TitleId }.Concat(entry.Artifacts.Select(a => a.TitleId)).Any(id => id.Replace("-", "").Equals(titleId, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(entry => (entry.Platform, Game: entry.GameId.Length > 0 ? entry.GameId : entry.Title)).Take(2).ToArray();
        return byTitleId.Length == 1 ? byTitleId[0].First() : null;
    }
    internal void RelinkLibraryEntry(LocalLibraryEntry item, CatalogEntry entry)
    {
        var platform = entry.Platform;
        if (!Library.Contains(item) || !Entries.Contains(entry) ||
            !(platform.Equals("PS4", StringComparison.OrdinalIgnoreCase) || platform.Equals("PS5", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a PS4 or PS5 catalogue game and a retained library item.");
        var previous = (item.CatalogEntryId, item.SourceId, item.CoverUrl, item.CoverFallbackUrl);
        item.CatalogEntryId = entry.Id; item.SourceId = entry.SourceId;
        item.CoverUrl = entry.CoverUrl; item.CoverFallbackUrl = entry.CoverFallbackUrl;
        try { Save(); }
        catch
        {
            item.CatalogEntryId = previous.CatalogEntryId; item.SourceId = previous.SourceId;
            item.CoverUrl = previous.CoverUrl; item.CoverFallbackUrl = previous.CoverFallbackUrl;
            throw;
        }
        Status = $"Linked {entry.Title} ({entry.Platform}) to its local game files.";
        Changed(nameof(Library));
    }
    private static string InstalledTitleId(LocalLibraryEntry item) => KytyCompatibility.NormalizeId(item.InstalledGame?.TitleId ?? "") is { Length: > 0 } installed
        ? installed : KytyCompatibility.NormalizeId(item.InspectedTitleId);
    internal string LibraryDisplayTitle(LocalLibraryEntry item) => item.InspectedTitle.Length > 0 ? item.InspectedTitle : LibraryCatalogEntry(item)?.Title ?? item.Title;
    internal string LibraryTitleId(LocalLibraryEntry item)
    {
        var installed = InstalledTitleId(item);
        if (installed.Length > 0) return installed;
        var entry = LibraryCatalogEntry(item);
        var ids = new[] { entry?.TitleId ?? "" }.Concat(entry?.Artifacts.Select(a => a.TitleId) ?? [])
            .Select(KytyCompatibility.NormalizeId).Where(id => id.Length > 0).Distinct().Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : "";
    }
    private string CompatibilityKey(LocalLibraryEntry item) => LibraryTitleId(item) is { Length: > 0 } id ? id : "library:" + item.Id;
    internal string LibraryCompatibility(LocalLibraryEntry item) => LibraryCompatibilityOverride(item) is { Length: > 0 } local ? local
        : Compatibility is null ? "unavailable" : Compatibility.GetValueOrDefault(LibraryTitleId(item), "untested");
    internal string LibraryCompatibilityOverride(LocalLibraryEntry item)
    {
        var status = Settings.CompatibilityOverrides.GetValueOrDefault("library:" + item.Id, Settings.CompatibilityOverrides.GetValueOrDefault(CompatibilityKey(item), ""));
        return KytyCompatibility.Statuses.Contains(status) ? status : "";
    }
    internal string LibrarySharpEmuCompatibilityOverride(LocalLibraryEntry item)
    {
        var key = LibraryTitleId(item) is { Length: > 0 } id ? id : "library:" + item.Id;
        var status = Settings.SharpEmuCompatibilityOverrides.GetValueOrDefault(key) ?? "";
        return SharpEmuCompatibility.Statuses.Contains(status, StringComparer.Ordinal) && status != "untested" ? status : "";
    }
    internal string LibrarySharpEmuOfficialCompatibility(LocalLibraryEntry item) => OfficialSharpEmuCompatibility is null ? "unavailable"
        : OfficialSharpEmuCompatibility.GetValueOrDefault(LibraryTitleId(item), "untested");
    internal CompatibilityTestVersion? LibrarySharpEmuTestVersion(LocalLibraryEntry item)
    {
        var key = LibraryTitleId(item) is { Length: > 0 } id ? id : "library:" + item.Id;
        return Settings.SharpEmuCompatibilityTestVersions.GetValueOrDefault(key);
    }
    internal void SetLibraryCompatibility(LocalLibraryEntry item, string status) => SetLibraryCompatibility(item, status, null);
    internal void SetLibraryCompatibility(LocalLibraryEntry item, string status, string? sharpEmuStatus)
    {
        if (!Library.Contains(item) || status.Length > 0 && !KytyCompatibility.Statuses.Contains(status) ||
            sharpEmuStatus is { Length: > 0 } && (!SharpEmuCompatibility.Statuses.Contains(sharpEmuStatus, StringComparer.Ordinal) || sharpEmuStatus == "untested"))
            throw new InvalidOperationException("Select a retained library item and known compatibility statuses.");
        var previous = new Dictionary<string, string>(Settings.CompatibilityOverrides);
        var previousVersions = new Dictionary<string, CompatibilityTestVersion>(Settings.CompatibilityTestVersions);
        var previousSharp = new Dictionary<string, string>(Settings.SharpEmuCompatibilityOverrides);
        var previousSharpVersions = new Dictionary<string, CompatibilityTestVersion>(Settings.SharpEmuCompatibilityTestVersions);
        var key = CompatibilityKey(item);
        Settings.CompatibilityOverrides.Remove("library:" + item.Id);
        Settings.CompatibilityTestVersions.Remove("library:" + item.Id);
        if (status.Length == 0) Settings.CompatibilityTestVersions.Remove(key);
        else Settings.CompatibilityTestVersions[key] = new(CurrentKytyVersion, DateTimeOffset.UtcNow);
        if (status.Length == 0) Settings.CompatibilityOverrides.Remove(key); else Settings.CompatibilityOverrides[key] = status;
        if (sharpEmuStatus is not null)
        {
            var sharpKey = LibraryTitleId(item) is { Length: > 0 } id ? id : "library:" + item.Id;
            if (sharpEmuStatus.Length == 0)
            {
                Settings.SharpEmuCompatibilityOverrides.Remove(sharpKey);
                Settings.SharpEmuCompatibilityTestVersions.Remove(sharpKey);
            }
            else
            {
                Settings.SharpEmuCompatibilityOverrides[sharpKey] = sharpEmuStatus;
                Settings.SharpEmuCompatibilityTestVersions[sharpKey] = new(SharpEmuInstalledVersion, DateTimeOffset.UtcNow);
            }
        }
        try { Save(); }
        catch
        {
            Settings.CompatibilityOverrides = previous; Settings.CompatibilityTestVersions = previousVersions;
            Settings.SharpEmuCompatibilityOverrides = previousSharp; Settings.SharpEmuCompatibilityTestVersions = previousSharpVersions;
            throw;
        }
        Changed(nameof(Compatibility)); Changed(nameof(OfficialSharpEmuCompatibility)); Changed(nameof(Library));
    }
    public IReadOnlyDictionary<string, string>? Compatibility { get; private set; }
    public IReadOnlyDictionary<string, string>? OfficialSharpEmuCompatibility { get; private set; }
    public async Task RefreshSharpEmuCompatibilityAsync() => await RefreshSharpEmuCompatibilityAsync(SharpEmuCompatibility.LoadAsync);
    internal async Task RefreshSharpEmuCompatibilityAsync(Func<CancellationToken, Task<Dictionary<string, string>>> load)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(catalogueLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            OfficialSharpEmuCompatibility = await load(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException or OperationCanceledException)
        { /* Feed availability must never block browsing or imply a compatibility result. */ }
        if (!disposed) Changed(nameof(OfficialSharpEmuCompatibility));
    }
    public async Task RefreshCompatibilityAsync()
    {
        await RefreshCompatibilityAsync(KytyCompatibility.LoadAsync);
        var ids = Library.Select(LibraryTitleId).Where(id => id.Length > 0).Distinct().ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(catalogueLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<KytyCommunityReport>>();
        try
        {
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (id, cancellation) =>
            {
                try
                {
                    var reports = await KytyCompatibility.LoadReportsAsync(id, cancellation);
                    loaded[id] = reports;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OperationCanceledException) { }
            });
        }
        catch (OperationCanceledException) { }
        foreach (var report in loaded) CommunityReports[report.Key] = report.Value;
        if (!disposed) Changed(nameof(Compatibility));
    }
    internal async Task RefreshCompatibilityAsync(Func<CancellationToken, Task<Dictionary<string, string>>> load)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(catalogueLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            Compatibility = await load(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OperationCanceledException)
        { /* Feed availability must never block browsing or imply a compatibility result. */ }
        if (!disposed) Changed(nameof(Compatibility));
    }
}
