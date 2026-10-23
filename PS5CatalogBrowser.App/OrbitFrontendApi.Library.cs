using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

internal sealed partial class OrbitFrontendApi
{
    private readonly Dictionary<string, (string Token, long Started, List<LibraryDeletionTarget> Targets)> deletionHolds = [];
    private static string LibraryArt(string recorded, string? catalog)
    {
        foreach (var url in new[] { recorded, catalog })
            if (!string.IsNullOrWhiteSpace(url))
                try { return NetworkPolicy.Validate(url).AbsoluteUri; } catch (InvalidOperationException) { }
        return "";
    }
    private (int Status, object Body) LibraryRequest(string path, string? body)
    {
        if (body is null || body.Length > 1024) return (400, new { error = "Invalid library request." });
        try
        {
            using var json = JsonDocument.Parse(body, new() { MaxDepth = 2 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (400, new { error = "Invalid library schema." });
            var fields = root.EnumerateObject().ToArray();
            var allowed = path.EndsWith("/prepare", StringComparison.Ordinal) || path.EndsWith("/log", StringComparison.Ordinal) || path.EndsWith("/details", StringComparison.Ordinal) ? new[] { "id" } :
                path.EndsWith("/confirm", StringComparison.Ordinal) ? new[] { "id", "token" } : new[] { "id", "status", "sharpEmuStatus" };
            var validFields = fields.Select(f => f.Name).Distinct().Count() == fields.Length &&
                (fields.Length == allowed.Length || path.EndsWith("/compatibility", StringComparison.Ordinal) && fields.Length == 2) &&
                fields.All(f => allowed.Contains(f.Name) && f.Value.ValueKind == JsonValueKind.String);
            if (!validFields ||
                !root.TryGetProperty("id", out var idJson) || idJson.GetString() is not { Length: > 0 and <= 100 } id)
                return (400, new { error = "Invalid library arguments." });
            var item = model.LibraryView.Cast<LocalLibraryEntry>().SingleOrDefault(item => item.Id == id);
            if (item is null) return (409, new { error = "This library item is no longer available." });
            if (path.EndsWith("/log", StringComparison.Ordinal)) return (200, new { text = model.ReadLaunchLog(item) });
            if (path.EndsWith("/details", StringComparison.Ordinal))
            {
                var job = model.Installations.FirstOrDefault(job => job.Id == item.InstalledGame?.InstallationId);
                return job is null ? (409, new { error = "Installation details are unavailable for this item." }) : (200, new
                {
                    fields = new Dictionary<string, string> {
                        ["Title"] = job.Title, ["Title ID"] = job.TitleId, ["Catalogue source"] = job.CatalogSource,
                        ["Original"] = job.SourcePath, ["Destination"] = job.Destination,
                        ["Files"] = job.Files.Count.ToString("N0"), ["Size"] = $"{job.TotalBytes:N0} bytes",
                        ["Installed"] = job.InstalledAt?.ToString() ?? "", ["State"] = job.StateLabel,
                        ["Full verification"] = job.Verified ? "Yes" : "No", ["Persisted registration"] = job.Registered ? "Yes" : "No",
                        ["Mount"] = job.MountDescription, ["Cleanup"] = job.CleanupResult, ["Error"] = job.Error }
                });
            }
            if (path.EndsWith("/compatibility", StringComparison.Ordinal))
            {
                model.SetLibraryCompatibility(item, root.GetProperty("status").GetString()!, root.TryGetProperty("sharpEmuStatus", out var sharpStatus) ? sharpStatus.GetString()! : null);
                return (200, new { saved = true });
            }
            if (!Allows("deleteLibrary", id)) return (409, new { error = "Deletion is unavailable while this item is busy." });
            if (path.EndsWith("/prepare", StringComparison.Ordinal))
            {
                foreach (var stale in deletionHolds.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Started).TotalSeconds > 30).Select(pair => pair.Key).ToArray()) deletionHolds.Remove(stale);
                var targets = model.PrepareLibraryDeletion(item);
                var token = Guid.NewGuid().ToString("N");
                deletionHolds[id] = (token, Stopwatch.GetTimestamp(), targets);
                return (200, new { token, paths = targets.Where(target => !target.Completed).Select(target => target.Path), holdMilliseconds = 2000 });
            }
            if (!deletionHolds.TryGetValue(id, out var hold) || hold.Token != root.GetProperty("token").GetString())
                return (409, new { error = "Hold Delete again to confirm the current files." });
            var elapsed = Stopwatch.GetElapsedTime(hold.Started).TotalSeconds;
            if (elapsed < 2 || elapsed > 30) return (409, new { error = "Hold Delete continuously for two seconds." });
            deletionHolds.Remove(id);
            if (ActionRequested is null) return (503, new { error = "Windows controls are unavailable." });
            ActionRequested.Invoke(new("deleteLibrary", id, hold.Targets));
            return (202, new { accepted = true });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        { return (400, new { error = ex.Message }); }
    }
}
