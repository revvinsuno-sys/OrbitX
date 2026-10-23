using System.IO;
using System.Text.Json;

namespace PS5CatalogBrowser.App;

internal sealed partial class OrbitFrontendApi
{
    private (int Status, object Body) LibraryRelinkRequest(string path, string? body)
    {
        if (body is null || body.Length > 2048) return (400, new { error = "Invalid relink request." });
        try
        {
            using var document = JsonDocument.Parse(body, new() { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (400, new { error = "Invalid relink request." });
            var fields = root.EnumerateObject().ToArray();
            string Text(string name, int maxLength) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } text && text.Length <= maxLength && !text.Contains('\0') ? text : throw new ArgumentException("Invalid relink request.");

            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                if (fields.Length != 1 || fields[0].Name != "query") return (400, new { error = "Search for a catalogue game by title." });
                var query = Text("query", 120).Trim();
                if (query.Length < 2) return (400, new { error = "Enter at least two characters to search." });
                var matches = model.Entries.Where(entry => (entry.Platform.Equals("PS4", StringComparison.OrdinalIgnoreCase) || entry.Platform.Equals("PS5", StringComparison.OrdinalIgnoreCase)) && entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase).Take(30)
                    .Select(entry => new { id = entry.Id, sourceId = entry.SourceId, source = entry.SourceName, title = entry.Title, platform = entry.Platform.ToUpperInvariant(),
                        titleId = entry.TitleId.Length > 0 ? entry.TitleId : string.Join(", ", entry.Artifacts.Select(artifact => artifact.TitleId).Where(id => id.Length > 0).Distinct().Take(2)),
                        cover = LibraryArt(entry.CoverUrl, entry.CoverFallbackUrl) }).ToArray();
                return (200, new { items = matches });
            }

            if (path.EndsWith("/link", StringComparison.Ordinal))
            {
                if (fields.Length != 3 || fields.Select(field => field.Name).Distinct().Count() != 3 ||
                    fields.Any(field => field.Name is not ("id" or "entryId" or "sourceId")))
                    return (400, new { error = "Choose one catalogue game to link." });
                var id = Text("id", 100); var entryId = Text("entryId", 512); var sourceId = Text("sourceId", 100);
                var item = model.LibraryView.Cast<PS5CatalogBrowser.Core.LocalLibraryEntry>().SingleOrDefault(entry => entry.Id == id);
                if (item is null) return (409, new { error = "This library item is no longer available." });
                var catalogueEntry = model.Entries.SingleOrDefault(entry => entry.Id == entryId && entry.SourceId == sourceId);
                if (catalogueEntry is null) return (409, new { error = "That catalogue result is no longer available. Search again." });
                model.RelinkLibraryEntry(item, catalogueEntry);
                return (200, new { saved = true });
            }

            return (404, new { error = "Relink operation unavailable." });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return (400, new { error = ex.Message }); }
    }
}
