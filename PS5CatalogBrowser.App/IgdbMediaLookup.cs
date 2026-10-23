using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.App;

internal sealed class IgdbMediaLookup : IDisposable
{
    private const string IgdbBaseUrl = "https://api.igdb.com/v4";
    private const string TwitchTokenUrl = "https://id.twitch.tv/oauth2/token";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, (DateTimeOffset Expires, IgdbMediaResult Result)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset tokenExpires;
    private DateTimeOffset nextRequestAt;

    internal async Task<IgdbMediaResult> LookupAsync(string title, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200) return new(false, [], [], [], "Enter a game title to look up media.");
        lock (cache)
            if (cache.TryGetValue(title, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Result;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var requestCancellation = timeout.Token;
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return new(false, [], [], [], "Add IGDB credentials in Settings to load game media.");
            await gate.WaitAsync(requestCancellation).ConfigureAwait(false);
            try
            {
                if (accessToken is null || tokenExpires <= DateTimeOffset.UtcNow.AddMinutes(1))
                    await RefreshTokenAsync(clientId, clientSecret, requestCancellation).ConfigureAwait(false);
                var rows = await PostAsync("search", SearchQuery(title), clientId, requestCancellation).ConfigureAwait(false);
                var exact = rows.Where(row => string.Equals(Normalize(Name(row)), Normalize(title), StringComparison.Ordinal))
                    .Select(Id).Where(id => id > 0).Distinct().Take(5).ToArray();
                if (exact.Length == 0)
                {
                    var empty = new IgdbMediaResult(true, [], [], [], null);
                    Cache(title, empty);
                    return empty;
                }
                var detailRows = await PostAsync("games", DetailQuery(exact), clientId, requestCancellation).ConfigureAwait(false);
                var matching = detailRows.Where(row => string.Equals(Normalize(Name(row)), Normalize(title), StringComparison.Ordinal)).Take(1).ToArray();
                var result = matching.Length == 0 ? new IgdbMediaResult(true, [], [], [], null) : MapMedia(matching[0]);
                Cache(title, result);
                return result;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(false, [], [], [], "IGDB media could not be loaded. Try again in a moment.");
        }
    }

    private async Task RefreshTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials",
        });
        using var response = await Http.PostAsync(TwitchTokenUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        accessToken = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidDataException("Twitch token response was empty.");
        var seconds = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var value) ? value : 3600;
        tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 60, 86_400));
    }

    private async Task<JsonElement[]> PostAsync(string endpoint, string query, string clientId, CancellationToken cancellationToken)
    {
        var delay = nextRequestAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{IgdbBaseUrl}/{endpoint}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Client-ID", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(query, Encoding.UTF8, "text/plain");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
    }

    private static string SearchQuery(string title) => $"search \"{Escape(title)}\";\nfields game,name;\nwhere game != null;\nlimit 20;";
    private static string DetailQuery(int[] ids) => $"fields id,name,artworks.image_id,artworks.width,artworks.height,screenshots.image_id,screenshots.width,screenshots.height,videos.video_id,videos.name;\nwhere id = ({string.Join(',', ids)});\nlimit {ids.Length};";
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string Name(JsonElement row) => row.TryGetProperty("name", out var value) ? value.GetString() ?? "" : "";
    private static int Id(JsonElement row)
    {
        if (!row.TryGetProperty("game", out var game)) return 0;
        if (game.ValueKind == JsonValueKind.Number && game.TryGetInt32(out var scalar)) return scalar;
        return game.ValueKind == JsonValueKind.Object && game.TryGetProperty("id", out var id) && id.TryGetInt32(out var result) ? result : 0;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var stripped = new string(decomposed.Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(stripped.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    }

    private static IgdbMediaResult MapMedia(JsonElement game)
    {
        var artworks = new List<IgdbMediaItem>();
        var screenshots = new List<IgdbMediaItem>();
        AddImages("artworks", "t_screenshot_big", artworks);
        AddImages("screenshots", "t_screenshot_big", screenshots);
        var videos = new List<IgdbMediaItem>();
        if (game.TryGetProperty("videos", out var videoRows) && videoRows.ValueKind == JsonValueKind.Array)
        {
            foreach (var video in videoRows.EnumerateArray().Take(2))
            {
                var id = video.TryGetProperty("video_id", out var videoId) ? videoId.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,20}$", RegexOptions.CultureInvariant)) continue;
                var name = video.TryGetProperty("name", out var videoName) ? videoName.GetString() : null;
                videos.Add(new("video", string.IsNullOrWhiteSpace(name) ? "Trailer" : name[..Math.Min(name.Length, 120)], "", "", id, "wide"));
            }
        }
        return new(true, artworks, screenshots, videos, null);

        void AddImages(string property, string imageSize, List<IgdbMediaItem> destination)
        {
            if (!game.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) return;
            foreach (var image in rows.EnumerateArray())
            {
                if (destination.Count == 4) return;
                if (!image.TryGetProperty("image_id", out var imageId)) continue;
                var id = imageId.GetString();
                if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant)) continue;
                var width = image.TryGetProperty("width", out var widthValue) && widthValue.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
                var height = image.TryGetProperty("height", out var heightValue) && heightValue.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
                var tileSize = width > 0 && height > 0 && height > width * 1.2 ? "large" : width > 0 && height > 0 && width < height * 1.2 ? "small" : "wide";
                destination.Add(new("image", property == "artworks" ? "Artwork" : "Screenshot",
                    $"https://images.igdb.com/igdb/image/upload/{imageSize}/{id}.jpg",
                    $"https://images.igdb.com/igdb/image/upload/t_1080p/{id}.jpg", "", tileSize));
                if (destination.Count == 4) return;
            }
        }
    }

    private void Cache(string title, IgdbMediaResult result)
    {
        lock (cache)
        {
            cache[title] = (DateTimeOffset.UtcNow.AddMinutes(20), result);
            foreach (var old in cache.Where(item => item.Value.Expires <= DateTimeOffset.UtcNow).Select(item => item.Key).ToArray()) cache.Remove(old);
        }
    }

    public void Dispose() => gate.Dispose();
}

internal sealed record IgdbMediaItem(string Type, string Title, string ThumbnailUrl, string ImageUrl, string VideoId, string Size);
internal sealed record IgdbMediaResult(bool Available, IReadOnlyList<IgdbMediaItem> Artworks, IReadOnlyList<IgdbMediaItem> Screenshots,
    IReadOnlyList<IgdbMediaItem> Trailers, string? Message);
