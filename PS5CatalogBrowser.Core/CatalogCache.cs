using System.Security.Cryptography;
using System.Text;

namespace PS5CatalogBrowser.Core;

public sealed class CatalogCache(string? directory = null)
{
    public string DirectoryPath { get; } = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS5CatalogBrowser", "catalogues");

    public string FilePath(CatalogSource source)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id + "\n" + source.Url))).ToLowerInvariant();
        return Path.Combine(DirectoryPath, key + ".json");
    }

    public async Task SaveAsync(CatalogSource source, byte[] data, CancellationToken token)
    {
        if (source.SourceType != CatalogSourceType.HttpJson) throw new InvalidOperationException("Orbit catalogue caching is disabled.");
        if (data.Length > CatalogParser.MaximumBytes) throw new InvalidDataException("Catalogue exceeds 8 MiB.");
        Directory.CreateDirectory(DirectoryPath);
        var path = FilePath(source);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, token);
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<(string Format, List<CatalogEntry> Entries)?> LoadAsync(CatalogSource source, CancellationToken token)
    {
        if (source.SourceType != CatalogSourceType.HttpJson) throw new InvalidOperationException("Orbit catalogue caching is disabled.");
        var path = FilePath(source);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > CatalogParser.MaximumBytes) throw new InvalidDataException("Cached catalogue exceeds 8 MiB.");
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + count > CatalogParser.MaximumBytes) throw new InvalidDataException("Cached catalogue exceeds 8 MiB.");
            buffer.Write(block, 0, count);
        }
        var data = buffer.ToArray();
        return await Task.Run(() => CatalogParser.Parse(source, data), token);
    }
}
