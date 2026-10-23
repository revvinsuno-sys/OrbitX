using System.Security.Cryptography;

namespace PS5CatalogBrowser.Core;

/// <summary>Authenticates Orbit version 1 feed envelopes and returns their plaintext bytes.</summary>
public static class OrbitCatalogDecoder
{
    public const int MaximumPlaintextLength = 8 * 1024 * 1024;
    private const int HeaderLength = 24;
    private const int TagLength = 16;
    private const int Overhead = HeaderLength + TagLength;

    /// <summary>Decodes a feed (purpose 1) using a caller-supplied AES-256 key.</summary>
    /// <remarks>The caller must separately validate UTF-8 JSON and catalogue schema.
    /// The public symmetric key does not establish trusted publisher identity.</remarks>
    /// <exception cref="InvalidDataException">Envelope length or header is unsupported.</exception>
    /// <exception cref="ArgumentException">The key is not 32 bytes.</exception>
    /// <exception cref="CryptographicException">Authentication fails.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key)
    {
        if (envelope.Length <= Overhead || envelope.Length > MaximumPlaintextLength + Overhead)
            throw new InvalidDataException("Orbit envelope must contain 1 byte to 8 MiB of plaintext plus 40 bytes of overhead.");
        if (!envelope[..8].SequenceEqual("ORBITENC"u8))
            throw new InvalidDataException("Invalid Orbit envelope magic.");
        if (envelope[8] != 1)
            throw new InvalidDataException("Unsupported Orbit envelope encoding version.");
        if (envelope[9] != 1)
            throw new InvalidDataException("Unsupported Orbit envelope key identifier.");
        if (envelope[10] != 1)
            throw new InvalidDataException("Orbit envelope must have catalogue feed purpose 1.");
        if (envelope[11] != 0)
            throw new InvalidDataException("Orbit envelope reserved byte must be zero.");
        if (key.Length != 32)
            throw new ArgumentException("Orbit decoding requires a 32-byte AES-256 key.", nameof(key));

        using var aes = new AesGcm(key, TagLength);
        var plaintext = new byte[envelope.Length - Overhead];
        try
        {
            aes.Decrypt(envelope.Slice(12, 12), envelope.Slice(HeaderLength, plaintext.Length),
                envelope[^TagLength..], plaintext, envelope[..HeaderLength]);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }
}
