using System.Diagnostics;
using PS5CatalogBrowser.Core;

internal static class LibraryDeletionTests
{
    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5LibraryDeletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var settings = new AppSettings { DownloadRoot = Path.Combine(root, "downloads"), Sources = [] };
            var trash = Path.Combine(root, "synthetic-trash"); Directory.CreateDirectory(trash);
            var protectedRoots = new[] { store.DirectoryPath };
            LocalLibraryEntry Add(string name)
            {
                var path = Path.Combine(root, name); File.WriteAllText(path, "fixture bytes");
                var item = new LocalLibraryEntry { Title = name, ContentPath = path };
                settings.Library.Add(item); return item;
            }
            List<LibraryDeletionTarget> Plan(LocalLibraryEntry item) => LibraryDeletion.Prepare(settings, item, protectedRoots, _ => false);
            void Recycle(LibraryDeletionTarget target)
            {
                Check(store.Load().Library.Single(item => item.Id == settings.Library.Single(entry => entry.DeletionTargets is not null).Id).DeletionTargets is not null,
                    "Deletion intent must be durable before recycling");
                var destination = Path.Combine(trash, Guid.NewGuid().ToString("N"));
                if (target.IsDirectory) Directory.Move(target.Path, destination); else File.Move(target.Path, destination);
            }
            void Delete(LocalLibraryEntry item, List<LibraryDeletionTarget> plan, Action<LibraryDeletionTarget>? recycle = null) =>
                LibraryDeletion.Delete(settings, item, plan, protectedRoots, _ => false, store.Save, recycle ?? Recycle);

            var item = Add("game.zip"); var sibling = Add("unrelated.elf");
            var folder = Path.Combine(root, "installed-game"); Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "nested", "eboot.bin"), "synthetic executable");
            item.DownloadPath = item.ContentPath; item.ContentPath = Path.Combine(folder, "nested");
            item.InstalledGame = new() { Directory = folder };
            settings.Downloads.Add(new() { Destination = item.DownloadPath, State = "Downloaded", ActualSha256 = "retained checksum" });
            var plan = Plan(item); store.Save(settings);
            Check(plan.Count == 2 && plan.Any(path => path.Path == folder), "Installed root and original ZIP must be selected once; nested paths must be collapsed");
            Check(File.Exists(item.DownloadPath) && Directory.Exists(folder), "Preparation/cancel leaves all files intact");
            store.BeforeSave = _ => throw new IOException("synthetic persistence failure");
            Reject(() => Delete(item, plan)); store.BeforeSave = null;
            Check(item.DeletionTargets is null && Directory.Exists(folder) && File.Exists(item.DownloadPath), "Failed intent save must preserve files and the entry");
            var calls = 0;
            Reject(() => Delete(item, plan, path => { if (++calls == 2) throw new IOException("synthetic recycle failure"); Recycle(path); }));
            Check(settings.Library.Contains(item) && item.DeletionTargets!.Count(path => path.Completed) == 1, "Partial failure retains entry and completed-path checkpoint");
            settings = store.Load(); item = settings.Library.Single(entry => entry.Id == item.Id);
            Check(item.Classification.StartsWith("Deletion needs attention") && Directory.Exists(folder), "Restart retains remaining files and never resumes deletion");
            Delete(item, Plan(item));
            Check(settings.Library.Count == 1 && File.Exists(sibling.ContentPath) && !Directory.Exists(folder), "Retry removes only remaining confirmed paths and selected entry");
            Check(store.Load().Library.Count == 1 && settings.Downloads.Single().State == "Missing" && settings.Downloads.Single().ActualSha256 == "retained checksum" &&
                settings.Downloads.Single().IsCleared && store.Load().Downloads.Single().IsCleared,
                "Removal survives restart, hides the download and preserves verification evidence");

            item = Add("shared.elf"); var shared = new LocalLibraryEntry { ContentPath = item.ContentPath }; settings.Library.Add(shared);
            Reject(() => Plan(item)); settings.Library.Remove(shared);
            var active = new DownloadJob { Destination = item.ContentPath, State = "Paused" }; settings.Downloads.Add(active);
            Reject(() => Plan(item)); active.State = "Downloaded"; active.Completion = new() { Phase = "Pending" };
            Reject(() => Plan(item)); active.Completion = new() { Phase = "Completed" };
            Reject(() => LibraryDeletion.Prepare(settings, item, protectedRoots, job => job == active)); settings.Downloads.Remove(active);
            plan = Plan(item); var original = item.ContentPath + ".original"; File.Move(item.ContentPath, original); File.WriteAllText(item.ContentPath, "replacement bytes");
            Reject(() => Delete(item, plan)); Check(File.ReadAllText(item.ContentPath) == "replacement bytes", "Replacement after confirmation is preserved");
            File.Delete(item.ContentPath); File.Move(original, item.ContentPath);
            plan = Plan(item);
            using (var locked = new FileStream(item.ContentPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => Delete(item, plan));
            Check(File.Exists(item.ContentPath) && settings.Library.Contains(item), "Locked file and entry remain recoverable");
            Delete(item, Plan(item));

            item = Add("checkpoint-save.elf"); plan = Plan(item); store.Save(settings);
            store.BeforeSave = candidate => { if (candidate.Library.Single(entry => entry.Id == item.Id).DeletionTargets?.Any(path => path.Completed) == true) throw new IOException("synthetic progress-save failure"); };
            Reject(() => Delete(item, plan)); store.BeforeSave = null;
            settings = store.Load(); item = settings.Library.Single(entry => entry.Id == item.Id);
            Check(item.DeletionTargets!.All(path => !path.Completed) && !File.Exists(item.ContentPath), "Crash before checkpoint save keeps intent and missing-path evidence");
            Delete(item, Plan(item), _ => throw new Exception("Missing deleted file must not be recycled again"));

            var absent = new LocalLibraryEntry { ContentPath = Path.Combine(root, "missing.elf") }; settings.Library.Add(absent);
            plan = Plan(absent); File.WriteAllText(absent.ContentPath, "new unrelated file");
            Reject(() => Delete(absent, plan)); File.Delete(absent.ContentPath); Delete(absent, plan);
            var unsafeRoot = new LocalLibraryEntry { ContentPath = root }; settings.Library.Add(unsafeRoot);
            Reject(() => Plan(unsafeRoot)); unsafeRoot.ContentPath = settings.DownloadRoot; Reject(() => Plan(unsafeRoot));
            unsafeRoot.ContentPath = Path.Combine(settings.DownloadRoot, "Installed"); Reject(() => Plan(unsafeRoot));
            unsafeRoot.ContentPath = Path.GetPathRoot(root)!; Reject(() => Plan(unsafeRoot));
            unsafeRoot.ContentPath = Path.Combine(settings.DownloadRoot, ".ps5catalog-staging", "x"); Reject(() => Plan(unsafeRoot));
            settings.Library.Remove(unsafeRoot);

            var link = Path.Combine(root, "linked");
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, trash }) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!) { process.WaitForExit(); Check(process.ExitCode == 0, "Synthetic junction created"); }
            try
            {
                var linked = new LocalLibraryEntry { ContentPath = link }; settings.Library.Add(linked); Reject(() => Plan(linked)); settings.Library.Remove(linked);
                var containing = new LocalLibraryEntry { ContentPath = Path.Combine(root, "container") }; Directory.CreateDirectory(containing.ContentPath);
                Directory.Move(link, Path.Combine(containing.ContentPath, "link")); settings.Library.Add(containing); Reject(() => Plan(containing));
                Directory.Delete(Path.Combine(containing.ContentPath, "link")); settings.Library.Remove(containing);
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }

            item = Add("final-save.elf");
            var finalDownload = new DownloadJob { Destination = item.ContentPath, State = "Downloaded" }; settings.Downloads.Add(finalDownload);
            plan = Plan(item); store.Save(settings);
            store.BeforeSave = candidate => { if (!candidate.Library.Any(entry => entry.Id == item.Id)) throw new IOException("synthetic final save failure"); };
            Reject(() => Delete(item, plan)); store.BeforeSave = null;
            Check(settings.Library.Contains(item) && item.DeletionTargets!.All(path => path.Completed), "Failed final save retains a recoverable entry");
            Check(!finalDownload.IsCleared && !store.Load().Downloads.Single(job => job.Id == finalDownload.Id).IsCleared, "failed final save keeps download visible until deletion completes");
            Delete(item, Plan(item), _ => throw new Exception("Already deleted files must not be recycled again"));
            Check(!store.Load().Library.Any(entry => entry.Id == item.Id), "Final-save retry only finishes registration removal");
            Check(finalDownload.IsCleared && store.Load().Downloads.Single(job => job.Id == finalDownload.Id).IsCleared, "final-save retry also clears the download atomically");

            var installedFolder = Path.Combine(root, "moved-game"); Directory.CreateDirectory(installedFolder);
            File.WriteAllText(Path.Combine(installedFolder, "eboot.bin"), "synthetic game");
            var retainedArchive = Path.Combine(root, "retained.rar"); File.WriteAllText(retainedArchive, "retained source");
            var associated = new DownloadJob { Destination = retainedArchive, State = "Imported" };
            var nestedDownload = new DownloadJob { Destination = Path.Combine(installedFolder, "eboot.bin"), State = "Imported" };
            var unrelated = new DownloadJob { Destination = sibling.ContentPath, Title = "Same game title", State = "Imported" };
            var parentDownload = new DownloadJob { Destination = root, State = "Imported" };
            settings.Downloads.AddRange([associated, nestedDownload, unrelated, parentDownload]);
            var moved = new LocalLibraryEntry { Title = unrelated.Title, ContentPath = installedFolder, ContentMove = new() { Phase = "Completed" },
                DownloadArtifacts = [new() { JobId = associated.Id, Destination = retainedArchive }] };
            settings.Library.Add(moved);
            associated.State = "Paused"; Reject(() => Plan(moved)); associated.State = "Imported";
            Delete(moved, Plan(moved));
            Check(associated.IsCleared && nestedDownload.IsCleared && !unrelated.IsCleared && !parentDownload.IsCleared && File.ReadAllText(retainedArchive) == "retained source" && File.Exists(sibling.ContentPath),
                "folder deletion clears registered and nested downloads, preserves retained source files and unrelated same-title downloads");
            Console.WriteLine("PASS library deletion: exact paths, installed roots, cancellation, persistence failure, partial retry/restart, shared/active/replaced files, protected roots and links");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception("Unsafe deletion was accepted");
    }
}
