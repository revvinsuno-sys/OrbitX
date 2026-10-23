using System.Security.Cryptography;
using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class OrbitCatalogDecoderTests
{
    public static Task RunAsync()
    {
        // Synthetic material only. Each fixture receives a fresh nonce.
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] json = "{\"schemaVersion\":1,\"releases\":[]}"u8.ToArray();
        var valid = Seal(json, key);
        Equal(json, OrbitCatalogDecoder.Decode(valid, key), "JSON round trip");
        using (var document = JsonDocument.Parse(OrbitCatalogDecoder.Decode(valid, key)))
            Check(document.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "JSON boundary");

        byte[] single = [0x61];
        Equal(single, OrbitCatalogDecoder.Decode(Seal(single, key), key), "minimum plaintext");
        byte[] maximum = new byte[OrbitCatalogDecoder.MaximumPlaintextLength];
        maximum[0] = 0x42;
        maximum[^1] = 0x7f;
        Equal(maximum, OrbitCatalogDecoder.Decode(Seal(maximum, key), key), "maximum plaintext");

        foreach (int offset in Enumerable.Range(0, 12))
        {
            var changed = (byte[])valid.Clone();
            changed[offset] ^= 0x80;
            Reject<InvalidDataException>(changed, key, $"header byte {offset}");
        }
        foreach (byte purpose in new byte[] { 0, 2, 3, 255 })
        {
            var changed = (byte[])valid.Clone();
            changed[10] = purpose;
            Reject<InvalidDataException>(changed, key, $"purpose {purpose}");
        }
        foreach (int length in new[] { 0, 16, 24, 31, 33, 64 })
            Reject<ArgumentException>(valid, new byte[length], $"key length {length}");
        for (int length = 0; length < 24; length++)
            Reject<InvalidDataException>(valid[..length], key, $"truncated header {length}");
        Reject<InvalidDataException>(valid[..24], key, "missing ciphertext and tag");
        Reject<InvalidDataException>(Seal([], key), key, "authenticated empty plaintext");
        Reject<InvalidDataException>(new byte[OrbitCatalogDecoder.MaximumPlaintextLength + 41], key, "oversize envelope");

        var wrongKey = (byte[])key.Clone();
        wrongKey[0] ^= 1;
        Reject<AuthenticationTagMismatchException>(valid, wrongKey, "wrong key");
        foreach (int offset in Enumerable.Range(12, 12).Concat(new[] { 24, valid.Length - 1 }))
        {
            var changed = (byte[])valid.Clone();
            changed[offset] ^= 1;
            Reject<AuthenticationTagMismatchException>(changed, key, $"authenticated byte {offset}");
        }
        // These retain valid header values but omit authenticated header bytes during sealing.
        // All must fail, proving the reader requires exactly the complete 24-byte AAD.
        foreach (int aadLength in new[] { 0, 12, 23 })
            Reject<AuthenticationTagMismatchException>(Seal(json, key, aadLength), key, $"incomplete AAD {aadLength}");

        Reject<AuthenticationTagMismatchException>(valid[..^16], key, "missing authentication tag");
        Reject<AuthenticationTagMismatchException>(valid[..^1], key, "truncated encrypted data");
        var truncatedCiphertext = new byte[valid.Length - 1];
        valid.AsSpan(0, 24).CopyTo(truncatedCiphertext);
        valid.AsSpan(25).CopyTo(truncatedCiphertext.AsSpan(24));
        Reject<AuthenticationTagMismatchException>(truncatedCiphertext, key, "truncated ciphertext with intact tag");

        // Authentication is not catalogue validation: O2 must reject malformed JSON itself.
        var malformed = OrbitCatalogDecoder.Decode(Seal("not json"u8.ToArray(), key), key);
        try { using var document = JsonDocument.Parse(malformed); }
        catch (JsonException) { return Task.CompletedTask; }
        throw new Exception("Malformed authenticated JSON was accepted by the JSON parser.");
    }

    private static byte[] Seal(byte[] plaintext, byte[] key, int aadLength = 24)
    {
        var envelope = new byte[40 + plaintext.Length];
        "ORBITENC"u8.CopyTo(envelope);
        envelope[8] = 1;
        envelope[9] = 1;
        envelope[10] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(12, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(envelope.AsSpan(12, 12), plaintext, envelope.AsSpan(24, plaintext.Length),
            envelope.AsSpan(24 + plaintext.Length, 16), envelope.AsSpan(0, aadLength));
        return envelope;
    }

    private static void Reject<T>(byte[] envelope, byte[] key, string name) where T : Exception
    {
        byte[]? result = null;
        try { result = OrbitCatalogDecoder.Decode(envelope, key); }
        catch (T) { Check(result is null, name + " returned plaintext"); return; }
        throw new Exception("Accepted invalid Orbit input: " + name);
    }

    private static void Equal(byte[] expected, byte[] actual, string name) =>
        Check(expected.AsSpan().SequenceEqual(actual), name);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Orbit decoder check failed: " + name);
    }
}
