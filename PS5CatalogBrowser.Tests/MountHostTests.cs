using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class MountHostTests
{
    public static async Task RunAsync()
    {
        var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PS5CatalogBrowser.App", "bin", "Release", "net10.0-windows", "PS5CatalogBrowser.MountHost.exe"));
        if (!File.Exists(helper)) { Console.WriteLine("SKIP mount helper IPC tests: build the complete Release solution first."); return; }
        using var fixture = new InstallationTests.Fixture();
        var original = File.ReadAllBytes(fixture.Source);
        foreach (var validParent in new[] { true, false })
        {
            var name = "PS5CatalogMount-" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = Process.Start(new ProcessStartInfo(helper)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--mount-session", name, (validParent ? Environment.ProcessId : 0).ToString() }
            }) ?? throw new Exception("Mount helper did not start.");
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (validParent)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                // A missing dependency proves the real helper's failure response without elevation or mounting anything.
                await writer.WriteLineAsync(JsonSerializer.Serialize(new MountRequest(Path.Combine(fixture.Root, "OSFMount.com"), fixture.Source)));
                var response = JsonSerializer.Deserialize<MountResponse>(await reader.ReadLineAsync(timeout.Token) ?? throw new Exception("Helper failed to return its dependency error."));
                if (response is not { Success: false, Root: "" } || response.Error.Length == 0) throw new Exception("Unsafe helper failure result.");
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != (validParent ? 1 : 3)) throw new Exception("Helper did not reject invalid dependency/parent identity.");
            if (!File.ReadAllBytes(fixture.Source).SequenceEqual(original)) throw new Exception("Helper failure modified the source.");
        }
        Console.WriteLine("PASS real mount helper IPC dependency failure, rejected parent identity and byte-identical source preservation (no elevation/mount)");
    }
}
