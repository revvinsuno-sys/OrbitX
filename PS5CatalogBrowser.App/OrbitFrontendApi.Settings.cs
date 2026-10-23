using System.Globalization;
using System.Text.Json;
using PS5CatalogBrowser.Core;

namespace PS5CatalogBrowser.App;

internal sealed partial class OrbitFrontendApi
{
    internal event Action<OrbitSettingsAction>? SettingsActionRequested;
    internal Func<bool>? SourceEditorAvailable;

    private object SettingsState() => new
    {
        fields = new Dictionary<string, object> {
            ["StartupFullscreen"] = model.StartupFullscreen,
            ["DownloadRoot"] = model.DownloadRoot, ["DownloadBandwidthMBps"] = model.DownloadBandwidthMBps,
            ["OSFMountPath"] = model.OSFMountPath, ["InstalledGamesRoot"] = model.InstalledGamesRoot, ["RetainOriginalImage"] = model.RetainOriginalImage,
            ["SharpEmuPath"] = model.SharpEmuPath, ["SharpEmuFullscreen"] = model.SharpEmuFullscreen, ["EmulatorPath"] = model.EmulatorPath,
            ["ShadPS4Path"] = model.ShadPS4Path, ["ShadPS4Fullscreen"] = model.ShadPS4Fullscreen,
            ["ArgumentFullscreen"] = model.ArgumentFullscreen, ["PerformanceArgumentsEnabled"] = model.PerformanceArgumentsEnabled,
            ["ArgumentWidth"] = model.ArgumentWidth, ["ArgumentHeight"] = model.ArgumentHeight, ["ArgumentVblank"] = model.ArgumentVblank,
            ["ArgumentPresentMode"] = model.ArgumentPresentMode, ["ArgumentShaderOptimization"] = model.ArgumentShaderOptimization,
            ["ArgumentPerformanceOverlay"] = model.ArgumentPerformanceOverlay, ["ArgumentAmdCpuPatches"] = model.ArgumentAmdCpuPatches,
            ["ArgumentRedZoneProtection"] = model.ArgumentRedZoneProtection, ["ArgumentShaderCacheFolder"] = model.ArgumentShaderCacheFolder,
            ["AdditionalArguments"] = model.AdditionalArguments
        },
        facts = new Dictionary<string, string> {
            ["MetadataCredentials"] = model.Settings.IgdbClientId.Length > 0 && model.Settings.IgdbClientSecretProtected.Length > 0 ? "Configured" : "Not configured",
            ["InstallationAvailability"] = model.InstallationAvailability, ["EmulatorAvailability"] = model.EmulatorAvailability,
            ["SharpEmuAvailability"] = model.SharpEmuAvailability, ["SharpEmuOwnership"] = model.SharpEmuOwnership,
            ["ShadPS4Availability"] = model.ShadPS4Availability,
            ["SharpEmuInstalledVersion"] = model.SharpEmuInstalledVersion, ["SharpEmuInstalledRevision"] = model.SharpEmuInstalledRevision,
            ["SharpEmuAvailableRevision"] = model.SharpEmuAvailableRevision, ["SharpEmuInstallDirectory"] = model.SharpEmuInstallDirectory,
            ["SharpEmuCheckTime"] = model.SharpEmuCheckTime, ["SharpEmuUpdateTime"] = model.SharpEmuUpdateTime,
            ["SharpEmuF10Support"] = model.SharpEmuF10Support, ["SharpEmuUpdateStatus"] = model.SharpEmuUpdateStatus,
            ["SharpEmuUpdateOutput"] = model.SharpEmuUpdateOutput, ["InstalledKytyVersion"] = model.InstalledKytyVersion,
            ["RepositoryKytyVersion"] = model.RepositoryKytyVersion, ["KytyUpdateStatus"] = model.KytyUpdateStatus,
            ["KytyUpdateOutput"] = model.KytyUpdateOutput, ["ArgumentsPreview"] = model.ArgumentsPreview,
            ["ArgumentsValidation"] = model.ArgumentsValidation, ["ArgumentsSaveState"] = model.ArgumentsSaveState,
            ["AboutBuildVersion"] = model.AboutBuildVersion, ["AboutLastUpdated"] = model.AboutLastUpdated
        },
        model.Status, actions = SettingsActions, liveSourceUrl = LiveOrbitCatalog.OfficialUrl,
        passwords = model.Settings.ArchivePasswords.Select((_, index) => new { index }).ToArray(),
        sources = model.Sources.Select(source => new { source.Id, source.Name, type = (int)source.SourceType, source.Url,
            file = source.EncryptedFilePath, key = source.KeyFilePath, source.Enabled, source.AutoRefresh,
            fallback = source.LocalFallbackSourceId, source.Format, source.GameCount, source.ReleaseCount, source.LastError, source.LastSuccessfulRefresh }).ToArray(),
        keys = model.ManagedOrbitKeys.Select(key => new { key.Description, path = key.FilePath }).ToArray(),
        presentModes = model.PresentModeChoices, shaderOptimizations = model.ShaderOptimizationChoices
    };

    internal string[] SettingsActions => new[] {
        "chooseDownload", "openDownloads", "chooseInstalled", "chooseOSF", "detectOSF", "testOSF", "getOSF", "clearBrowser", "refreshCatalogues",
        model.CanEditKytyPath ? "chooseEmulator" : null, "testEmulator", model.CanCheckKytyVersion ? "checkKyty" : null,
        model.CanUpdateKyty ? "updateKyty" : null, "chooseCache", "applyArguments",
        model.CanManageSharpEmu ? "chooseSharp" : null, model.CanManageSharpEmu ? "checkSharp" : null,
        model.CanBuildSharpEmu ? "updateSharp" : null, "sharpLog",
        model.CanManageShadPS4 ? "chooseShadPS4" : null, model.CanManageShadPS4 ? "testShadPS4" : null,
        CanEditSource ? "sourceAdd" : null, CanEditSource ? "sourceSave" : null,
        CanEditSource ? "sourceRemove" : null, CanEditSource ? "sourceTest" : null,
        CanEditSource ? "sourceRefresh" : null, CanEditSource ? "sourceFile" : null,
        CanEditSource ? "sourceKey" : null, CanEditSource ? "sourceDownloadKey" : null,
        model.IsFetchingOrbitKey ? "sourceCancelKey" : null
    }.OfType<string>().ToArray();
    private bool CanEditSource => !model.IsFetchingOrbitKey && (SourceEditorAvailable?.Invoke() ?? true);

    private (int Status, object Body) SettingsRequest(string? body)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? "{}", new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind)) throw new ArgumentException("Invalid settings request.");
            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            var allowed = kind.GetString() switch {
                "field" => new[] { "kind", "name", "value" },
                "password" => new[] { "kind", "operation", "index", "count", "value" },
                "action" => new[] { "kind", "action", "source" },
                _ => []
            };
            if (properties.Distinct().Count() != properties.Length || properties.Any(name => !allowed.Contains(name))) throw new ArgumentException("Unexpected settings arguments.");
            string Text(string name, int limit = 4096)
            {
                var value = root.GetProperty(name).GetString() ?? "";
                if (value.Length > limit || value.Contains('\0')) throw new ArgumentException("Settings value is too long or invalid.");
                return value;
            }
            if (kind.GetString() == "field")
            {
                var name = Text("name", 64); var value = root.GetProperty("value");
                string String() => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 4096 } text && !text.Contains('\0') ? text : throw new ArgumentException("Invalid text setting.");
                bool Bool() => value.GetBoolean();
                switch (name)
                {
                    case "StartupFullscreen": model.StartupFullscreen = Bool(); break;
                    case "DownloadRoot": model.DownloadRoot = String(); break;
                    case "DownloadBandwidthMBps": model.DownloadBandwidthMBps = double.Parse(String(), CultureInfo.InvariantCulture); break;
                    case "OSFMountPath": model.OSFMountPath = String(); break;
                    case "InstalledGamesRoot": model.InstalledGamesRoot = String(); break;
                    case "RetainOriginalImage": model.RetainOriginalImage = Bool(); break;
                    case "SharpEmuPath": model.SharpEmuPath = String(); break;
                    case "SharpEmuFullscreen": model.SharpEmuFullscreen = Bool(); break;
                    case "EmulatorPath": model.EmulatorPath = String(); break;
                    case "ShadPS4Path": model.ShadPS4Path = String(); break;
                    case "ShadPS4Fullscreen": model.ShadPS4Fullscreen = Bool(); break;
                    case "ArgumentFullscreen": model.ArgumentFullscreen = Bool(); break;
                    case "PerformanceArgumentsEnabled": model.PerformanceArgumentsEnabled = Bool(); break;
                    case "ArgumentWidth": model.ArgumentWidth = String(); break;
                    case "ArgumentHeight": model.ArgumentHeight = String(); break;
                    case "ArgumentVblank": model.ArgumentVblank = String(); break;
                    case "ArgumentPresentMode": model.ArgumentPresentMode = String(); break;
                    case "ArgumentShaderOptimization": model.ArgumentShaderOptimization = String(); break;
                    case "ArgumentPerformanceOverlay": model.ArgumentPerformanceOverlay = Bool(); break;
                    case "ArgumentAmdCpuPatches": model.ArgumentAmdCpuPatches = Bool(); break;
                    case "ArgumentRedZoneProtection": model.ArgumentRedZoneProtection = Bool(); break;
                    case "ArgumentShaderCacheFolder": model.ArgumentShaderCacheFolder = String(); break;
                    case "AdditionalArguments": model.AdditionalArguments = String(); break;
                    default: throw new ArgumentException("Unknown settings field.");
                }
            }
            else if (kind.GetString() == "password")
            {
                var operation = Text("operation", 16); var index = root.GetProperty("index").GetInt32();
                var passwords = model.Settings.ArchivePasswords.ToList();
                // Count fences stale masked rows; existing password bytes never enter the WebView.
                if (root.GetProperty("count").GetInt32() != passwords.Count || index < 0 || index > passwords.Count) throw new ArgumentException("Password list changed. Refresh and try again.");
                if (operation == "add" && index == passwords.Count) passwords.Add(Text("value", 1024));
                else if (operation == "edit" && index < passwords.Count) passwords[index] = Text("value", 1024);
                else if (operation == "remove" && index < passwords.Count) passwords.RemoveAt(index);
                else throw new ArgumentException("Invalid password action.");
                if (passwords.Any(password => password.Contains('\r') || password.Contains('\n'))) throw new ArgumentException("Enter one password per field.");
                model.ArchivePasswordsText = string.Join(Environment.NewLine, passwords);
            }
            else if (kind.GetString() == "action")
            {
                var action = Text("action", 64);
                if (!SettingsActions.Contains(action)) return (409, new { error = "This settings action is unavailable. Wait for the current operation to finish." });
                if (action.StartsWith("source", StringComparison.Ordinal) && action != "sourceCancelKey")
                {
                    var draft = root.GetProperty("source");
                    if (draft.ValueKind != JsonValueKind.Object) throw new ArgumentException("A source draft is required.");
                    foreach (var name in new[] { "id", "name", "url", "file", "key", "fallback" })
                        if (draft.GetProperty(name).ValueKind != JsonValueKind.String || draft.GetProperty(name).GetString() is not { Length: <= 4096 } text || text.Contains('\0')) throw new ArgumentException("Invalid source field.");
                    if (draft.GetProperty("type").GetInt32() is < 0 or > 2) throw new ArgumentException("Invalid source type.");
                    _ = draft.GetProperty("enabled").GetBoolean(); _ = draft.GetProperty("autoRefresh").GetBoolean();
                    var id = draft.GetProperty("id").GetString();
                    if (!string.IsNullOrEmpty(id) && !model.Sources.Any(source => source.Id == id)) return (409, new { error = "That source is no longer available." });
                    if (action is "sourceSave" or "sourceRemove" or "sourceTest" or "sourceRefresh" && string.IsNullOrEmpty(id)) throw new ArgumentException("Select a source.");
                }
                SettingsActionRequested?.Invoke(new(action, root.Clone()));
                return (202, new { accepted = true });
            }
            else throw new ArgumentException("Unknown settings request.");
            return (200, SettingsState());
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return (400, new { error = ex.Message }); }
    }
}

internal sealed record OrbitSettingsAction(string Action, JsonElement Payload);
