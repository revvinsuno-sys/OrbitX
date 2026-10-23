using System.Text.Json;

namespace PS5CatalogBrowser.Core;

public sealed class InstallationJournal(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const string InterruptedGuidance = "Previous installation was interrupted. The original image was preserved. Retry to start a fresh installation attempt; incomplete staging is not resumed.";
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
                job.Error = InterruptedGuidance;
                Save(job);
            }
            else if (job.State == InstallationState.NeedsAttention && job.Error.StartsWith("Previous installation was interrupted.", StringComparison.Ordinal) &&
                !job.Error.Contains("Retry to start a fresh installation attempt", StringComparison.Ordinal))
            {
                job.Error = InterruptedGuidance;
                Save(job);
            }
        return jobs;
    }
}
