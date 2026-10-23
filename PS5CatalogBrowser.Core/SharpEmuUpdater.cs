using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public sealed record SharpEmuInstallation(string UpstreamCommit, string Version, string ExecutableSha256,
    string PatchSha256, DateTimeOffset InstalledAt, string F10Support = "Patched SDL/Vulkan menu");

public sealed class SharpEmuUpdater(string root, string patch, Action<string> output)
{
    public const string DefaultRoot = @"D:\Games\PS5\Tools\SharpEmu";
    public const string OfficialOrigin = "https://github.com/sharpemu/sharpemu.git";
    private const string MetadataFile = "catalog-browser-installation.json";
    public string Status { get; private set; } = "Not checked";
    public string LogPath { get; private set; } = "";
    // Same direct-process/ArgumentList boundary as Kyty's updater; a seam for offline failure tests.
    public Func<ProcessStartInfo, Action<string>, Task<int>> RunProcess { get; set; } = RunAsync;
    public Action<string>? ActivationCheckpoint { get; set; }
    public string InstallationDirectory => Path.Combine(root, "installed");
    private string Source => Path.Combine(root, "source");

    public static IDisposable AcquireRuntimeLease(string directory, bool updating)
    {
        ImageFileSystem.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".runtime-lock");
        ImageFileSystem.EnsureNoLinks(path);
        if (!updating && File.Exists(Path.Combine(directory, "activation.json"))) throw new IOException("SharpEmu activation was interrupted. Retry Update/build to recover before launching.");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            updating ? FileShare.None : FileShare.ReadWrite); }
        catch (IOException ex) { throw new IOException("SharpEmu is running or another update is active. Close it and retry.", ex); }
    }

    public static SharpEmuInstallation? ReadInstallation(string directory)
    {
        var path = Path.Combine(directory, MetadataFile);
        ImageFileSystem.EnsureNoLinks(path);
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<SharpEmuInstallation>(File.ReadAllText(path))
            ?? throw new InvalidDataException("SharpEmu installation metadata is empty.");
        if (!IsRevision(value.UpstreamCommit) || string.IsNullOrWhiteSpace(value.Version) ||
            value.ExecutableSha256 is not { Length: 64 } || value.PatchSha256 is not { Length: 64 })
            throw new InvalidDataException("SharpEmu installation metadata is invalid.");
        return value;
    }

    private static string Hash(string path)
    {
        ImageFileSystem.EnsureNoLinks(path);
        using var input = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
    }
    public static bool IsRevision(string? value) => value is not null && Regex.IsMatch(value, "\\A[0-9a-f]{40}\\z");
    public async Task<string> CheckAsync()
    {
        using var lease = AcquireRuntimeLease(root, updating: true);
        await VerifySourceAsync();
        var result = await Git("ls-remote", "--exit-code", "origin", "refs/heads/main");
        var sha = result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!IsRevision(sha)) throw new InvalidDataException("Official upstream returned an invalid main revision.");
        var installed = ReadInstallation(InstallationDirectory);
        if (installed?.UpstreamCommit == sha)
        {
            try { VerifyPublished(InstallationDirectory, installed); SetStatus("Up to date"); }
            catch (IOException) { SetStatus("Installation needs repair"); }
        }
        else SetStatus("Update available");
        return sha;
    }

    private void SetStatus(string value) { Status = value; output("SharpEmu: " + value); }
    private async Task VerifySourceAsync()
    {
        ImageFileSystem.EnsureNoLinks(Path.Combine(Source, ".git"));
        if (!Directory.Exists(Path.Combine(Source, ".git"))) throw new IOException("The managed official SharpEmu source checkout is missing.");
        if (!ImageFileSystem.SamePath(Source, (await Git("rev-parse", "--show-toplevel")).Trim()))
            throw new IOException("SharpEmu source checkout ownership does not match the managed source directory.");
        if ((await Git("remote", "get-url", "origin")).Trim() != OfficialOrigin)
            throw new IOException("SharpEmu source origin is not the official HTTPS repository.");
        if ((await Git("branch", "--show-current")).Trim() != "main" || (await Git("status", "--porcelain")).Length != 0)
            throw new IOException("SharpEmu source must be clean on upstream main. Existing files were preserved.");
    }

    public async Task<SharpEmuInstallation> UpdateAsync(string target)
    {
        if (!IsRevision(target)) throw new ArgumentException("Check for updates to select a full official revision.");
        using var lease = AcquireRuntimeLease(root, updating: true);
        EnsureNotRunning();
        RecoverActivationCore();
        await VerifySourceAsync();
        var existing = ReadInstallation(InstallationDirectory);
        ImageFileSystem.EnsureNoLinks(patch);
        var patchBytes = File.ReadAllBytes(patch);
        var patchHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(patchBytes));
        if (existing is not null && existing.UpstreamCommit == target && existing.PatchSha256 == patchHash)
        {
            try { VerifyPublished(InstallationDirectory, existing); SetStatus("Up to date"); return existing; }
            catch (IOException ex) { SetStatus("Repairing the managed installation: " + ex.Message); }
        }
        var transaction = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(root, "staging", transaction);
        ImageFileSystem.EnsureNoLinks(stage);
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        LogPath = Path.Combine(root, "logs", transaction + ".log");
        ImageFileSystem.EnsureNoLinks(LogPath);
        using var log = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
        logWriter = log;
        try
        {
            SetStatus("Updating: fetching official source");
            await Git("fetch", "--no-tags", "origin", "main");
            var fetched = (await Git("rev-parse", "refs/remotes/origin/main")).Trim();
            if (fetched != target) throw new IOException("Upstream main changed since the check. Check for updates again before building.");
            if ((await Git("ls-tree", "-r", target)).Split('\n').Any(line => line.StartsWith("120000 ", StringComparison.Ordinal)))
                throw new IOException("Upstream archive contains symbolic links; staging requires review.");
            var archive = Path.Combine(stage, "source.zip");
            await Git("archive", "--format=zip", "--output=" + archive, target);
            var buildSource = Path.Combine(stage, "source");
            System.IO.Compression.ZipFile.ExtractToDirectory(archive, buildSource);
            var frozenPatch = Path.Combine(stage, "f10-menu.patch");
            File.WriteAllBytes(frozenPatch, patchBytes);
            Log("Upstream commit: " + target + "; patch SHA256: " + patchHash);
            SetStatus("Updating: patch preflight");
            if (File.ReadAllText(Path.Combine(buildSource, "src", "SharpEmu.Libs", "VideoOut", "SdlHostWindow.cs")).Contains("SDLK_F10", StringComparison.Ordinal))
                throw new InvalidDataException("Upstream now handles F10. Review native support before applying this patch.");
            await Command("git", buildSource, "apply", "--check", frozenPatch);
            await Command("git", buildSource, "apply", frozenPatch);
            SetStatus("Updating: publishing Windows x64 build");
            Log("SDK requirement: " + File.ReadAllText(Path.Combine(buildSource, "global.json")));
            await Command("dotnet", buildSource, "--version");
            var publish = Path.Combine(stage, "publish");
            await Command("dotnet", buildSource, "publish", "src/SharpEmu.CLI/SharpEmu.CLI.csproj", "-c", "Release", "-r", "win-x64",
                "--self-contained", "true", "-o", publish, "--nologo", "--disable-build-servers", "-p:UseSharedCompilation=false", "-p:SourceRevisionId=" + target);
            var exe = Path.Combine(publish, "SharpEmu.exe");
            var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? throw new IOException("SharpEmu version is unavailable.");
            var metadata = new SharpEmuInstallation(target, version, Hash(exe), patchHash, DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(publish, MetadataFile), JsonSerializer.Serialize(metadata));
            VerifyPublished(publish, metadata);
            SetStatus("Updating: validating menu and input");
            await Command("dotnet", buildSource, "test", "tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj", "-c", "Release",
                "--filter", "FullyQualifiedName~BrowserGameMenuTests|FullyQualifiedName~BrowserPresenterTests|FullyQualifiedName~HostWindowInputTests|FullyQualifiedName~SdlHostWindowTests", "--nologo", "--disable-build-servers", "-p:UseSharedCompilation=false");
            // The focused presenter test intentionally requires a real SDL/Vulkan device; no silently skipped GPU acceptance.
            EnsureNotRunning();
            ActivateCore(publish, metadata);
            SetStatus("Installed successfully");
            return metadata;
        }
        catch
        {
            SetStatus(Status.Contains("patch", StringComparison.OrdinalIgnoreCase) ? "Patch failure" :
                Status.Contains("publishing", StringComparison.OrdinalIgnoreCase) || Status.Contains("input", StringComparison.OrdinalIgnoreCase) ? "Build failure" : "Update failed");
            throw;
        }
        finally
        {
            logWriter = null;
            // Only the unique transaction created above is disposable; installed/previous/logs remain.
            if (File.Exists(Path.Combine(root, "activation.json"))) output("Recovery transaction retained: " + stage);
            else
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try { Directory.Delete(stage, recursive: true); break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt == 19) output("Temporary build retained: " + stage + ": " + ex.Message);
                        else await Task.Delay(100).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private StreamWriter? logWriter;
    private readonly object logGate = new();
    private void Log(string value) { lock (logGate) { logWriter?.WriteLine(value); output(value); } }
    private async Task<string> Git(params string[] args) => await Command("git", Source, args);
    private async Task<string> Command(string executable, string cwd, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (executable == "dotnet") info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        var text = new StringBuilder(); var gate = new object();
        var exit = await RunProcess(info, line => { lock (gate) text.AppendLine(line); Log(line); }).ConfigureAwait(false);
        if (exit != 0) throw new IOException($"{executable} failed (exit {exit}). See {LogPath} for details.");
        return text.ToString().TrimEnd();
    }

    public static async Task<int> RunAsync(ProcessStartInfo info, Action<string> output)
    {
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + info.FileName);
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) output(line); }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        await process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private void EnsureNotRunning()
    {
        foreach (var process in Process.GetProcessesByName("SharpEmu"))
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } path || ImageFileSystem.IsWithin(InstallationDirectory, path))
                        throw new IOException("Close SharpEmu before updating its managed installation.");
                }
                catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Cannot verify a running SharpEmu process. Close it before updating.", ex); }
            }
    }

    public static void VerifyPublished(string directory, SharpEmuInstallation metadata)
    {
        ImageFileSystem.EnsureNoLinks(directory);
        var exe = Path.Combine(directory, "SharpEmu.exe");
        SharpEmuLauncher.ValidateExecutable(exe);
        if (ReadInstallation(directory) != metadata) throw new IOException("SharpEmu installation metadata does not match the staged build.");
        if (!IsRevision(metadata.UpstreamCommit) || !File.Exists(exe) || Hash(exe) != metadata.ExecutableSha256)
            throw new IOException("SharpEmu executable/revision integrity validation failed.");
        foreach (var name in new[] { "avcodec-61.dll", "avformat-61.dll", "avutil-59.dll", "swresample-5.dll", "SharpEmu.LibAtrac9.dll" })
            if (!File.Exists(Path.Combine(directory, "plugins", name))) throw new IOException("SharpEmu publish is missing plugins/" + name);
    }

    private sealed record Activation(string Stage, string Backup, bool HadInstalled);
    public void Activate(string published, SharpEmuInstallation metadata)
    {
        using var lease = AcquireRuntimeLease(root, updating: true);
        ActivateCore(published, metadata);
    }
    private void ActivateCore(string published, SharpEmuInstallation metadata)
    {
        ImageFileSystem.EnsureNoLinks(InstallationDirectory);
        VerifyPublished(published, metadata);
        if (!ImageFileSystem.IsWithin(Path.Combine(root, "staging"), published)) throw new IOException("Activation requires an owned staging build.");
        EnsureNotRunning();
        var backup = Path.Combine(root, "previous", Guid.NewGuid().ToString("N"));
        ImageFileSystem.EnsureNoLinks(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var journal = Path.Combine(root, "activation.json");
        if (File.Exists(journal)) throw new IOException("Recover the interrupted SharpEmu activation before retrying.");
        var activation = new Activation(published, backup, Directory.Exists(InstallationDirectory));
        using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, activation); stream.Flush(true); }
        try
        {
            if (activation.HadInstalled) Directory.Move(InstallationDirectory, backup);
            ActivationCheckpoint?.Invoke("PreviousPreserved");
            Directory.Move(published, InstallationDirectory);
            ActivationCheckpoint?.Invoke("Activated");
            VerifyPublished(InstallationDirectory, metadata);
            File.Delete(journal);
        }
        catch { RecoverActivationCore(); throw; }
    }

    public void RecoverActivation()
    {
        using var lease = AcquireRuntimeLease(root, updating: true);
        EnsureNotRunning(); RecoverActivationCore();
    }
    private void RecoverActivationCore()
    {
        var journal = Path.Combine(root, "activation.json");
        ImageFileSystem.EnsureNoLinks(journal);
        if (!File.Exists(journal)) return;
        var activation = JsonSerializer.Deserialize<Activation>(File.ReadAllText(journal)) ?? throw new IOException("Invalid activation journal.");
        if (!ImageFileSystem.IsWithin(Path.Combine(root, "staging"), activation.Stage) ||
            !ImageFileSystem.IsWithin(Path.Combine(root, "previous"), activation.Backup)) throw new IOException("Unsafe activation recovery paths.");
        ImageFileSystem.EnsureNoLinks(activation.Stage); ImageFileSystem.EnsureNoLinks(activation.Backup); ImageFileSystem.EnsureNoLinks(InstallationDirectory);
        if (Directory.Exists(activation.Backup))
        {
            if (Directory.Exists(InstallationDirectory))
            {
                if (Directory.Exists(activation.Stage)) throw new IOException("Ambiguous activation recovery. Both staging and installed builds exist.");
                Directory.Move(InstallationDirectory, activation.Stage);
            }
            Directory.Move(activation.Backup, InstallationDirectory);
        }
        else if (activation.HadInstalled && !Directory.Exists(InstallationDirectory)) throw new IOException("Previous SharpEmu installation is missing; manual recovery required.");
        else if (!activation.HadInstalled && Directory.Exists(InstallationDirectory) && !Directory.Exists(activation.Stage))
            Directory.Move(InstallationDirectory, activation.Stage);
        File.Delete(journal);
    }
}
