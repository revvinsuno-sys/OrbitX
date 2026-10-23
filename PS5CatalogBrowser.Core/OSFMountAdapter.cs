using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace PS5CatalogBrowser.Core;

public sealed record MountRequest(string Executable, string Source);
public sealed record MountResponse(bool Success, string Root, string Description, string Error);

public sealed class OSFMountAdapter(string executable, string helper) : IImageMounter
{
    public Task CheckAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested(); OSFMountDependency.Validate(executable);
        if (!File.Exists(helper)) throw new FileNotFoundException("The installation mount helper is missing. Rebuild/reinstall OrbitX.", helper);
    }, cancellation);
    public async Task<IImageMount> MountAsync(string source, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OSFMount requires Windows.");
        cancellation.ThrowIfCancellationRequested();
        var pipeName = "PS5CatalogMount-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
        Process? process = null; Session? session = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(Path.GetFullPath(helper))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "--mount-session", pipeName, Environment.ProcessId.ToString() }
            }) ?? throw new IOException("Mount helper did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var connection = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(connection, exited) == exited) throw new IOException("Mount helper exited before connecting.");
            await connection;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != process.Id)
                throw new IOException("Mount helper pipe client identity did not match the launched helper.");
            session = new(pipe, process);
            await session.Writer.WriteLineAsync(JsonSerializer.Serialize(new MountRequest(Path.GetFullPath(executable), Path.GetFullPath(source))));
            var response = await session.ReadAsync(timeout.Token);
            if (!response.Success) throw new IOException(response.Error + (response.Description.Length > 0 ? " Mount details: " + response.Description : ""));
            session.Root = response.Root; session.Description = response.Description;
            if (cancellation.IsCancellationRequested) { await session.ReleaseAsync(); cancellation.ThrowIfCancellationRequested(); }
            return session;
        }
        catch
        {
            // Pipe closure causes the helper to release its owned mount, including when the parent dies.
            pipe.Dispose();
            if (process is not null)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try { await process.WaitForExitAsync(cleanupTimeout.Token); }
                catch (OperationCanceledException) { throw new IOException("Mount cleanup needs attention: helper did not finish. Inspect the source image in OSFMount; original image is preserved."); }
                process.Dispose();
            }
            throw;
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    private sealed class Session : IImageMount
    {
        private readonly NamedPipeServerStream pipe;
        private readonly Process process;
        private readonly StreamReader reader;
        public StreamWriter Writer { get; }
        private bool released;
        public string Root { get; set; } = "";
        public string Description { get; set; } = "";
        public Session(NamedPipeServerStream stream, Process host)
        {
            pipe = stream; process = host;
            reader = new(pipe, Encoding.UTF8, false, 4096, true);
            Writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        }
        public async Task<MountResponse> ReadAsync(CancellationToken cancellation)
        {
            var line = await reader.ReadLineAsync(cancellation) ?? throw new IOException("Mount helper disconnected. Check installation details before releasing any orphaned mount.");
            return JsonSerializer.Deserialize<MountResponse>(line) ?? throw new IOException("Invalid mount helper response.");
        }
        public async Task ReleaseAsync()
        {
            if (released) return;
            await Writer.WriteLineAsync("release");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var response = await ReadAsync(timeout.Token);
            if (!response.Success) throw new IOException(response.Error);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new IOException("Mount helper could not confirm release.");
            released = true;
        }
        public ValueTask DisposeAsync()
        {
            Writer.Dispose(); reader.Dispose(); pipe.Dispose(); process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
