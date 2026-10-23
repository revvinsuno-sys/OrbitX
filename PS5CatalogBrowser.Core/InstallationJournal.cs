using System.Text.Json;

namespace PS5CatalogBrowser.Core;

public sealed class InstallationJournal(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid installation job ID.");
        return Path.Combine(directory, id + ".json");
    }
    public void Save(InstallationJob job)
    {
        ImageFileSystem.EnsureNoLinks(directory); Directory.CreateDirectory(directory);
        var path = PathFor(job.Id); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(job, Options));
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
    public List<InstallationJob> Load()
    {
        if (!Directory.Exists(directory)) return [];
        ImageFileSystem.EnsureNoLinks(directory);
        // Keep the latest recorded attempt last after restart, as in the live collection.
        var jobs = Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).Select(path =>
            JsonSerializer.Deserialize<InstallationJob>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Invalid installation journal.")).ToList();
        foreach (var job in jobs)
            if (job.State is not (InstallationState.Completed or InstallationState.CompletedOriginalRetained or InstallationState.Failed or InstallationState.Cancelled or InstallationState.NeedsAttention))
            {
                job.State = InstallationState.NeedsAttention;
                job.Error = "Previous installation was interrupted. Original-image cleanup will never resume automatically. Review the mount and installation details.";
                Save(job);
            }
        return jobs;
    }
}
