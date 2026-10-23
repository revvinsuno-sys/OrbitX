using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

/// <summary>Local input only. No network, persistent catalogue cache, or bundled decoding material.</summary>
public static class LocalOrbitCatalog
{
    public static void ValidatePaths(string encryptedFilePath, string keyFilePath)
    {
        if (!Path.IsPathFullyQualified(encryptedFilePath) || !Path.GetExtension(encryptedFilePath).Equals(".enc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an absolute path to a local Orbit .enc file.");
        if (!Path.IsPathFullyQualified(keyFilePath)) throw new ArgumentException("Select an absolute path to a local decoding key file.");
    }

    public static Task<OrbitCatalogResult> LoadAsync(CatalogSource source, CancellationToken token = default) => Task.Run(() =>
    {
        ValidatePaths(source.EncryptedFilePath, source.KeyFilePath);
        byte[]? envelope = null;
        try
        {
            envelope = ReadBounded(source.EncryptedFilePath, OrbitCatalogDecoder.MaximumPlaintextLength + 40, "Encrypted catalogue file", token);
            return Decode(source, envelope, token);
        }
        finally { if (envelope is not null) CryptographicOperations.ZeroMemory(envelope); }
    }, token);

    private static byte[] ReadBounded(string path, int maximum, string label, CancellationToken token)
    {
        byte[]? data = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 || stream.Length > maximum) throw new InvalidDataException(label + " is empty or exceeds the supported size limit.");
            data = new byte[(int)stream.Length];
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(data);
            if (stream.ReadByte() != -1) throw new InvalidDataException(label + " changed while loading; retry refresh.");
            return data;
        }
        catch (IOException)
        {
            if (data is not null) CryptographicOperations.ZeroMemory(data);
            throw new InvalidDataException(label + " is missing, unavailable or changed. Check the selected path and retry refresh.");
        }
        catch (UnauthorizedAccessException)
        {
            if (data is not null) CryptographicOperations.ZeroMemory(data);
            throw new InvalidDataException(label + " cannot be read. Check file permissions.");
        }
        catch { if (data is not null) CryptographicOperations.ZeroMemory(data); throw; }
    }

    internal static OrbitCatalogResult Decode(CatalogSource source, byte[] envelope, CancellationToken token)
    {
        byte[]? material = null, key = null, plaintext = null;
        try
        {
            if (!Path.IsPathFullyQualified(source.KeyFilePath)) throw new ArgumentException("Select an absolute path to a local decoding key file.");
            material = ReadBounded(source.KeyFilePath, 4096, "Decoding key file", token);
            key = ParseKey(material);
            token.ThrowIfCancellationRequested();
            try { plaintext = OrbitCatalogDecoder.Decode(envelope, key); }
            catch (CryptographicException) { throw new InvalidDataException("Orbit authentication failed. Check that the key file matches this encrypted catalogue."); }
            token.ThrowIfCancellationRequested();
            var result = OrbitCatalogParser.Parse(source, plaintext);
            foreach (var entry in result.Entries) entry.Kind = "game";
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            foreach (var buffer in new[] { material, key, plaintext })
                if (buffer is not null) CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public static byte[] ParseKey(ReadOnlySpan<byte> material)
    {
        if (material.Length == 32) return material.ToArray();
        if (material.Length is 0 or > 4096 || material.ContainsAnyExceptInRange((byte)0, (byte)127)) throw KeyError();
        var text = Encoding.ASCII.GetString(material).Trim();
        if (text.Length == 64 && text.All(char.IsAsciiHexDigit)) return Convert.FromHexString(text);
        // The inspected published header declares one literal catalog_key[32]. Accept no C expressions or executable code.
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline | RegexOptions.NonBacktracking).Trim();
        var match = Regex.Match(text, @"\A#ifndef\s+ORBIT_CATALOG_KEY_H\s+#define\s+ORBIT_CATALOG_KEY_H\s+static\s+const\s+unsigned\s+char\s+catalog_key\s*\[32\]\s*=\s*\{\s*(?<bytes>0x[0-9a-fA-F]{2}(?:\s*,\s*0x[0-9a-fA-F]{2}){31})\s*\}\s*;\s*#endif\s*\z", RegexOptions.NonBacktracking);
        if (!match.Success) throw KeyError();
        return match.Groups["bytes"].Value.Split(',').Select(x => Convert.ToByte(x.Trim()[2..], 16)).ToArray();
    }

    private static InvalidDataException KeyError() => new("Unsupported decoding key. Select a 32-byte binary key, 64 hexadecimal characters, or the published literal catalog_key.h header.");
}
