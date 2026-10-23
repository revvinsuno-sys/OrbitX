using System.Text.Json;

namespace PS5CatalogBrowser.App;

internal static class OrbitFrontendSecurity
{
    public const string Host = "orbit.ps5catalog.invalid";
    public const string Origin = "https://" + Host;
    public const string Document = Origin + "/index.html";
    public static bool IsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IdnHost == Host && uri.Port == 443 && uri.UserInfo.Length == 0;
    public static bool IsDocument(string value) => IsOrigin(value) && new Uri(value) is { AbsolutePath: "/index.html", Query.Length: 0, Fragment.Length: 0 };
    public static bool TryCommand(string source, string currentDocument, string json, out string command, out string? page)
    {
        command = ""; page = null;
        if (!IsDocument(source) || !IsDocument(currentDocument) || json.Length > 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("command", out var value) || value.ValueKind != JsonValueKind.String) return false;
            command = value.GetString()!;
            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Distinct().Count() != properties.Length) return false;
            if (command == "navigate")
            {
                if (properties.Length != 3 || !root.TryGetProperty("page", out var target) || target.ValueKind != JsonValueKind.String) return false;
                page = target.GetString();
                return page is "Settings" or "Downloads" or "My Library" or "Classic Windows";
            }
            return properties.Length == 2 && command is "fullscreen" or "exitFullscreen" or "toolbar" or "ready" or "error";
        }
        catch (JsonException) { return false; }
    }
}
