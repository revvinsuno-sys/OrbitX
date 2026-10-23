using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using PS5CatalogBrowser.Core;

internal static class SharpEmuTests
{
    private const string Old = "1111111111111111111111111111111111111111", Latest = "2222222222222222222222222222222222222222";
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Hash(string file) { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)); }
    private static SharpEmuInstallation PublishFixture(string directory, string revision)
    {
        Directory.CreateDirectory(Path.Combine(directory, "plugins"));
        var exe = Path.Combine(directory, "SharpEmu.exe");
        File.Copy(Environment.ProcessPath!, exe);
        foreach (var name in new[] { "avcodec-61.dll", "avformat-61.dll", "avutil-59.dll", "swresample-5.dll", "SharpEmu.LibAtrac9.dll" })
            File.WriteAllText(Path.Combine(directory, "plugins", name), "synthetic dependency");
        var metadata = new SharpEmuInstallation(revision, "synthetic-" + revision[..7], Hash(exe), new string('A', 64), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(directory, "catalog-browser-installation.json"), System.Text.Json.JsonSerializer.Serialize(metadata));
        return metadata;
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsStore(Path.Combine(root, "settings"));
            var a = new LocalLibraryEntry { Title = "Same title", Id = "release-a" };
            var b = new LocalLibraryEntry { Title = "Same title", Id = "release-b" };
            settings.Save(new() { Library = [a, b], IgdbClientId = "keep-me", Downloads = [new() { Title = "retained download" }] });
            Check(settings.Load().Library.All(item => item.LastEmulator == EmulatorBackend.KytyPS5), "old/never-run entries default to Kyty");
            a.LastEmulator = EmulatorBackend.SharpEmu; settings.Save(new() { Library = [a, b], IgdbClientId = "keep-me" });
            var restarted = new SettingsStore(settings.DirectoryPath).Load();
            Check(restarted.Library[0].LastEmulator == EmulatorBackend.SharpEmu && restarted.Library[1].LastEmulator == EmulatorBackend.KytyPS5 && restarted.IgdbClientId == "keep-me", "restart preserves per-release choice and unrelated metadata");
            a.LastEmulator = EmulatorBackend.KytyPS5; settings.Save(new() { Library = [a] });
            Check(settings.Load().Library[0].LastEmulator == EmulatorBackend.KytyPS5, "switching back persists");
            var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Library\":[{\"Id\":\"old\",\"Title\":\"Legacy\"}],\"IgdbClientId\":\"retained\"}");
            var oldSettings = new SettingsStore(legacy).Load();
            Check(oldSettings.Library[0].LastEmulator == EmulatorBackend.KytyPS5 && oldSettings.IgdbClientId == "retained", "additive schema preserves legacy records");
            var executable = Path.Combine(root, "SharpEmu.exe"); File.Copy(Environment.ProcessPath!, executable);
            var game = Path.Combine(root, "game with spaces"); Directory.CreateDirectory(game);
            try { SharpEmuLauncher.BuildStartInfo(executable, game); throw new Exception("Missing eboot accepted"); } catch (IOException) { }
            File.WriteAllText(Path.Combine(game, "eboot.bin"), "invalid");
            var start = SharpEmuLauncher.BuildStartInfo(executable, game);
            Check(start.ArgumentList.SequenceEqual(new[] { Path.Combine(game, "eboot.bin"), "--log-file", "--window-mode=windowed" }) && start.WorkingDirectory == root && !start.UseShellExecute, "SharpEmu receives the full path, log option and selected window mode without Kyty-specific ELF/SELF validation");
            try { SharpEmuLauncher.BuildStartInfo(Path.Combine(root, "absent", "SharpEmu.exe"), game); throw new Exception("Missing emulator accepted"); } catch (FileNotFoundException) { }
            var managed = Path.Combine(root, "managed"); Directory.CreateDirectory(Path.Combine(managed, "source", ".git"));
            var patch = Path.Combine(root, "menu.patch"); File.WriteAllText(patch, "fixture patch");
            var updater = new SharpEmuUpdater(managed, patch, _ => { });
            var installed = Path.Combine(managed, "installed");
            var old = PublishFixture(installed, Old);
            File.WriteAllText(Path.Combine(installed, "catalog-browser-installation.json"), System.Text.Json.JsonSerializer.Serialize(old));
            var oldHash = Hash(Path.Combine(installed, "SharpEmu.exe"));
            foreach (var failAt in new[] { "PreviousPreserved", "Activated" })
            {
                var stage = Path.Combine(managed, "staging", Guid.NewGuid().ToString("N"), "publish");
                var next = PublishFixture(stage, Latest);
                updater.ActivationCheckpoint = point => { if (point == failAt) throw new IOException("Injected " + point); };
                try { updater.Activate(stage, next); throw new Exception("Injected activation did not fail"); } catch (IOException) { }
                Check(Hash(Path.Combine(installed, "SharpEmu.exe")) == oldHash && SharpEmuUpdater.ReadInstallation(installed)!.UpstreamCommit == Old,
                    "activation failure restores old executable and metadata: " + failAt);
            }
            updater.ActivationCheckpoint = null;
            var successfulStage = Path.Combine(managed, "staging", "success", "publish");
            var successful = PublishFixture(successfulStage, Latest);
            File.WriteAllText(Path.Combine(successfulStage, "catalog-browser-installation.json"), System.Text.Json.JsonSerializer.Serialize(successful));
            updater.Activate(successfulStage, successful);
            Check(SharpEmuUpdater.ReadInstallation(installed)!.UpstreamCommit == Latest && Directory.GetDirectories(Path.Combine(managed, "previous")).Length > 0, "successful activation changes metadata and preserves previous build");
            using (SharpEmuUpdater.AcquireRuntimeLease(managed, false))
            {
                try { using var denied = SharpEmuUpdater.AcquireRuntimeLease(managed, true); throw new Exception("Updater obtained running-game lease"); } catch (IOException) { }
            }
            using (SharpEmuUpdater.AcquireRuntimeLease(managed, true))
            {
                try { using var denied = SharpEmuUpdater.AcquireRuntimeLease(managed, true); throw new Exception("Concurrent update allowed"); } catch (IOException) { }
                try { using var denied = SharpEmuUpdater.AcquireRuntimeLease(managed, false); throw new Exception("Launch during update allowed"); } catch (IOException) { }
            }
            var patchCalls = 0; var builds = 0; var failPatch = false; var failBuild = true;
            updater.RunProcess = (info, output) =>
            {
                var argv = info.ArgumentList.ToArray();
                if (info.FileName == "git")
                {
                    switch (argv[0])
                    {
                        case "remote": output(SharpEmuUpdater.OfficialOrigin); break;
                        case "branch": output("main"); break;
                        case "ls-remote": output(Latest + "\trefs/heads/main"); break;
                        case "rev-parse": output(argv.Contains("--show-toplevel") ? info.WorkingDirectory : Latest); break;
                        case "archive":
                            using (var zip = ZipFile.Open(argv.Single(arg => arg.StartsWith("--output="))[9..], ZipArchiveMode.Create))
                            {
                                using (var text = new StreamWriter(zip.CreateEntry("src/SharpEmu.Libs/VideoOut/SdlHostWindow.cs").Open())) text.Write("upstream without menu");
                                using (var text = new StreamWriter(zip.CreateEntry("global.json").Open())) text.Write("{}");
                            }
                            break;
                        case "apply": if (argv.Contains("--check") && failPatch) return Task.FromResult(1); if (!argv.Contains("--check")) patchCalls++; break;
                    }
                    return Task.FromResult(0);
                }
                if (argv[0] == "publish") { builds++; if (failBuild) return Task.FromResult(17); PublishFixture(argv[Array.IndexOf(argv, "-o") + 1], Latest); }
                return Task.FromResult(0);
            };
            var metadataPath = Path.Combine(installed, "catalog-browser-installation.json");
            var savedMetadata = File.ReadAllText(metadataPath);
            File.WriteAllText(metadataPath, System.Text.Json.JsonSerializer.Serialize(old));
            Check(await updater.CheckAsync() == Latest && updater.Status == "Update available", "different full upstream SHA is an available update");
            File.WriteAllText(metadataPath, savedMetadata);
            Check(await updater.CheckAsync() == Latest && updater.Status == "Up to date", "same upstream SHA is current");
            // Existing patch differs, so same-source rebuild is deliberate.
            try { await updater.UpdateAsync(Latest); throw new Exception("Build failure accepted"); } catch (IOException) { }
            Check(updater.Status == "Build failure" && SharpEmuUpdater.ReadInstallation(installed)!.UpstreamCommit == Latest && patchCalls == 1, "build failure preserves installation; patch applied once");
            failPatch = true;
            try { await updater.UpdateAsync(Latest); throw new Exception("Patch conflict accepted"); } catch (IOException) { }
            Check(updater.Status == "Patch failure" && builds == 1 && patchCalls == 1, "patch conflict stops before compilation and is not reapplied speculatively");
            failPatch = false; failBuild = false;
            var updated = await updater.UpdateAsync(Latest);
            Check(updater.Status == "Installed successfully" && updated.UpstreamCommit == Latest && updated.PatchSha256 == Hash(patch), "pipeline activates executable and exact source/patch metadata");
            var interruptedStage = Path.Combine(managed, "staging", "crash", "publish");
            var crashBuild = PublishFixture(interruptedStage, Old);
            File.WriteAllText(Path.Combine(interruptedStage, "catalog-browser-installation.json"), System.Text.Json.JsonSerializer.Serialize(crashBuild));
            var backup = Path.Combine(managed, "previous", "crash-backup");
            File.WriteAllText(Path.Combine(managed, "activation.json"), System.Text.Json.JsonSerializer.Serialize(new { Stage = interruptedStage, Backup = backup, HadInstalled = true }));
            Directory.Move(installed, backup); Directory.Move(interruptedStage, installed);
            new SharpEmuUpdater(managed, patch, _ => { }).RecoverActivation();
            Check(SharpEmuUpdater.ReadInstallation(installed)!.UpstreamCommit == Latest && Directory.Exists(interruptedStage), "fresh updater recovers interrupted activation from durable journal");
            var previousBuilds = builds; await updater.UpdateAsync(Latest);
            Check(builds == previousBuilds && updater.Status == "Up to date", "current source and patch do not rebuild");
            File.Delete(Path.Combine(installed, "SharpEmu.exe"));
            await updater.CheckAsync(); Check(updater.Status == "Installation needs repair", "same SHA with missing executable is not reported current");
            await updater.UpdateAsync(Latest);
            Check(File.Exists(Path.Combine(installed, "SharpEmu.exe")) && updater.Status == "Installed successfully", "same-revision update can repair an invalid installation");
            Console.WriteLine("PASS dual-emulator defaults/restart/identity, SharpEmu arguments, update SHA, patch/build failures, activation rollback, success, retry and cross-process leases");
        }
        finally { Directory.Delete(root, true); }
    }
}
