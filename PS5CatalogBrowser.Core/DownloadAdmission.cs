using System.Security.Cryptography;
using System.Text.Json;

namespace PS5CatalogBrowser.Core;

// Selected artefact reservations include retained direct partials and unresolved completion intents.
public sealed class DownloadAdmission
{
    public static void EnsureEnabled(CatalogEntry entry)
    {
        if (!entry.DownloadsEnabled) throw new InvalidOperationException("Download integration not yet enabled");
    }
    private readonly object gate = new();
    private readonly Dictionary<string, DownloadJob> owners = new(StringComparer.Ordinal);

    public static string Capture(CatalogEntry entry, CatalogArtifact artifact)
    {
        OrbitDownloadPolicy.ValidateSelection(entry, artifact);
        if (entry.CatalogFormat == "Orbit")
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                entry.SourceId, entry.Id, Release = OrbitReleaseSelection.Capture(entry, artifact) })));
        if (entry.Id.Length == 0 || artifact.Id.Length == 0 || artifact.Url.Length == 0) return "";
        // Entry IDs already include the source scope. Keep SourceId when supplied, too.
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] {
            entry.SourceId, entry.Id, artifact.Id, artifact.Url, entry.Version, artifact.Version,
            artifact.EditionId, artifact.Region, artifact.PartNumber?.ToString() ?? "", artifact.Sha256 })));
    }

    public List<DownloadJob> Admit(IReadOnlyList<string> identities, Func<List<DownloadJob>> create,
        Action<List<DownloadJob>> persist, Action<List<DownloadJob>> rollback)
    {
        lock (gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identity in identities.Where(x => x.Length > 0))
            {
                if (owners.TryGetValue(identity, out var active))
                    throw new IOException("This item already has an active download. Active job: " + active.Title + " (" + active.Id + ").");
                if (!seen.Add(identity)) throw new IOException("This item already has an active download. The batch repeats a selected item.");
            }
            var jobs = create();
            try
            {
                if (jobs.Count != identities.Count) throw new InvalidOperationException("Admission job count mismatch.");
                // A refreshed release may have a different fingerprint while its previous writer still owns the stable ID.
                foreach (var job in jobs.Where(job => job.OrbitRelease is not null))
                    if (owners.Values.Any(active => active.OrbitRelease is not null && active.SourceId == job.SourceId &&
                        active.ArtifactId == job.ArtifactId))
                        throw new IOException("This item already has an active download. The selected Orbit release is still owned.");
                for (var i = 0; i < jobs.Count; i++)
                {
                    jobs[i].SelectionIdentity = identities[i];
                    if (identities[i].Length > 0) owners.Add(identities[i], jobs[i]);
                }
                persist(jobs);
                return jobs;
            }
            catch
            {
                foreach (var job in jobs) Release(job);
                rollback(jobs);
                throw;
            }
        }
    }

    public bool Owns(DownloadJob job)
    {
        lock (gate) return owners.TryGetValue(job.SelectionIdentity, out var owner) && ReferenceEquals(owner, job);
    }

    public void RestoreCompletions(IEnumerable<DownloadJob> jobs)
    {
        lock (gate)
            foreach (var job in jobs.Where(job => DownloadCompletion.Unresolved(job) || HttpResume.Reserves(job)).Where(job => job.SelectionIdentity.Length > 0))
                owners.TryAdd(job.SelectionIdentity, job);
    }

    public void Release(DownloadJob job)
    {
        if (DownloadCompletion.Unresolved(job) || HttpResume.Reserves(job)) return;
        lock (gate)
            if (owners.TryGetValue(job.SelectionIdentity, out var owner) && ReferenceEquals(owner, job))
                owners.Remove(job.SelectionIdentity);
    }
}
