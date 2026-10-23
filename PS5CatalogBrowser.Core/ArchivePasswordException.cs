namespace PS5CatalogBrowser.Core;

public sealed class ArchivePasswordException() : IOException("Password protected")
{
    public static bool IsPasswordFailure(Exception error) => error is ArchivePasswordException ||
        error.GetType().Name.Contains("CryptographicException", StringComparison.Ordinal) ||
        error.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
        error.InnerException is { } inner && IsPasswordFailure(inner);
}
