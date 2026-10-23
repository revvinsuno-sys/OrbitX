using System.Text.Json;
using PS5CatalogBrowser.Core;

internal static class KytyArgumentTests
{
    public static Task RunAsync()
    {
        const string expected = "--screen-width 1280 --screen-height 720 --vblank-frequency 30 --present-mode Fifo --shader-optimization-type Performance --perf-overlay true";
        var settings = AppSettings.CreateDefault();
        Check(KytyArguments.Preview(KytyArguments.Build(settings.KytyArguments)) == expected, "new defaults and preview");
        Check(KytyArguments.Build(JsonSerializer.Deserialize<AppSettings>("{}")!.KytyArguments).Count == 0, "legacy settings retain emulator defaults");
        var options = settings.KytyArguments!;
        foreach (var mode in KytyArguments.PresentModes)
        { options.PresentMode = mode; Check(Value("--present-mode") == mode, "present mode mapping"); }
        foreach (var mode in KytyArguments.ShaderOptimizations)
        { options.ShaderOptimization = mode; Check(Value("--shader-optimization-type") == mode, "shader mapping"); }
        options.PerformanceOverlay = false; Check(Value("--perf-overlay") == "false", "overlay disabled");
        options.PerformanceOverlay = true; Check(Value("--perf-overlay") == "true", "overlay enabled");
        options.VblankFrequency = 60; Check(Value("--vblank-frequency") == "60", "vblank next launch");
        options.AdditionalArguments = "--user-name \"Player One\" --shader-log-folder \"C:\\logs with spaces\\\\\" --fullscreen";
        var tokens = KytyArguments.Build(options);
        Check(tokens.TakeLast(5).SequenceEqual(["--user-name", "Player One", "--shader-log-folder", "C:\\logs with spaces\\", "--fullscreen"]), "additional quoting");
        Check(KytyArguments.Parse(KytyArguments.Preview(tokens)).SequenceEqual(tokens), "preview round trip including trailing slashes");
        string[] quotedValues = ["", "plain", "two words", "embedded\"quote", "trailing slash \\", "\\\\server\\share with spaces\\"];
        Check(KytyArguments.Parse(KytyArguments.Preview(quotedValues)).SequenceEqual(quotedValues), "empty values, literal quotes and UNC paths round trip");
        Check(KytyArguments.Parse("\"a\"\"b\"").Single() == "a\"b", "consecutive quoted quotes follow Windows rules");
        foreach (var text in new[] { "--game other", "--game=other", "--screen-width 800", "--perf-overlay false", "--user-name \"unfinished" })
        { options.AdditionalArguments = text; Reject(() => KytyArguments.Build(options)); }
        options.AdditionalArguments = "";
        options.ScreenWidth = 0; Reject(() => KytyArguments.Build(options)); options.ScreenWidth = 1366;
        options.ScreenHeight = -1; Reject(() => KytyArguments.Build(options)); options.ScreenHeight = 768;
        options.VblankFrequency = 0; Reject(() => KytyArguments.Build(options)); options.VblankFrequency = 60;
        options.PresentMode = "fifo"; Reject(() => KytyArguments.Build(options)); options.PresentMode = "Fifo";
        options.ShaderOptimization = "Fast"; Reject(() => KytyArguments.Build(options)); options.ShaderOptimization = "Size";
        options.Enabled = false;
        foreach (var text in new[] { "--screen-width 1 --screen-width 2", "--screen-height nope", "--vblank-frequency -2", "--present-mode Bad", "--shader-optimization-type Fast", "--perf-overlay yes" })
        { options.AdditionalArguments = text; Reject(() => KytyArguments.Build(options)); }
        options.AdditionalArguments = "--screen-width 1600 --present-mode Mailbox";
        Check(KytyArguments.Build(options).SequenceEqual(KytyArguments.Parse(options.AdditionalArguments)), "manual managed arguments retain order and values when structured controls are disabled");
        options.Enabled = true; options.AdditionalArguments = "--user-name \"Player One\"";
        var root = Path.Combine(Path.GetTempPath(), "PS5Arguments-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            options.AmdCpuPatches = true; options.RedZoneProtection = true;
            options.ShaderCacheFolder = Path.Combine(root, "shader cache with spaces");
            var hardware = KytyArguments.Build(options);
            Check(hardware.Count(arg => arg == "--amd-cpu") == 1 && hardware.Count(arg => arg == "--redzone") == 1 && hardware.Contains(options.ShaderCacheFolder), "CPU switches and cache folder are constructed once");
            foreach (var duplicate in new[] { "--amd-cpu", "--redzone", "--shader-cache-folder \"C:\\cache\"" })
            { options.AdditionalArguments = duplicate; Reject(() => KytyArguments.Build(options)); }
            options.AdditionalArguments = "--user-name \"Player One\"";
            var validCache = options.ShaderCacheFolder;
            foreach (var invalid in new[] { "relative cache", "D:relative", "cache\0folder", Path.Combine(root, "cache*folder"), Path.Combine(root, "cache:folder") })
            { options.ShaderCacheFolder = invalid; Reject(() => KytyArguments.Build(options)); }
            options.ShaderCacheFolder = validCache;
            var manual = new KytyArgumentSettings { Enabled = false, AdditionalArguments = "--amd-cpu --redzone --shader-cache-folder \"C:\\manual cache\"" };
            Check(KytyArguments.Build(manual).SequenceEqual(KytyArguments.Parse(manual.AdditionalArguments)), "legacy manual CPU/cache flags remain effective until explicitly moved to controls");
            manual.AdditionalArguments = "--amd-cpu --amd-cpu"; Reject(() => KytyArguments.Build(manual));
            var store = new SettingsStore(root); store.Save(settings);
            var restored = store.Load(); var saved = restored.KytyArguments!;
            Check(saved.ScreenWidth == 1366 && saved.ScreenHeight == 768 && saved.VblankFrequency == 60 && saved.AdditionalArguments == options.AdditionalArguments, "custom values and additional arguments survive restart");
            Check(saved.AmdCpuPatches && saved.RedZoneProtection && saved.ShaderCacheFolder == validCache && !Directory.Exists(validCache), "CPU/cache settings persist without creating or relocating cache content");
            Check(restored.Downloads.Count == 0 && restored.Library.Count == 0 && restored.CompatibilityOverrides.Count == 0, "argument settings do not create records");
            var executable = Path.Combine(root, "kyty_emulator.exe"); File.WriteAllText(executable, "fixture");
            var game = Path.Combine(root, "game with spaces"); Directory.CreateDirectory(game); File.WriteAllText(Path.Combine(game, "eboot.bin"), "fixture");
            var info = KytyLauncher.BuildStartInfo(executable, game, arguments: saved);
            Check(info.ArgumentList.SequenceEqual(new[] { "--game", game }.Concat(KytyArguments.Build(saved))) && info.ArgumentList.Count(value => value == "--game") == 1, "authoritative process arguments and exactly one game path");
            Check(info.WorkingDirectory == root && !info.UseShellExecute && info.Arguments == "", "direct process launch policy");
            try { KytyLauncher.BuildStartInfo(Path.Combine(root, "missing.exe"), game, arguments: saved); throw new Exception("Missing executable accepted"); }
            catch (FileNotFoundException) { }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
        string Value(string flag) { var args = KytyArguments.Build(options).ToList(); return args[args.IndexOf(flag) + 1]; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException ex) { Check(ex.Message.Length > 15, "actionable validation error"); return; } throw new Exception("Invalid arguments accepted"); }
}
