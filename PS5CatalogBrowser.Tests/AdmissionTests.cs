using PS5CatalogBrowser.Core;

internal static class AdmissionTests
{
    public static async Task RunAsync()
    {
        static void Check(bool condition) { if (!condition) throw new Exception("Admission assertion failed."); }
        var entry = new CatalogEntry { SourceId = "source", Id = "entry", Version = "1" };
        var artifact = new CatalogArtifact { Id = "artifact", Url = "https://fixture.test/original", FileName = "same.pkg", Mirrors = ["https://mirror.test/file"] };
        var identity = DownloadAdmission.Capture(entry, artifact);
        var admission = new DownloadAdmission();
        var records = new List<DownloadJob>();
        DownloadJob Admit(string key) => admission.Admit([key], () => [new() { Title = "fixture" }], jobs => records.AddRange(jobs), _ => { })[0];
        void Rejected(Action action)
        {
            try { action(); } catch (IOException ex) when (ex.Message.StartsWith("This item already has an active download.")) { return; }
            throw new Exception("Expected duplicate rejection.");
        }
        // Competing clicks/callbacks must produce exactly one persisted owner.
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try { Admit(identity); Interlocked.Increment(ref accepted); } catch (IOException) { }
        })));
        Check(accepted == 1 && records.Count == 1);
        var owner = records.Single();
        foreach (var state in new[] { "Queued", "Downloading", "Paused", "Interrupted", "Checking integrity", "Cancelled" })
        {
            owner.State = state;
            Rejected(() => Admit(identity)); // State alone cannot establish writer termination.
        }
        Check(admission.Owns(owner));
        admission.Release(new DownloadJob { SelectionIdentity = identity }); // A different job cannot release this owner.
        Rejected(() => Admit(identity));
        admission.Release(owner);
        var retry = admission.Admit([owner.SelectionIdentity], () => [owner], _ => { }, _ => { })[0];
        Check(ReferenceEquals(owner, retry)); admission.Release(retry);

        // Identity does not depend on chosen mirror or filename, and distinct selections remain independent.
        artifact.FileName = "renamed.pkg";
        Check(identity == DownloadAdmission.Capture(entry, artifact));
        artifact.Id = "other-opti-link"; Check(identity != DownloadAdmission.Capture(entry, artifact));
        artifact.Id = "artifact"; artifact.Url = "https://fixture.test/replaced";
        Check(identity != DownloadAdmission.Capture(entry, artifact));
        artifact.Url = "https://fixture.test/original"; entry.Version = "2";
        Check(identity != DownloadAdmission.Capture(entry, artifact)); entry.Version = "1";
        artifact.PartNumber = 2; Check(identity != DownloadAdmission.Capture(entry, artifact)); artifact.PartNumber = null;
        entry.SourceId = "other-source"; Check(identity != DownloadAdmission.Capture(entry, artifact)); entry.SourceId = "source";
        Check(DownloadAdmission.Capture(new CatalogEntry(), artifact) == "");
        Admit(""); Admit(""); // Legacy/unscoped records are not invented equivalences.

        var root = Path.Combine(Path.GetTempPath(), "PS5Admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            var settings = new AppSettings { Downloads = [owner] };
            owner.Method = "Browser"; owner.State = "Paused";
            store.Save(settings);
            var restored = store.Load().Downloads.Single();
            Check(restored.State == "Interrupted" && restored.SelectionIdentity == identity);
            var restarted = new DownloadAdmission();
            restarted.Admit([identity], () => [new()], _ => { }, _ => { });
            Check(!restarted.Owns(restored));

            var rolledBack = false;
            try
            {
                admission.Admit([identity], () => [new()], _ => throw new IOException("Injected persistence failure"), _ => rolledBack = true);
                throw new Exception("Expected persistence failure.");
            }
            catch (IOException ex) when (ex.Message == "Injected persistence failure") { }
            Check(rolledBack);
            var afterFailure = Admit(identity);
            var created = false;
            Rejected(() => admission.Admit(["independent", identity], () => { created = true; return [new(), new()]; }, _ => { }, _ => { }));
            Check(!created);
            Rejected(() => admission.Admit(["part", "part"], () => { created = true; return [new(), new()]; }, _ => { }, _ => { }));
            Check(!created);
            admission.Release(afterFailure);
            var distinct = admission.Admit(["part1", "part2"], () => [new(), new()], _ => { }, _ => { });
            Check(distinct.All(admission.Owns)); foreach (var job in distinct) admission.Release(job);
        }
        finally { Directory.Delete(root, true); }
    }
}
