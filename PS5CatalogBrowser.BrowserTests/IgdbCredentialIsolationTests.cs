using System.IO;
using System.Text;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class IgdbCredentialIsolationTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5IgdbIsolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fresh = new SettingsStore(Path.Combine(root, "fresh"));
            fresh.Save(new() { Sources = [] });
            using (var model = new MainViewModel(fresh))
                Check(model.Settings.IgdbClientId == "" && model.Settings.IgdbClientSecretProtected == "", "new settings have no implicit credentials");

            var imported = new SettingsStore(Path.Combine(root, "imported"));
            imported.Save(new() { Sources = [], IgdbClientId = "synthetic-imported-id", IgdbClientSecretProtected = "synthetic-imported-secret", IgdbCredentialsSeededFromShared = true });
            using (var model = new MainViewModel(imported))
            {
                Check(model.Settings.IgdbClientId == "" && model.Settings.IgdbClientSecretProtected == "" && !model.Settings.IgdbCredentialsSeededFromShared,
                    "previous implicit credentials are erased before use");
                using var api = new OrbitFrontendApi(model);
                var response = api.Respond(OrbitFrontendSecurity.Document, "GET", OrbitFrontendSecurity.Origin + "/api/v1/windows/igdb-settings");
                var json = Encoding.UTF8.GetString(api.SerializeResponse(response.Body));
                Check(response.Status == 200 && json.Contains("\"hasClientSecret\":false") && !json.Contains("seededFromShared"), "credential state has no implicit import capability");
            }
            Check(imported.Load().IgdbClientId == "" && imported.Load().IgdbClientSecretProtected == "" &&
                !File.ReadAllText(imported.FilePath).Contains(nameof(AppSettings.IgdbCredentialsSeededFromShared)), "erasure persists without the old import marker");
            using (var restarted = new MainViewModel(imported))
                Check(restarted.Settings.IgdbClientId == "" && restarted.Settings.IgdbClientSecretProtected == "", "restart leaves erased credentials empty");

            var manual = new SettingsStore(Path.Combine(root, "manual"));
            manual.Save(new() { Sources = [] });
            string protectedSecret;
            using (var model = new MainViewModel(manual))
            {
                model.SaveIgdbCredentials("orbitx-synthetic-client", "orbitx-synthetic-secret");
                protectedSecret = model.Settings.IgdbClientSecretProtected;
                Check(protectedSecret.Length > 0 && !protectedSecret.Contains("orbitx-synthetic-secret"), "explicit credentials use protection");
            }
            using (var restarted = new MainViewModel(manual))
                Check(restarted.Settings.IgdbClientId == "orbitx-synthetic-client" && restarted.Settings.IgdbClientSecretProtected == protectedSecret,
                    "explicit OrbitX credentials survive restart unchanged");
            Console.WriteLine("PASS IGDB isolation: empty startup, removal/persistence of implicit credentials, no import API and protected explicit credentials");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
