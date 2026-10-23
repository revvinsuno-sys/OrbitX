using System.Globalization;
using System.Runtime.CompilerServices;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

public sealed partial class MainViewModel
{
    private KytyArgumentSettings? argumentDraft;
    private KytyArgumentSettings ArgumentDraft => argumentDraft ??= Settings.KytyArguments is { } saved
        ? System.Text.Json.JsonSerializer.Deserialize<KytyArgumentSettings>(System.Text.Json.JsonSerializer.Serialize(saved))!
        : new() { Enabled = false };
    private string? argumentWidth, argumentHeight, argumentVblank;
    private bool argumentsEdited;
    public string[] PresentModeChoices => KytyArguments.PresentModes;
    public string[] ShaderOptimizationChoices => KytyArguments.ShaderOptimizations;
    public bool PerformanceArgumentsEnabled { get => ArgumentDraft.Enabled; set { ArgumentDraft.Enabled = value; ArgumentsEdited(); } }
    public bool ArgumentFullscreen { get => ArgumentDraft.Fullscreen; set { ArgumentDraft.Fullscreen = value; ArgumentsEdited(); } }
    public string ArgumentWidth { get => argumentWidth ??= ArgumentDraft.ScreenWidth.ToString(CultureInfo.InvariantCulture); set { argumentWidth = value; ArgumentsEdited(); } }
    public string ArgumentHeight { get => argumentHeight ??= ArgumentDraft.ScreenHeight.ToString(CultureInfo.InvariantCulture); set { argumentHeight = value; ArgumentsEdited(); } }
    public string ArgumentVblank { get => argumentVblank ??= ArgumentDraft.VblankFrequency.ToString(CultureInfo.InvariantCulture); set { argumentVblank = value; ArgumentsEdited(); } }
    public string ArgumentPresentMode { get => ArgumentDraft.PresentMode; set { ArgumentDraft.PresentMode = value; ArgumentsEdited(); } }
    public string ArgumentShaderOptimization { get => ArgumentDraft.ShaderOptimization; set { ArgumentDraft.ShaderOptimization = value; ArgumentsEdited(); } }
    public bool ArgumentPerformanceOverlay { get => ArgumentDraft.PerformanceOverlay; set { ArgumentDraft.PerformanceOverlay = value; ArgumentsEdited(); } }
    public string AdditionalArguments { get => ArgumentDraft.AdditionalArguments; set { ArgumentDraft.AdditionalArguments = value; ArgumentsEdited(); } }
    public bool ArgumentAmdCpuPatches { get => ArgumentDraft.AmdCpuPatches; set { ArgumentDraft.AmdCpuPatches = value; ArgumentsEdited(); } }
    public bool ArgumentRedZoneProtection { get => ArgumentDraft.RedZoneProtection; set { ArgumentDraft.RedZoneProtection = value; ArgumentsEdited(); } }
    public string ArgumentShaderCacheFolder { get => ArgumentDraft.ShaderCacheFolder; set { ArgumentDraft.ShaderCacheFolder = value; ArgumentsEdited(); } }
    public string ArgumentsSaveState => argumentsEdited ? "Unsaved changes — Apply arguments to use them on the next launch." : "Saved. Applies to the next launch.";
    public string ArgumentsValidation
    {
        get { try { KytyArguments.Build(ArgumentCandidate()); return ""; } catch (ArgumentException ex) { return ex.Message; } }
    }
    public string ArgumentsPreview
    {
        get { try { return KytyArguments.Preview(KytyArguments.Build(ArgumentCandidate())); } catch (ArgumentException) { return "Resolve the validation error to preview these options."; } }
    }
    private KytyArgumentSettings ArgumentCandidate()
    {
        int Number(string text, string label, int previous)
        {
            if (!ArgumentDraft.Enabled) return previous;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw new ArgumentException($"{label} must be a positive whole number.");
            return value;
        }
        return new()
        {
            Enabled = ArgumentDraft.Enabled,
            Fullscreen = ArgumentDraft.Fullscreen,
            ScreenWidth = Number(ArgumentWidth, "Screen width", ArgumentDraft.ScreenWidth),
            ScreenHeight = Number(ArgumentHeight, "Screen height", ArgumentDraft.ScreenHeight),
            VblankFrequency = Number(ArgumentVblank, "Vblank frequency", ArgumentDraft.VblankFrequency),
            PresentMode = ArgumentDraft.PresentMode, ShaderOptimization = ArgumentDraft.ShaderOptimization,
            PerformanceOverlay = ArgumentDraft.PerformanceOverlay, AdditionalArguments = ArgumentDraft.AdditionalArguments,
            AmdCpuPatches = ArgumentDraft.AmdCpuPatches, RedZoneProtection = ArgumentDraft.RedZoneProtection, ShaderCacheFolder = ArgumentDraft.ShaderCacheFolder
        };
    }
    private void ArgumentsEdited([CallerMemberName] string? property = null)
    {
        argumentsEdited = true; Changed(property); Changed(nameof(ArgumentsPreview)); Changed(nameof(ArgumentsValidation)); Changed(nameof(ArgumentsSaveState));
    }
    public void ApplyArguments()
    {
        var candidate = ArgumentCandidate(); KytyArguments.Build(candidate);
        var previous = Settings.KytyArguments; Settings.KytyArguments = candidate;
        try { Save(); } catch { Settings.KytyArguments = previous; throw; }
        argumentsEdited = false; Changed(nameof(ArgumentsSaveState));
        Status = "KytyPS5 arguments saved for the next launch.";
    }
}
