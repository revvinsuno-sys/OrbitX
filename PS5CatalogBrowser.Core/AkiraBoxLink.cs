namespace PS5CatalogBrowser.Core;

// A page and its generated anchor are untrusted. This only permits a browser navigation;
// native confirmation, frozen selection and file size/hash validation still own admission.
public static class AkiraBoxLink
{
    public const string Instructions = "Please wait and the download will continue automatically when AkiraBox makes the link ready. Complete any verification yourself, then wait without clicking the site's Download button. Matching filenames are accepted automatically; other offers ask for confirmation.";
    private static Uri? Address(string value)
    {
        if (value.Length > 16384) return null;
        try
        {
            var uri = NetworkPolicy.Validate(value);
            return uri.Scheme == "https" && uri.Port == 443 && uri.Host is "akirabox.com" or "www.akirabox.com" or "akirabox.to" or "www.akirabox.to" ? uri : null;
        }
        catch (InvalidOperationException) { return null; }
    }
    public static string? FileId(string value)
    {
        if (Address(value) is not { } uri) return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 3 || parts[0].Length != 0) return null;
        var id = parts[1] == "file" ? parts[2] : parts[2] is "file" or "download" ? parts[1] : "";
        return id.Length is > 0 and <= 128 && id.All(char.IsAsciiLetterOrDigit) ? id : null;
    }
    public static bool Ready(string selectedPage, string currentPage, string candidate, string expectedName)
    {
        var id = FileId(selectedPage);
        if (id is null || FileId(currentPage) != id || string.IsNullOrEmpty(expectedName) || Address(candidate) is not { Fragment.Length: 0 } uri) return false;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 4 || parts[0].Length != 0 || parts[1] != "download" || parts[2].Length == 0) return false;
        // AkiraBox form-encodes this path segment: spaces are '+', while a
        // literal plus is '%2B'. Decode in that order before exact matching.
        var filename = Uri.UnescapeDataString(parts[3].Replace('+', ' '));
        return filename == expectedName && !filename.Contains('/') && !filename.Contains('\\');
    }
}
