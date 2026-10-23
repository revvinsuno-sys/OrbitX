using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public static class KytyCompatibility
{
    public const string IndexUrl = "https://kytyps5.github.io/data/compat-index.json";
    public static readonly string[] Statuses = ["doesnt-boot", "logo", "main-menu", "in-game", "untested"];
    public static string NormalizeId(string id) => Regex.IsMatch(id, "^PPSA-?[0-9]{5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ? id.Replace("-", "").ToUpperInvariant() : "";

    public static Dictionary<string, string> Parse(byte[] data)
    {
        using var json = JsonDocument.Parse(data, new() { MaxDepth = 16 });
        if (json.RootElement.GetProperty("version").GetInt32() != 2) throw new InvalidDataException("Unsupported compatibility index version.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var game in json.RootElement.GetProperty("games").EnumerateArray())
        {
            var id = NormalizeId(game.GetProperty("titleId").GetString() ?? "");
            var status = game.GetProperty("overall").GetString() ?? "";
            if (id.Length == 0 || !Statuses.Contains(status) || !result.TryAdd(id, status))
                throw new InvalidDataException("Invalid or duplicate compatibility title ID/status.");
        }
        if (result.Count == 0) throw new InvalidDataException("Empty compatibility index.");
        return result;
    }

    public static async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellation)
        => Parse(await DownloadAsync(IndexUrl, cancellation));

    public static IReadOnlyList<KytyCommunityReport> ParseReports(byte[] data, string titleId)
    {
        using var json = JsonDocument.Parse(data, new() { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.GetProperty("version").GetInt32() != 2 || NormalizeId(root.GetProperty("titleId").GetString() ?? "") != titleId)
            throw new InvalidDataException("Compatibility report identity/version mismatch.");
        var reports = new List<KytyCommunityReport>();
        foreach (var report in root.GetProperty("reports").EnumerateArray())
        {
            if (NormalizeId(report.GetProperty("titleId").GetString() ?? "") != titleId) throw new InvalidDataException("Compatibility report identity mismatch.");
            var status = report.GetProperty("status").GetString() ?? "";
            if (!Statuses.Contains(status)) throw new InvalidDataException("Unknown compatibility report status.");
            string Text(string key)
            {
                var text = report.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                return text[..Math.Min(text.Length, 256)];
            }
            reports.Add(new(status, Text("testedVersion"), Text("os"), Text("testedDate")));
            if (reports.Count > 256) throw new InvalidDataException("Too many compatibility reports.");
        }
        return reports;
    }

    public static async Task<IReadOnlyList<KytyCommunityReport>> LoadReportsAsync(string titleId, CancellationToken cancellation)
    {
        if (NormalizeId(titleId) != titleId || titleId.Length == 0) throw new InvalidDataException("Invalid compatibility title ID.");
        return ParseReports(await DownloadAsync($"https://kytyps5.github.io/data/compat/{titleId}.json", cancellation), titleId);
    }

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        cancellation = timeout.Token;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        const int limit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Compatibility index exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Compatibility index exceeds size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
