namespace PS5CatalogBrowser.Core;

// Remove archive wrappers using directory renames, preserving every companion file.
public sealed class InstalledFolderLayout
{
    public string Root { get; init; } = "";
    public string GameRoot { get; init; } = "";
    public string Extras { get; init; } = "";
    public string Temporary { get; init; } = "";
    public Dictionary<string, string> FilePaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<InstalledFolderLayout?> PlanAsync(string root, CancellationToken cancellation)
    {
        root = Path.GetFullPath(root);
        var scan = await ImageFileSystem.ScanAsync(root, cancellation, requireGame: false);
        var gameRoots = scan.Files.Where(file => Path.GetFileName(file.RelativePath).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetDirectoryName(file.RelativePath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (gameRoots.Length == 0 || gameRoots.Contains("")) return null;
        // A decrypted executable beneath the same game is a variant, not another game.
        var candidates = gameRoots.Where(candidate => gameRoots.All(other => candidate == other ||
            Path.GetRelativePath(Path.Combine(root, candidate), Path.Combine(root, other)).Equals("decrypted", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length != 1) return null;
        var gameRoot = candidates[0];
        var extras = "Archive extras";
        for (var suffix = 1; File.Exists(Path.Combine(root, gameRoot, extras)) || Directory.Exists(Path.Combine(root, gameRoot, extras)); suffix++)
            extras = "Archive extras (" + suffix + ")";
        var layout = new InstalledFolderLayout { Root = root, GameRoot = gameRoot, Extras = extras,
            Temporary = Path.Combine(Path.GetDirectoryName(root)!, ".ps5catalog-layout-" + Guid.NewGuid().ToString("N")) };
        foreach (var file in scan.Files)
            layout.FilePaths.Add(file.RelativePath, ImageFileSystem.IsWithin(Path.Combine(root, gameRoot), Path.Combine(root, file.RelativePath))
                ? Path.GetRelativePath(Path.Combine(root, gameRoot), Path.Combine(root, file.RelativePath)) : Path.Combine(extras, file.RelativePath));
        return layout;
    }

    public void Apply()
    {
        ImageFileSystem.EnsureNoLinks(Root); ImageFileSystem.EnsureNoLinks(Temporary);
        if (File.Exists(Temporary) || Directory.Exists(Temporary)) throw new IOException("Layout staging already exists.");
        var lifted = false; var wrapped = false;
        try
        {
            Directory.Move(Path.Combine(Root, GameRoot), Temporary); lifted = true;
            Directory.Move(Root, Path.Combine(Temporary, Extras)); wrapped = true;
            Directory.Move(Temporary, Root);
        }
        catch
        {
            if (wrapped) Directory.Move(Path.Combine(Temporary, Extras), Root);
            if (lifted) Directory.Move(Temporary, Path.Combine(Root, GameRoot));
            throw;
        }
    }

    public void Undo()
    {
        Directory.Move(Root, Temporary);
        Directory.Move(Path.Combine(Temporary, Extras), Root);
        Directory.Move(Temporary, Path.Combine(Root, GameRoot));
    }

    public void Restore()
    {
        // Each directory rename is atomic. Restore the original layout at any checkpoint.
        if (Directory.Exists(Temporary))
        {
            if (!Directory.Exists(Root)) Directory.Move(Path.Combine(Temporary, Extras), Root);
            Directory.Move(Temporary, Path.Combine(Root, GameRoot));
        }
        else if (File.Exists(Path.Combine(Root, "eboot.bin")) && Directory.Exists(Path.Combine(Root, Extras))) Undo();
        else if (!Directory.Exists(Path.Combine(Root, GameRoot))) throw new IOException("Layout recovery paths are missing; files were preserved.");
    }
}
