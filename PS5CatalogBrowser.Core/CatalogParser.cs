using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public static class CatalogParser
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static (string Format, List<CatalogEntry> Entries) Parse(CatalogSource source, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes) throw new InvalidDataException("Catalogue exceeds 8 MiB.");
        using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        string format;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array) { format = "OptiStore"; items = root; }
        else if (root.ValueKind == JsonValueKind.Object && Array(root, "apps", out items)) format = "PS5 Homebrew";
        else if (root.ValueKind == JsonValueKind.Object && Array(root, "entries", out items)) format = "PS5 Library";
        else if (root.ValueKind == JsonValueKind.Object && Array(root, "payloads", out items)) format = "evoX Payloads";
        else if (root.ValueKind == JsonValueKind.Object &&
                 ((root.TryGetProperty("packages", out items) && items.ValueKind is JsonValueKind.Array or JsonValueKind.Object) ||
                  (root.TryGetProperty("DATA", out items) && items.ValueKind == JsonValueKind.Object))) format = "OptiStore";
        else throw new InvalidDataException("Unsupported catalogue format: expected apps, entries, payloads, packages or DATA, or an OptiStore array.");
        var result = new List<CatalogEntry>();
        var index = 0;
        var identities = new Dictionary<string, int>(StringComparer.Ordinal);
        var records = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Select(x => (Key: "", Item: x)) :
            items.EnumerateObject().Select(x => (Key: x.Name, Item: x.Value));
        foreach (var (key, item) in records)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (format == "OptiStore")
            {
                var optiEntry = ParseOptiStore(item, key, source, identities);
                if (optiEntry is not null) result.Add(optiEntry);
                if (++index > 10000) throw new InvalidDataException("Catalogue has too many entries.");
                continue;
            }
            var title = Get(item, format == "PS5 Homebrew" ? "name" : format == "evoX Payloads" ? "name" : "title");
            if (string.IsNullOrWhiteSpace(title)) title = "Untitled item";
            var nativeId = Get(item, "id", "titleid", "title_id");
            var identity = nativeId.Length > 0 ? nativeId : item.GetRawText();
            identities.TryGetValue(identity, out var occurrence);
            identities[identity] = occurrence + 1;
            var entry = new CatalogEntry
            {
                Id = source.Id + ":" + Hash(identity + ":" + occurrence), CatalogFormat = format,
                SourceId = source.Id, SourceName = source.Name, Title = title,
                TitleId = Get(item, "title_id", "titleid"), Description = Get(item, "description"),
                Version = Get(item, "version"), Author = Get(item, "author"),
                Kind = Get(item, "kind", "category"), Platform = Get(item, "platform") is { Length: > 0 } p ? p : "PS5",
                Format = Get(item, "format"), CoverUrl = Get(item, "cover", "icon_url"), Size = Number(item, "size"), SizeLabel = Get(item, "size")
            };
            if (format == "PS5 Homebrew" || format == "evoX Payloads")
            {
                var url = Get(item, format == "PS5 Homebrew" ? "artifact_url" : "url");
                if (url.Length > 0) entry.Artifacts.Add(new CatalogArtifact
                {
                    Id = entry.Id + ":0", Url = url,
                    Name = Get(item, "filename") is { Length: > 0 } n ? n : Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.LocalPath : url),
                    Format = entry.Format, Sha256 = ValidSha(Get(item, format == "PS5 Homebrew" ? "sha256" : "checksum"))
                });
                if (format == "evoX Payloads") entry.Kind = "Console payload: " + entry.Kind;
            }
            else
            {
                ParseFiles(item, entry, "");
                if (Array(item, "source_sets", out var sets))
                {
                    var setIndex = 0;
                    foreach (var set in sets.EnumerateArray())
                    {
                        if (set.ValueKind != JsonValueKind.Object || !Array(set, "files", out var mirrorFiles)) continue;
                        var label = Get(set, "host", "name", "id") is { Length: > 0 } n ? n : $"Mirror {++setIndex}";
                        var fileIndex = 0;
                        foreach (var mirror in mirrorFiles.EnumerateArray())
                        {
                            if (mirror.ValueKind != JsonValueKind.Object) continue;
                            var mirrorUrl = Get(mirror, "url", "download_url");
                            if (mirrorUrl.Length == 0) continue;
                            if (fileIndex < entry.Artifacts.Count)
                            {
                                var artifact = entry.Artifacts[fileIndex];
                                if (mirrorUrl != artifact.Url && !artifact.Mirrors.Contains(mirrorUrl)) artifact.Mirrors.Add(mirrorUrl);
                            }
                            else AddFile(mirror, entry, label);
                            fileIndex++;
                        }
                    }
                }
            }
            result.Add(entry);
            index++;
            if (index > 10000) throw new InvalidDataException("Catalogue has too many entries.");
        }
        return (format, result);
    }

    private static void ParseFiles(JsonElement owner, CatalogEntry entry, string set)
    {
        if (!Array(owner, "files", out var files)) return;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            var url = Get(file, "url", "download_url");
            if (url.Length == 0) continue;
            var artifact = AddFile(file, entry, set);
            if (Array(file, "mirrors", out var mirrors))
                foreach (var mirror in mirrors.EnumerateArray())
                {
                    var value = mirror.ValueKind == JsonValueKind.String ? mirror.GetString() ?? "" : mirror.ValueKind == JsonValueKind.Object ? Get(mirror, "url") : "";
                    if (value.Length > 0) artifact.Mirrors.Add(value);
                }
        }
    }

    private static CatalogArtifact AddFile(JsonElement file, CatalogEntry entry, string set)
    {
        var url = Get(file, "url", "download_url");
        var artifact = new CatalogArtifact
        {
            Id = entry.Id + ":" + entry.Artifacts.Count, Url = url,
            Name = Get(file, "name", "filename") is { Length: > 0 } n ? n : Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.LocalPath : url),
            Format = Get(file, "format"), Size = Number(file, "size"),
            Sha256 = ValidSha(Get(file, "sha256", "checksum")), SourceSet = set
        };
        entry.Artifacts.Add(artifact);
        return artifact;
    }

    private static CatalogEntry? ParseOptiStore(JsonElement item, string key, CatalogSource source, Dictionary<string, int> identities)
    {
        var title = Get(item, "title", "name");
        var titleId = Get(item, "titleId", "title_id", "titleid");
        if (title.Length == 0 && titleId.Length == 0) return null;
        var identity = Get(item, "id") is { Length: > 0 } id ? id : key.Length > 0 ? key :
            titleId + ":" + Get(item, "category", "kind") + ":" + Get(item, "version") + ":" + title;
        identities.TryGetValue(identity, out var occurrence);
        identities[identity] = occurrence + 1;
        var description = Get(item, "description");
        var region = Get(item, "region");
        if (region.Length == 0)
        {
            var match = Regex.Match(description, @"(?:^|\n)\s*Region\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (match.Success) region = match.Groups[1].Value.Trim();
        }
        var entry = new CatalogEntry
        {
            Id = source.Id + ":" + Hash(identity + ":" + occurrence), CatalogFormat = "OptiStore", SourceId = source.Id,
            SourceName = source.Name, Title = title.Length > 0 ? title : titleId,
            TitleId = titleId, Description = description, Version = Get(item, "version"),
            Kind = Get(item, "category", "kind"), Platform = Get(item, "platform") is { Length: > 0 } p ? p : "PS5",
            Format = Get(item, "formatLabel", "format"), CoverUrl = Get(item, "posterUrl", "poster_url", "cover"),
            Size = Number(item, "sizeBytes") ?? Number(item, "size"),
            SizeLabel = Number(item, "sizeBytes") is long bytes ? $"{bytes / 1073741824.0:N2} GiB" : Get(item, "size"), Region = region
        };
        if (entry.Format.Length == 0 && Array(item, "fileFormat", out var formats))
            entry.Format = string.Join(", ", formats.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        ParseOptiLinks(item, "downloadLinks", entry, key);
        ParseOptiLinks(item, "files", entry, key);
        return entry;
    }

    private static void ParseOptiLinks(JsonElement item, string property, CatalogEntry entry, string mapKey)
    {
        if (!Array(item, property, out var links))
        {
            if (property == "downloadLinks" && Uri.TryCreate(mapKey, UriKind.Absolute, out var mapUrl))
                AddOptiLink(entry, mapUrl.AbsoluteUri, "Download", "", "", "", "", null, "", "");
            return;
        }
        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind == JsonValueKind.String)
            {
                AddOptiLink(entry, link.GetString() ?? "", "Download", "", "", "", "", null, "", "");
                continue;
            }
            if (link.ValueKind != JsonValueKind.Object) continue;
            AddOptiLink(entry, Get(link, "url", "link", "download_url"), Get(link, "name", "label", "fileName", "filename"),
                Get(link, "group"), Get(link, "version"), Get(link, "region"), Get(link, "mirror", "host"),
                Number(link, "sizeBytes") ?? Number(link, "size"), Get(link, "fileName", "filename"), Get(link, "editionId"));
        }
    }

    private static void AddOptiLink(CatalogEntry entry, string url, string name, string group, string version, string region, string host,
        long? size, string fileName, string editionId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        var label = name.Length > 0 ? name : fileName.Length > 0 ? fileName : "Download";
        if (group.Length == 0)
        {
            var type = Regex.Match(label, @"\b(DLC|Update|Patch|Base|Game|Dump|FPKG)\b", RegexOptions.IgnoreCase);
            if (type.Success) group = type.Value;
        }
        var part = Regex.Match(label + " " + fileName, @"(?:\[\s*(\d+)\s*/\s*(\d+)\b|\b(?:part|pt)\.?\s*(\d+)(?:\s*/\s*(\d+))?)", RegexOptions.IgnoreCase);
        int? partNumber = null, partCount = null;
        if (part.Success)
        {
            if (int.TryParse(part.Groups[1].Success ? part.Groups[1].Value : part.Groups[3].Value, out var n)) partNumber = n;
            if (int.TryParse(part.Groups[2].Success ? part.Groups[2].Value : part.Groups[4].Value, out var count)) partCount = count;
        }
        entry.Artifacts.Add(new CatalogArtifact
        {
            Id = entry.Id + ":" + entry.Artifacts.Count, Url = url, Name = label, FileName = fileName,
            Host = uri.Host, Version = version, Region = region.Length > 0 ? region : entry.Region, EditionId = editionId,
            SourceSet = string.Join(" · ", new[] { group, version, region, host, editionId }.Where(x => x.Length > 0)),
            Format = entry.Format, Size = size, PartNumber = partNumber, PartCount = partCount
        });
    }

    private static string Get(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
            if (obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return value.ToString();
        return "";
    }
    private static long? Number(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.Number or JsonValueKind.String && long.TryParse(value.ToString(), out var n) ? n : null;
    private static bool Array(JsonElement obj, string name, out JsonElement value) => obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;
    private static string ValidSha(string value) => Regex.IsMatch(value, "^[a-fA-F0-9]{64}$") ? value.ToLowerInvariant() : "";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..20];
}
