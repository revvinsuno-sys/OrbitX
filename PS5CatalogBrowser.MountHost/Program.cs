using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PS5CatalogBrowser.Core;

// This helper accepts one authenticated parent session, owns one read-only mount, and exits after release.
if (args.Length != 3 || args[0] != "--mount-session" || !args[1].StartsWith("PS5CatalogMount-", StringComparison.Ordinal) ||
    !Guid.TryParseExact(args[1][16..], "N", out _) || !int.TryParse(args[2], out var parentPid)) return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await pipe.ConnectAsync(timeout.Token);
if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != parentPid) return 3;
using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
string drive = "", ownedDescription = "", device = ""; MountRequest? request = null; var released = false; var resultCode = 1;
FileStream? executableLease = null; FileStream? wrapperLease = null;
try
{
    var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Parent disconnected.");
    if (line.Length > 32768) throw new InvalidDataException("Invalid mount request.");
    request = JsonSerializer.Deserialize<MountRequest>(line) ?? throw new InvalidDataException("Invalid mount request.");
    if (request.Source.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(request.Source) || !InstallationSource.IsCandidate(request.Source))
        throw new InvalidDataException("Only local standalone exFAT image files can be mounted.");
    executableLease = ImageFileSystem.OpenRead(request.Executable);
    OSFMountDependency.Validate(request.Executable);
    if (Path.GetExtension(request.Executable).Equals(".com", StringComparison.OrdinalIgnoreCase))
    {
        var sibling = Path.ChangeExtension(request.Executable, ".exe");
        wrapperLease = ImageFileSystem.OpenRead(sibling); OSFMountDependency.Validate(sibling, true);
    }
    drive = OSFMountProtocol.FreeDrive(d => Native.Device(d).Length > 0 || Directory.Exists(d + "\\"));
    var mounted = await RunAsync("-a", "-t", "file", "-f", request.Source, "-m", drive, "-o", "ro,logical,fix");
    var query = await RunAsync("-l", "-m", drive);
    if (mounted.ExitCode == 0 && query.ExitCode == 0 && OSFMountProtocol.IsOwnedDescription(query.Output, drive, request.Source))
    {
        ownedDescription = query.Output.Trim(); device = Native.Device(drive);
    }
    if (mounted.ExitCode != 0 || ownedDescription.Length == 0 || device.Length == 0)
        throw new IOException("OSFMount did not establish a confirmed read-only mount: " + mounted.Output);
    var root = drive + "\\";
    if (!Directory.Exists(root) || !new DriveInfo(root).DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Mounted image is not an accessible exFAT filesystem.");
    await Send(new(true, root, ownedDescription, ""));
    // The parent may take hours to hash/copy; pipe closure is also the crash cleanup signal.
    var command = await reader.ReadLineAsync();
    if (command is not (null or "release")) throw new InvalidDataException("Unsupported mount command.");
    await Release(); resultCode = 0;
    if (command is not null) await Send(new(true, "", "Released", ""));
}
catch (Exception ex)
{
    var error = ex.Message;
    try { await Release(); }
    catch (Exception cleanup) { error += " Mount cleanup needs attention: " + cleanup.Message; }
    try { await Send(new(false, drive, ownedDescription, error)); } catch (IOException) { }
}
finally { executableLease?.Dispose(); wrapperLease?.Dispose(); }
return resultCode;

async Task Send(MountResponse response) => await writer.WriteLineAsync(JsonSerializer.Serialize(response));
async Task Release()
{
    if (released || drive.Length == 0 || request is null) return;
    if (ownedDescription.Length == 0)
    {
        if (Native.Device(drive).Length > 0) throw new IOException("Mount ownership could not be established; no dismount was attempted. Inspect " + drive + " in OSFMount.");
        released = true; return;
    }
    var query = await RunAsync("-l", "-m", drive);
    if (query.ExitCode != 0 || query.Output.Trim() != ownedDescription || Native.Device(drive) != device || !OSFMountProtocol.IsOwnedDescription(query.Output, drive, request.Source))
        throw new IOException("Mount identity changed; no dismount was attempted. Inspect " + drive + " in OSFMount.");
    var unmounted = await RunAsync("-d", "-m", drive);
    if (unmounted.ExitCode != 0 || Native.Device(drive).Length > 0 || Directory.Exists(drive + "\\"))
        throw new IOException("Owned mount could not be released normally. Inspect " + drive + " in OSFMount. " + unmounted.Output);
    released = true;
}
async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
{
    var info = new ProcessStartInfo(request!.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new IOException("OSFMount did not start.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try { await process.WaitForExitAsync(limit.Token); }
    catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("OSFMount command timed out; mount ownership must be checked."); }
    return (process.ExitCode, (await stdout) + (await stderr));
}
static class Native
{
    public static string Device(string drive)
    {
        var text = new StringBuilder(32768);
        if (QueryDosDevice(drive, text, text.Capacity) != 0) return text.ToString();
        if (Marshal.GetLastWin32Error() != 2) throw new IOException("Cannot establish drive-letter ownership: " + drive);
        return "";
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, StringBuilder target, int length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
