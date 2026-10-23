using PS5CatalogBrowser.Core;

internal static class InstalledFolderLayoutTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5InstalledLayout-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var mode in new[] { "future", "existing", "save-before", "save-after", "cancel", "recovery-before", "recovery-lifted", "recovery-wrapped", "recovery-finished", "multiple-games", "nested-game" })
            {
                var local = Path.Combine(root, mode);
                var downloads = Path.Combine(local, "downloads"); var installed = Path.Combine(local, "installed");
                var folder = Path.Combine(mode == "future" ? downloads : installed, "wrapper-extracted");
                var game = Path.Combine(folder, "wrapper", "Game"); Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
                File.WriteAllText(Path.Combine(game, "eboot.bin"), "synthetic executable");
                File.WriteAllText(Path.Combine(game, "sce_sys", "param.json"), """{"titleId":"PPSA06438","titleName":"Layout game"}""");
                Directory.CreateDirectory(Path.Combine(game, "decrypted")); File.WriteAllText(Path.Combine(game, "decrypted", "eboot.bin"), "executable variant");
                File.WriteAllText(Path.Combine(folder, "Note.txt"), "preserved archive note");
                if (mode == "existing") { Directory.CreateDirectory(Path.Combine(game, "Archive extras")); File.WriteAllText(Path.Combine(game, "Archive extras", "keep.txt"), "existing extras preserved"); }
                if (mode == "multiple-games") { Directory.CreateDirectory(Path.Combine(folder, "Other")); File.WriteAllText(Path.Combine(folder, "Other", "eboot.bin"), "other game"); }
                if (mode == "nested-game") { Directory.CreateDirectory(Path.Combine(game, "Other")); File.WriteAllText(Path.Combine(game, "Other", "eboot.bin"), "other game"); }
                var scan = await ImageFileSystem.ScanAsync(folder, default, false);
                var files = new List<ImageFile>();
                foreach (var file in scan.Files) files.Add(file with { Sha256 = await ImageFileSystem.HashAsync(Path.Combine(folder, file.RelativePath), default) });
                var previous = new ContentMoveIntent { Source = Path.Combine(downloads, "original"), Destination = folder, InstalledRoot = installed,
                    Directory = true, Phase = "Completed", Files = files, Directories = scan.Directories };
                var item = new LocalLibraryEntry { ContentPath = folder, Extraction = "Extracted", ContentMove = mode == "future" ? null : previous };
                var settings = new AppSettings { DownloadRoot = downloads, InstalledGamesRoot = installed, Library = [item], Sources = [] };
                var store = new SettingsStore(Path.Combine(local, "settings")); store.Save(settings);
                if (mode.StartsWith("recovery-"))
                {
                    var plan = (await InstalledFolderLayout.PlanAsync(folder, default))!;
                    item.ContentMove = new() { Phase = "Normalizing", Layout = plan, PreviousMove = previous }; store.Save(settings);
                    if (mode is "recovery-lifted" or "recovery-wrapped") Directory.Move(game, plan.Temporary);
                    if (mode == "recovery-wrapped") Directory.Move(folder, Path.Combine(plan.Temporary, plan.Extras));
                    if (mode == "recovery-finished") plan.Apply();
                    settings = store.LoadForStartup(); item = settings.Library.Single();
                    Check(item.ContentMove?.Phase == "Completed" && File.Exists(Path.Combine(game, "eboot.bin")), "restart restores original layout: " + mode);
                    await RunnableContentMove.VerifyAsync(item.ContentMove!, folder, default);
                    continue;
                }
                var faulted = false;
                store.BeforeSave = candidate =>
                {
                    var phase = candidate.Library.Single().ContentMove?.Phase;
                    if (!faulted && (mode == "save-before" && phase == "Normalizing" || mode == "save-after" && phase == "Completed"))
                    { faulted = true; throw new IOException("Synthetic layout persistence failure"); }
                };
                using var cancellation = new CancellationTokenSource(); if (mode == "cancel") cancellation.Cancel();
                try
                {
                    var changed = mode == "future" ? await RunnableContentMove.MoveAsync(settings, item, store.Save, _ => false) :
                        await RunnableContentMove.NormalizeInstalledAsync(settings, item, store.Save, cancellation.Token);
                    Check(changed == (mode is not ("multiple-games" or "nested-game")), "only one unambiguous game is normalized");
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                { Check(mode is "save-before" or "save-after" or "cancel", "unexpected layout failure: " + mode); }
                store.BeforeSave = null;
                if (mode is "save-before" or "save-after" or "cancel" or "multiple-games" or "nested-game")
                {
                    Check(File.Exists(Path.Combine(game, "eboot.bin")) && File.ReadAllText(Path.Combine(folder, "Note.txt")) == "preserved archive note", "failure preserves original files: " + mode);
                    await RunnableContentMove.VerifyAsync(previous, folder, default);
                    continue;
                }
                Check(File.Exists(Path.Combine(item.ContentPath, "eboot.bin")) && File.ReadAllText(Path.Combine(item.ContentPath, mode == "existing" ? "Archive extras (1)" : "Archive extras", "Note.txt")) == "preserved archive note", "game root is flat and companion files are preserved");
                if (mode == "existing") Check(File.ReadAllText(Path.Combine(item.ContentPath, "Archive extras", "keep.txt")) == "existing extras preserved", "extras-name collision preserves existing game files");
                Check(Directory.GetParent(item.ContentPath)!.FullName == installed, "game is directly under Installed");
                await RunnableContentMove.VerifyAsync(item.ContentMove!, item.ContentPath, default);
                var reopened = store.LoadForStartup(); await RunnableContentMove.VerifyAsync(reopened.Library.Single().ContentMove!, item.ContentPath, default);
            }
            Console.WriteLine("PASS installed layout: new/existing roots, executable variants, extras, manifests, collisions, cancellation, save failures and every rename recovery checkpoint");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
