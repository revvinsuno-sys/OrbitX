using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public static class SharpEmuCompatibility
{
    public const string IndexUrl = "https://sharpemu.app/games-index.json";
    public static readonly string[] Statuses = ["nothing", "boots", "menus", "ingame", "playable", "untested"];

    public static string NormalizeId(string value) => Regex.IsMatch(value, "^PPSA-?[0-9]{5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ? value.Replace("-", "").ToUpperInvariant() : "";

    public static Dictionary<string, string> Parse(byte[] data)
    {
        using var json = JsonDocument.Parse(data, new() { MaxDepth = 8 });
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() is < 1 or > 50000)
            throw new InvalidDataException("Invalid SharpEmu compatibility index.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var game in json.RootElement.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("t", out var title) || title.ValueKind != JsonValueKind.String ||
                !game.TryGetProperty("a", out var aliases) || aliases.ValueKind != JsonValueKind.String ||
                !game.TryGetProperty("s", out var statusValue) || statusValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid SharpEmu compatibility entry.");
            var status = statusValue.GetString() ?? "";
            var primary = NormalizeId(title.GetString() ?? "");
            var ids = (aliases.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeId).Append(primary).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (primary.Length == 0 || ids.Length == 0 || !Statuses.Contains(status, StringComparer.Ordinal) || ids.Any(id => result.ContainsKey(id)))
                throw new InvalidDataException("Invalid or duplicate SharpEmu compatibility ID/status.");
            foreach (var id in ids) result.Add(id, status);
        }
        if (result.Count == 0) throw new InvalidDataException("Empty SharpEmu compatibility index.");
        return result;
    }

    public static async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(IndexUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("SharpEmu compatibility index exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("SharpEmu compatibility index exceeds size limit.");
            output.Write(buffer, 0, count);
        }
        return Parse(output.ToArray());
    }
}
