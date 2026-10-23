using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class LocalOrbitCatalogTests
{
    public static readonly byte[] Key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
    public static byte[] Feed(int schema = 1, bool reverse = false, int generation = 6, int revision = 27, bool remove = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = schema, feedVersion = generation, revision,
        releases = (reverse ? new[] { "c", "b", "a" } : new[] { "a", "b", "c" }).Where(id => !remove || id != "b").Select(id => new
        {
            id, gameId = "game", platform = id == "c" ? "ps4" : "ps5", title = "Synthetic Orbit game",
            titleId = id == "c" ? "CUSA00001" : "PPSA00001", filename = "synthetic.part01.rar",
            url = "https://provider.invalid/" + id, browserUrl = "https://provider.invalid/page/" + id,
            cover = "http://127.0.0.1:1/cover", provider = "Synthetic provider", sourceId = "provider",
            version = id == "a" ? "1.0" : "2.0", region = id == "a" ? "EUR" : "USA", format = "exFAT",
            compression = "rar", sizeBytes = 1234, archivePassword = "synthetic-password"
        })
    });
    public static byte[] Seal(byte[] plaintext)
    {
        var envelope = new byte[plaintext.Length + 40];
        "ORBITENC"u8.CopyTo(envelope); envelope[8] = envelope[9] = envelope[10] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(12, 12));
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(envelope.AsSpan(12, 12), plaintext, envelope.AsSpan(24, plaintext.Length), envelope.AsSpan(24 + plaintext.Length, 16), envelope.AsSpan(0, 24));
        return envelope;
    }
    public static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5OrbitCore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new CatalogSource { Id = "local", Name = "Synthetic", SourceType = CatalogSourceType.OrbitLocal,
                EncryptedFilePath = Path.Combine(root, "catalog.enc"), KeyFilePath = Path.Combine(root, "key.bin") };
            File.WriteAllBytes(source.KeyFilePath, Key); File.WriteAllBytes(source.EncryptedFilePath, Seal(Feed()));
            var before = File.ReadAllBytes(source.EncryptedFilePath);
            var result = await LocalOrbitCatalog.LoadAsync(source);
            Check(result.Entries.Count == 2 && result.Entries.Sum(e => e.Artifacts.Count) == 3, "load all alternatives/platforms");
            Check(before.SequenceEqual(File.ReadAllBytes(source.EncryptedFilePath)), "source untouched");
            Check(result.Entries.All(e => e.Kind == "game" && e.DownloadsEnabled && !e.DownloadAllEnabled && e.ThumbnailUrl == e.CoverUrl && e.CoverUrl.Length > 0), "individual releases and catalogue artwork");
            Check(result.Entries.SelectMany(e => e.Artifacts).All(a => a.PartNumber is null && a.PartCount is null), "no multipart inference");
            File.WriteAllBytes(source.EncryptedFilePath, Seal(Feed(reverse: true)));
            Check(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(await LocalOrbitCatalog.LoadAsync(source)), "stable reordered load");
            Check(LocalOrbitCatalog.ParseKey(Encoding.ASCII.GetBytes(Convert.ToHexString(Key) + "\n")).SequenceEqual(Key), "hex key");
            var header = "/* synthetic */\n#ifndef ORBIT_CATALOG_KEY_H\n#define ORBIT_CATALOG_KEY_H\nstatic const unsigned char catalog_key[32] = {" + string.Join(", ", Key.Select(x => "0x" + x.ToString("X2"))) + "};\n#endif\n";
            Check(LocalOrbitCatalog.ParseKey(Encoding.ASCII.GetBytes(header)).SequenceEqual(Key), "literal header");
            foreach (var bad in new[] { header + "void run();", header.Replace("0x01", "(1+0)"), header.Replace("0x01", "0x01, 0x01"), "bad key" })
                try { LocalOrbitCatalog.ParseKey(Encoding.ASCII.GetBytes(bad)); throw new Exception("accepted unsupported key"); } catch (InvalidDataException) { }
            async Task Fails(string expected)
            {
                try { await LocalOrbitCatalog.LoadAsync(source); throw new Exception("accepted invalid source"); }
                catch (InvalidDataException ex) { Check(ex.Message.Contains(expected) && !ex.Message.Contains("provider.invalid"), "redacted error: " + expected); }
            }
            File.WriteAllBytes(source.KeyFilePath, new byte[32]); await Fails("authentication failed");
            File.WriteAllText(source.KeyFilePath, "bad"); await Fails("Unsupported decoding key");
            File.Delete(source.KeyFilePath); await Fails("key file is missing");
            File.WriteAllBytes(source.KeyFilePath, Key);
            File.WriteAllBytes(source.EncryptedFilePath, [1, 2]); await Fails("Orbit envelope");
            File.WriteAllBytes(source.EncryptedFilePath, Seal(Feed(schema: 2))); await Fails("unsupported schemaVersion");
            File.WriteAllBytes(source.EncryptedFilePath, Seal("{invalid"u8.ToArray())); await Fails("invalid JSON");
            File.Delete(source.EncryptedFilePath); await Fails("catalogue file is missing");
            using (var file = File.Create(source.EncryptedFilePath)) file.SetLength(OrbitCatalogDecoder.MaximumPlaintextLength + 41L);
            await Fails("size limit");
            var store = new SettingsStore(Path.Combine(root, "settings"));
            store.Save(new() { Sources = [source, new() { Name = "HTTP", Url = "https://example.invalid/catalog.json" }] });
            var loaded = store.Load();
            Check(loaded.Sources[0].SourceType == CatalogSourceType.OrbitLocal && loaded.Sources[0].KeyFilePath == source.KeyFilePath && loaded.Sources[1].SourceType == CatalogSourceType.HttpJson, "source persistence");
            var saved = File.ReadAllText(store.FilePath);
            Check(!saved.Contains("synthetic-password") && !saved.Contains(Convert.ToHexString(Key)) && !saved.Contains("provider.invalid"), "no secrets or catalogue data saved");
            File.WriteAllText(store.FilePath, "{\"Sources\":[{\"Id\":\"old\",\"Name\":\"Old HTTP\",\"Url\":\"https://example.invalid/catalog.json\"}]}");
            Check(store.Load().Sources[0].SourceType == CatalogSourceType.HttpJson, "old settings compatibility");
            Check(!Directory.EnumerateFiles(root, "*.json").Any(), "no plaintext cache");
        }
        finally { Directory.Delete(root, true); }
    }
}
