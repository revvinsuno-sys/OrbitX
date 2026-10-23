using System.IO;
using System.Text.Json;
using System.Windows;
using PS5CatalogBrowser.App;
using PS5CatalogBrowser.Core;

internal static class InstalledIdentityTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5InstalledIdentity-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed");
        var folder = Path.Combine(installed, "unconfirmed-archive");
        var game = Path.Combine(folder, "nested", "game");
        var metadata = Path.Combine(game, "sce_sys", "param.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllText(Path.Combine(game, "eboot.bin"), "synthetic fixture; never executed");
        File.WriteAllText(metadata, """{"titleId":"PPSA06438","localizedParameters":{"defaultLanguage":"fr-FR","en-US":{"titleName":"English title"},"fr-FR":{"titleName":"Inspected title"}}}""");
        var item = new LocalLibraryEntry { Title = "unconfirmed-archive.rar", ContentPath = folder };
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(new() { Sources = [new() { Id = "fixture", Name = "Identity fixture" }],
            DownloadRoot = Path.Combine(root, "downloads"), InstalledGamesRoot = installed, Library = [item] });
        var model = new MainViewModel(store);
        OrbitFrontendHost? host = null;
        Window? window = null;
        try
        {
            item = model.Library.Single();
            var saved = JsonSerializer.Serialize(model.Settings);
            await model.RefreshFolderActionsAsync();
            Check(model.LibraryDisplayTitle(item) == "Inspected title" && model.LibraryTitleId(item) == "PPSA06438", "nested game metadata supplies title and ID without a catalogue");
            model.Entries.Add(new() { Id = "release-a", SourceId = "fixture", GameId = "zoo", TitleId = "PPSA06438", Title = "Catalogue title", Platform = "PS5", CoverUrl = "https://fixture.invalid/cover.jpg" });
            model.Entries.Add(new() { Id = "release-b", SourceId = "fixture", GameId = "zoo", TitleId = "PPSA06438", Title = "Catalogue title", Platform = "PS5" });
            Check(model.LibraryCatalogEntry(item)?.CoverUrl.EndsWith("cover.jpg") == true, "multiple releases of one identified game can supply artwork");
            await model.RefreshCompatibilityAsync(_ => Task.FromResult(new Dictionary<string, string> { ["PPSA06438"] = "in-game" }));
            Check(model.LibraryCompatibility(item) == "in-game", "compatibility uses the inspected title ID");
            using var api = new OrbitFrontendApi(model);
            var response = api.Respond(OrbitFrontendSecurity.Document, "GET", OrbitFrontendSecurity.Origin + "/api/v1/windows/state");
            using (var json = JsonDocument.Parse(api.SerializeResponse(response.Body)))
            {
                var card = json.RootElement.GetProperty("library")[0];
                Check(card.GetProperty("title").GetString() == "Inspected title" && card.GetProperty("titleId").GetString() == "PPSA06438" &&
                    card.GetProperty("cover").GetString()!.EndsWith("cover.jpg"), "library API presents inspected title, ID and matched artwork");
            }
            Check(JsonSerializer.Serialize(model.Settings) == saved && item.Title == "unconfirmed-archive.rar" && item.InstalledGame is null,
                "identity presentation preserves persisted ownership and installation evidence");
            Directory.CreateDirectory(Path.Combine(game, "decrypted")); File.WriteAllText(Path.Combine(game, "decrypted", "eboot.bin"), "synthetic executable variant");
            await model.RefreshFolderActionsAsync();
            Check(model.LibraryTitleId(item) == "PPSA06438", "a companion decrypted executable retains the owning game's identity");
            host = new OrbitFrontendHost();
            window = new Window { Content = host, Width = 1100, Height = 750 };
            window.Show(); await host.InitializeAsync(model); host.NavigatePage("Library");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var rendered = false;
            while (DateTime.UtcNow < deadline)
            {
                if (await host.Browser.ExecuteScriptAsync("document.querySelector('[data-windows-id=\"" + item.Id + "\"]')?.textContent.includes('Inspected title')===true") == "true")
                { rendered = true; break; }
                await Task.Delay(60);
            }
            Check(rendered, "real WebView2 library card shows the inspected game title");
            model.Entries.Add(new() { Id = "conflict", SourceId = "fixture", GameId = "different", TitleId = "PPSA06438", Title = "Different game", Platform = "PS5" });
            Check(model.LibraryCatalogEntry(item) is null, "conflicting games sharing an ID remain unmatched");
            File.WriteAllText(metadata, """{"titleId":"PPSA06438","localizedParameters":{"defaultLanguage":"missing","ja-JP":{"titleName":"Other language"},"en-US":{"titleName":"English title"}}}""");
            await model.RefreshFolderActionsAsync();
            Check(model.LibraryDisplayTitle(item) == "English title", "English is preferred when the default language is unavailable, as in Kyty");
            File.WriteAllText(metadata, "not JSON");
            await model.RefreshFolderActionsAsync();
            Check(model.LibraryTitleId(item) == "" && model.LibraryDisplayTitle(item) == item.Title, "malformed metadata clears prior identity without filename guessing");
            File.WriteAllText(metadata, """{"titleId":"PPSA06438","titleName":"First game"}""");
            var other = Path.Combine(folder, "second"); Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "eboot.bin"), "another synthetic game");
            await model.RefreshFolderActionsAsync();
            Check(model.LibraryTitleId(item) == "" && model.LibraryDisplayTitle(item) == item.Title, "multiple game roots do not label a collection as one game");
            File.Delete(Path.Combine(other, "eboot.bin"));
            File.WriteAllText(metadata, new string('x', 1024 * 1024 + 1));
            await model.RefreshFolderActionsAsync();
            Check(model.LibraryTitleId(item) == "" && item.LaunchPaths.Count == 2, "oversized metadata stays unidentified without disabling launch actions");
            Console.WriteLine("PASS installed identity: nested/localized metadata, catalogue artwork, compatibility, ambiguity, malformed/oversized metadata and unchanged ownership");
        }
        finally
        {
            window?.Close(); host?.Dispose(); model.Dispose();
            for (var attempt = 0; attempt < 30 && Directory.Exists(root); attempt++)
            { try { Directory.Delete(root, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
