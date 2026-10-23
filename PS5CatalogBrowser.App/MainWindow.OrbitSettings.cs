using System.Text.Json;

namespace PS5CatalogBrowser.App;

public partial class MainWindow
{
    // The graphical page shares the existing native picker/confirmation and operation handlers.
    // Only the source editor needs an explicit draft transfer; all bound settings use the model.
    private void RunOrbitSettingsAction(OrbitSettingsAction request) => Run(() =>
    {
        if (request.Action.StartsWith("source", StringComparison.Ordinal) && request.Action != "sourceCancelKey")
        {
            if (!SourceEditorPanel.IsEnabled) throw new InvalidOperationException("Wait for the source operation to finish.");
            var draft = request.Payload.GetProperty("source");
            string Text(string name) => draft.GetProperty(name).GetString() is { Length: <= 4096 } text && !text.Contains('\0') ? text : throw new ArgumentException("Invalid source field.");
            var id = Text("id");
            model.SelectedSource = id.Length == 0 ? null : model.Sources.Single(source => source.Id == id);
            var type = draft.GetProperty("type").GetInt32();
            if (type is < 0 or > 2) throw new ArgumentException("Invalid source type.");
            SourceTypeBox.SelectedIndex = type;
            SourceNameBox.Text = Text("name"); SourceUrlBox.Text = Text("url"); SourceFileBox.Text = Text("file"); SourceKeyBox.Text = Text("key");
            SourceEnabledBox.IsChecked = draft.GetProperty("enabled").GetBoolean();
            SourceAutoRefreshBox.IsChecked = draft.GetProperty("autoRefresh").GetBoolean();
            SourceFallbackBox.SelectedValue = Text("fallback");
        }
        switch (request.Action)
        {
            case "chooseDownload": ChooseDownloadRoot_Click(this, new()); break;
            case "openDownloads": OpenDownloadsFolder_Click(this, new()); break;
            case "chooseInstalled": ChooseInstalledRoot_Click(this, new()); break;
            case "chooseOSF": ChooseOSFMount_Click(this, new()); break;
            case "detectOSF": DetectOSFMount_Click(this, new()); break;
            case "testOSF": TestOSFMount_Click(this, new()); break;
            case "getOSF": InstallOSFMount_Click(this, new()); break;
            case "clearBrowser": ClearBrowserData_Click(this, new()); break;
            case "refreshCatalogues": RefreshAll_Click(this, new()); break;
            case "chooseEmulator": ChooseEmulator_Click(this, new()); break;
            case "testEmulator": TestEmulator_Click(this, new()); break;
            case "checkKyty": CheckKytyVersion_Click(this, new()); break;
            case "updateKyty": UpdateKyty_Click(this, new()); break;
            case "chooseCache": ChooseShaderCacheFolder_Click(this, new()); break;
            case "applyArguments": ApplyArguments_Click(this, new()); break;
            case "chooseSharp": ChooseSharpEmu_Click(this, new()); break;
            case "chooseShadPS4": ChooseShadPS4_Click(this, new()); break;
            case "testShadPS4": TestShadPS4_Click(this, new()); break;
            case "checkSharp": CheckSharpEmu_Click(this, new()); break;
            case "updateSharp": UpdateSharpEmu_Click(this, new()); break;
            case "sharpLog": SharpEmuLog_Click(this, new()); break;
            case "sourceAdd": AddSource_Click(this, new()); break;
            case "sourceSave": SaveSource_Click(this, new()); break;
            case "sourceRemove": RemoveSource_Click(this, new()); break;
            case "sourceTest": TestSource_Click(this, new()); break;
            case "sourceRefresh": RefreshSource_Click(this, new()); break;
            case "sourceFile": ChooseSourceFile_Click(this, new()); PublishSourcePicker(); break;
            case "sourceKey": ChooseSourceKey_Click(this, new()); break;
            case "sourceDownloadKey": DownloadSourceKey_Click(this, new()); break;
            case "sourceCancelKey": CancelSourceKey_Click(this, new()); break;
            default: throw new ArgumentException("Unknown settings action.");
        }
    });

    private void PublishSourcePicker() => orbitFrontend?.Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
    { command = "settingsSourcePicker", file = SourceFileBox.Text, key = SourceKeyBox.Text }));
}
