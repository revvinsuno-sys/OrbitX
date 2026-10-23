using System.Security.Cryptography;
using System.Text.Json;

namespace PS5CatalogBrowser.Core;

public sealed record OrbitManagedKey(string Fingerprint, string Release, string BundleSha256, string Origin)
{
    public string FilePath { get; init; } = "";
    public string Description => $"Orbit key ID 1 · {(Release.Length > 0 ? Release : "Manual import")} · SHA-256 {Fingerprint[..12]}";
}

/// <summary>Content-addressed decoding keys. Existing key files are never overwritten.</summary>
public sealed class OrbitKeyStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public List<OrbitManagedKey> Load()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        ImageFileSystem.EnsureNoLinks(DirectoryPath);
        var result = new List<OrbitManagedKey>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").Take(256))
        {
            try
            {
                ImageFileSystem.EnsureNoLinks(path);
                if (new FileInfo(path).Length > 8192) continue;
                var key = JsonSerializer.Deserialize<OrbitManagedKey>(File.ReadAllBytes(path));
                if (key is null || key.Release is null || key.BundleSha256 is null || key.Origin is null || !ValidFingerprint(key.Fingerprint) || Path.GetFileNameWithoutExtension(path) != key.Fingerprint) continue;
                var keyPath = Path.Combine(DirectoryPath, key.Fingerprint + ".bin");
                ValidateFile(keyPath, key.Fingerprint);
                result.Add(key with { FilePath = keyPath });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { /* Invalid records are not offered for selection. */ }
        }
        return result.OrderBy(k => k.Description, StringComparer.Ordinal).ToList();
    }

    public async Task<OrbitManagedKey> ImportAsync(string filePath, CancellationToken token = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is < 1 or > 4096) throw new InvalidDataException("Decoding key file exceeds the supported size limit.");
        var material = new byte[(int)stream.Length];
        byte[]? key = null;
        try
        {
            await stream.ReadExactlyAsync(material, token);
            key = LocalOrbitCatalog.ParseKey(material);
            return await SaveAsync(key, "", "", "Manual import", token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    internal async Task<OrbitManagedKey> SaveAsync(byte[] key, string release, string bundleSha256, string origin, CancellationToken token)
    {
        if (key.Length != 32) throw new InvalidDataException("Orbit decoding requires a 32-byte key.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        ImageFileSystem.EnsureNoLinks(DirectoryPath); Directory.CreateDirectory(DirectoryPath);
        var keyPath = Path.Combine(DirectoryPath, fingerprint + ".bin");
        var metadataPath = Path.Combine(DirectoryPath, fingerprint + ".json");
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (!File.Exists(keyPath))
            {
                await File.WriteAllBytesAsync(temporary, key, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, keyPath); // Never overwrite a key, including a corrupted existing file.
            }
            ValidateFile(keyPath, fingerprint);
            var existing = Load().FirstOrDefault(k => k.Fingerprint == fingerprint);
            if (release.Length == 0 && existing is not null) return existing;
            var record = new OrbitManagedKey(fingerprint, release, bundleSha256, origin) { FilePath = keyPath };
            ImageFileSystem.EnsureNoLinks(metadataPath);
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(record), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, metadataPath, true);
            return record;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidFingerprint(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateFile(string path, string fingerprint)
    {
        ImageFileSystem.EnsureNoLinks(path);
        if (new FileInfo(path).Length != 32) throw new InvalidDataException("Stored Orbit key is damaged; import a separate valid key.");
        var key = File.ReadAllBytes(path);
        try
        {
            if (Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant() != fingerprint)
                throw new InvalidDataException("Stored Orbit key fingerprint does not match. The file was not replaced.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
