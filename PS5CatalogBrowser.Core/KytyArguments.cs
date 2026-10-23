using System.Globalization;
using System.Text;

namespace PS5CatalogBrowser.Core;

public sealed class KytyArgumentSettings
{
    public bool Enabled { get; set; } = true;
    public bool Fullscreen { get; set; }
    public int ScreenWidth { get; set; } = 1280;
    public int ScreenHeight { get; set; } = 720;
    public int VblankFrequency { get; set; } = 30;
    public string PresentMode { get; set; } = "Fifo";
    public string ShaderOptimization { get; set; } = "Performance";
    public bool PerformanceOverlay { get; set; } = true;
    public string AdditionalArguments { get; set; } = "";
    public bool AmdCpuPatches { get; set; }
    public bool RedZoneProtection { get; set; }
    public string ShaderCacheFolder { get; set; } = "";
}

public static class KytyArguments
{
    public static readonly string[] PresentModes = ["Fifo", "Mailbox", "Immediate"];
    public static readonly string[] ShaderOptimizations = ["None", "Size", "Performance"];
    private static readonly string[] Managed = ["--screen-width", "--screen-height", "--vblank-frequency", "--present-mode", "--shader-optimization-type", "--perf-overlay"];

    public static IReadOnlyList<string> Build(KytyArgumentSettings? settings)
    {
        // Missing settings means an existing configuration: preserve its emulator defaults.
        if (settings is null) return [];
        var result = new List<string>();
        if (settings.Enabled)
        {
            Positive(settings.ScreenWidth, "Screen width"); Positive(settings.ScreenHeight, "Screen height"); Positive(settings.VblankFrequency, "Vblank frequency");
            Choice(settings.PresentMode, PresentModes, "Present mode"); Choice(settings.ShaderOptimization, ShaderOptimizations, "Shader optimisation");
            result.AddRange(["--screen-width", settings.ScreenWidth.ToString(CultureInfo.InvariantCulture), "--screen-height", settings.ScreenHeight.ToString(CultureInfo.InvariantCulture),
                "--vblank-frequency", settings.VblankFrequency.ToString(CultureInfo.InvariantCulture), "--present-mode", settings.PresentMode,
                "--shader-optimization-type", settings.ShaderOptimization, "--perf-overlay", settings.PerformanceOverlay ? "true" : "false"]);
        }
        if (settings.Fullscreen) result.Add("--fullscreen");
        var additional = Parse(settings.AdditionalArguments);
        if (settings.Fullscreen && additional.Contains("--fullscreen", StringComparer.Ordinal))
            throw new ArgumentException("Remove duplicate --fullscreen from Additional Arguments or clear the Start full screen setting.");
        if (settings.AmdCpuPatches) result.Add("--amd-cpu");
        if (settings.RedZoneProtection) result.Add("--redzone");
        if (settings.ShaderCacheFolder.Length > 0) result.AddRange(["--shader-cache-folder", CacheFolder(settings.ShaderCacheFolder)]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < additional.Count; i++)
        {
            var flag = additional[i].Split('=', 2)[0];
            if (flag == "--game") throw new ArgumentException("Remove --game from Additional Arguments. The application supplies the selected game path.");
            if (flag is "--amd-cpu" or "--redzone" or "--shader-cache-folder")
            {
                if (result.Contains(flag) || !seen.Add(flag)) throw new ArgumentException($"Remove duplicate {flag} from Additional Arguments or clear its control above.");
                if (additional[i] != flag) throw new ArgumentException($"Use {flag} without an equals sign.");
                if (flag == "--shader-cache-folder")
                {
                    if (++i >= additional.Count) throw new ArgumentException("--shader-cache-folder requires an absolute folder path.");
                    CacheFolder(additional[i]);
                }
                continue;
            }
            if (!Managed.Contains(flag)) continue;
            if (settings.Enabled || !seen.Add(flag)) throw new ArgumentException($"Remove duplicate {flag} from Additional Arguments or disable structured performance settings.");
            if (flag != additional[i] || ++i >= additional.Count) throw new ArgumentException($"Use {flag} followed by a value separated by a space.");
            var value = additional[i];
            if (flag is "--screen-width" or "--screen-height" or "--vblank-frequency")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new ArgumentException($"{flag} requires a positive whole number.");
                Positive(number, flag);
            }
            else if (flag == "--present-mode") Choice(value, PresentModes, flag);
            else if (flag == "--shader-optimization-type") Choice(value, ShaderOptimizations, flag);
            else if (value is not ("true" or "false")) throw new ArgumentException("--perf-overlay requires true or false.");
        }
        result.AddRange(additional);
        return result;
    }

    private static void Positive(int value, string name)
    { if (value <= 0) throw new ArgumentException($"{name} must be a positive whole number."); }
    private static void Choice(string value, string[] choices, string name)
    { if (!choices.Contains(value)) throw new ArgumentException($"{name}: choose {string.Join(", ", choices)}."); }
    private static string CacheFolder(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || File.Exists(path)) throw new ArgumentException();
            var fullPath = Path.GetFullPath(path);
            var parts = fullPath[Path.GetPathRoot(fullPath)!.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new ArgumentException();
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Shader cache folder must be an absolute directory path, such as D:\\KytyShaderCache. Leave it empty for the emulator default."); }
    }

    // Windows command-line quoting rules, without shell expansion or execution.
    public static IReadOnlyList<string> Parse(string text)
    {
        if (text is null || text.IndexOf('\0') >= 0) throw new ArgumentException("Additional Arguments cannot contain a null character.");
        var result = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index == text.Length) break;
            var token = new StringBuilder(); var quoted = false;
            while (index < text.Length && (quoted || !char.IsWhiteSpace(text[index])))
            {
                var slashes = 0;
                while (index < text.Length && text[index] == '\\') { slashes++; index++; }
                if (index < text.Length && text[index] == '"')
                {
                    token.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) token.Append('"');
                    else if (quoted && index + 1 < text.Length && text[index + 1] == '"') { token.Append('"'); index++; }
                    else quoted = !quoted;
                    index++;
                }
                else
                {
                    token.Append('\\', slashes);
                    if (index < text.Length && (quoted || !char.IsWhiteSpace(text[index]))) token.Append(text[index++]);
                }
            }
            if (quoted) throw new ArgumentException("Additional Arguments has an unclosed double quote. Quote paths containing spaces with matching double quotes.");
            result.Add(token.ToString());
        }
        return result;
    }

    public static string Preview(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));
    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
