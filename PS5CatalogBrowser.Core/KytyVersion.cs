using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public sealed record CompatibilityTestVersion(string Version, DateTimeOffset RecordedAt);
public sealed record KytyCommunityReport(string Status, string TestedVersion, string Os, string TestedDate);

public static class KytyVersion
{
    private static readonly Regex Build = new(@"KytyPS5-\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}(?:-\d+-g[0-9a-f]{7,40})?(?:-dirty)?(?![0-9A-Za-z-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BuildDate = new(@"KytyPS5-(\d{4}-\d{2}-\d{2})-", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    public static bool IsReleaseTag(string value) => Regex.IsMatch(value, @"^KytyPS5-\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}$", RegexOptions.CultureInvariant);
    public static bool MatchesRelease(string installed, string release) => IsReleaseTag(release) &&
        string.Equals(installed.EndsWith("-dirty", StringComparison.Ordinal) ? installed[..^6] : installed, release, StringComparison.OrdinalIgnoreCase);

    // Read metadata only: checking the configured build must never start the emulator.
    public static string Read(string path)
    {
        if (!Path.GetFileName(path).Equals("kyty_emulator.exe", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 256 * 1024 * 1024) return "";
            var buffer = new byte[65536]; var tail = ""; var builds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                var text = tail + Encoding.ASCII.GetString(buffer, 0, count);
                foreach (Match match in Build.Matches(text))
                    if (match.Index + match.Length < text.Length || input.Position == input.Length) builds.Add(match.Value);
                tail = text[^Math.Min(192, text.Length)..];
            }
            if (builds.Count == 1) return builds.Single();
            if (builds.Count > 1) return ""; // Ambiguous embedded identities cannot establish a tested build.
            var info = FileVersionInfo.GetVersionInfo(path);
            return new[] { info.ProductVersion, info.FileVersion }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "0.0.0.0")?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException) { return ""; }
    }

    public static string Comparison(string tested, string current, string status)
    {
        if (status == "untested" || string.IsNullOrWhiteSpace(tested) || string.IsNullOrWhiteSpace(current)) return "unknown";
        var testedDate = BuildDate.Match(tested); var currentDate = BuildDate.Match(current);
        if (testedDate.Success && currentDate.Success && DateOnly.TryParse(testedDate.Groups[1].Value, out var testedDay) && DateOnly.TryParse(currentDate.Groups[1].Value, out var currentDay))
        {
            if (testedDay < currentDay) return "older";
            if (testedDay > currentDay) return "newer";
        }
        var testedBuild = Build.Match(tested); var currentBuild = Build.Match(current);
        if (testedBuild.Success != currentBuild.Success) return "unknown";
        return string.Equals(testedBuild.Success ? testedBuild.Value : tested.Trim(), currentBuild.Success ? currentBuild.Value : current.Trim(), StringComparison.OrdinalIgnoreCase)
            ? "current" : "different";
    }
}
