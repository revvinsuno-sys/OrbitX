namespace PS5CatalogBrowser.Core;

public static class NetworkPolicy
{
    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps ||
              uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException("Only HTTPS URLs and local HTTP development endpoints are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Credentials in URLs are not allowed.");
        return uri;
    }
}
