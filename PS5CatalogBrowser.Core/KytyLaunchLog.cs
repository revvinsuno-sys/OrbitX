using System.Diagnostics;
using System.Text;

namespace PS5CatalogBrowser.Core;

public sealed record LoggedLaunch(string LogId, Task<int> Completion);

public static class KytyLaunchLog
{
    public static LoggedLaunch Start(ProcessStartInfo info, string directory, Action<string> logReady, string emulator = "KytyPS5")
    {
        ImageFileSystem.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var writer = new StreamWriter(new FileStream(Path.Combine(directory, id + ".log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        var process = new Process { StartInfo = info };
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;
        try
        {
            var version = emulator == "KytyPS5" ? KytyVersion.Read(info.FileName) : FileVersionInfo.GetVersionInfo(info.FileName).ProductVersion;
            writer.WriteLine($"{emulator} launch · {DateTimeOffset.UtcNow:O}\nExecutable: {info.FileName}\nBuild: {version}\nArguments: {string.Join(" | ", info.ArgumentList)}\n");
            // Persist the association before starting, including very early native crashes.
            logReady(id);
            if (!process.Start()) throw new InvalidOperationException(emulator + " did not start.");
            return new(id, Task.Run(() => CaptureAsync(process, writer)));
        }
        catch (Exception ex)
        {
            try { writer.WriteLine("Launch failed: " + ex.Message); } finally { writer.Dispose(); process.Dispose(); }
            throw;
        }
    }

    private static async Task<int> CaptureAsync(Process process, StreamWriter writer)
    {
        var gate = new object();
        void Write(string text)
        {
            lock (gate)
            {
                // Continue draining both pipes if the disk becomes unavailable: never stall the emulator.
                try { writer.Write(text); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        async Task Drain(StreamReader input, string stream)
        {
            var buffer = new char[4096];
            int count;
            while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                Write($"[{stream}] " + new string(buffer, 0, count));
        }
        var stdout = Drain(process.StandardOutput, "stdout");
        var stderr = Drain(process.StandardError, "stderr");
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var code = process.ExitCode;
            Write($"\n\nProcess exited · {DateTimeOffset.UtcNow:O} · Exit code: {code} (0x{unchecked((uint)code):X8})\n");
            return code;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Write("\nLog capture interrupted: " + ex.Message + "\n"); return -1; }
        finally
        {
            try { writer.Dispose(); } catch (IOException) { }
            process.Dispose();
        }
    }

    private static string PathFor(string directory, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("No saved launch log is available for this item.");
        var path = Path.Combine(directory, id + ".log");
        ImageFileSystem.EnsureNoLinks(path);
        return path;
    }
    public static bool Exists(string directory, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return false;
        try { return File.Exists(PathFor(directory, id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static string Read(string directory, string id)
    {
        using var input = new FileStream(PathFor(directory, id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int limit = 2 * 1024 * 1024;
        var truncated = input.Length > limit;
        if (truncated) input.Seek(-limit, SeekOrigin.End);
        var bytes = new byte[(int)Math.Min(input.Length, limit)];
        input.ReadExactly(bytes);
        return (truncated ? "Showing the last 2 MiB of this log. The complete log is saved on disk.\n\n" : "") + Encoding.UTF8.GetString(bytes);
    }
}
