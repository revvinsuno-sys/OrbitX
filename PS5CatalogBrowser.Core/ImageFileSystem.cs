using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5CatalogBrowser.Core;

public static class ImageFileSystem
{
    public static bool SamePath(string a, string b) => a.Length > 0 && b.Length > 0 && Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static readonly Regex Devices = new(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase);
    private static bool IsDevice(string name) => Devices.IsMatch(name.Split('.')[0].TrimEnd(' '));
    public static string FolderName(string title)
    {
        var name = new string(title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        // Traversal supplied as metadata is reduced to a single ordinary folder name.
        while (name.Contains("..", StringComparison.Ordinal)) name = name.Replace("..", "_", StringComparison.Ordinal);
        if (name.Length == 0) name = "Game";
        if (IsDevice(name)) name = "_" + name;
        return name.Length > 120 ? name[..120].TrimEnd('.', ' ') : name;
    }

    public static string Destination(string root, string folder)
    {
        if (folder != FolderName(folder) || folder.StartsWith(".install-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a single valid Windows folder name (no paths or reserved installation names).");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, folder));
        if (!IsWithin(root, path)) throw new InvalidDataException("Destination escapes the installed games directory.");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Destination already exists. Choose another folder name or cancel; existing data cannot be overwritten.");
        EnsureNoLinks(root);
        return path;
    }

    public static bool IsWithin(string root, string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links and reparse points are not allowed: " + current);
    }

    private static void ValidateName(string relative)
    {
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Length == 0 || part is "." or ".." || part.TrimEnd('.', ' ') != part || IsDevice(part) ||
                part.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
                throw new InvalidDataException("Unsafe image path: " + relative);
    }

    public static async Task<ImageScan> ScanAsync(string root, CancellationToken cancellation, bool requireGame = true)
    {
        return await Task.Run(async () =>
        {
            EnsureNoLinks(root);
            var files = new List<ImageFile>(); var directories = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root, entry); ValidateName(relative);
                    if (!IsWithin(root, entry) || !paths.Add(relative)) throw new InvalidDataException("Unsafe or duplicate image path: " + relative);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Image contains a link or reparse point: " + relative);
                    if ((attributes & FileAttributes.Directory) != 0) { directories.Add(relative); pending.Push(entry); }
                    else { var info = new FileInfo(entry); files.Add(new(relative, info.Length, info.LastWriteTimeUtc.Ticks)); }
                    if (paths.Count > 1_000_000) throw new InvalidDataException("Image contains too many filesystem entries.");
                }
            }
            files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
            directories.Sort(StringComparer.OrdinalIgnoreCase);
            var executables = files.Where(f => Path.GetFileName(f.RelativePath).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (requireGame && executables.Length != 1) throw new InvalidDataException("Image must contain one unambiguous game root with eboot.bin.");
            var gameRoot = executables.Length == 1 ? Path.GetDirectoryName(executables[0].RelativePath) ?? "" : "";
            var title = ""; var titleId = "";
            if (requireGame)
            {
                var systemRoot = Path.Combine(gameRoot, "sce_sys") + Path.DirectorySeparatorChar;
                if (!files.Any(f => f.RelativePath.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Game root has no sce_sys metadata or resources.");
                var metadata = files.FirstOrDefault(f => f.RelativePath.Equals(Path.Combine(gameRoot, "sce_sys", "param.json"), StringComparison.OrdinalIgnoreCase));
                if (metadata is not null)
                {
                    (title, titleId) = await ReadGameMetadataAsync(Path.Combine(root, gameRoot), cancellation);
                }
            }
            return new ImageScan(files, directories, gameRoot, title, titleId);
        }, cancellation);
    }
    public static async Task<(string Title, string TitleId)> ReadGameMetadataAsync(string gameRoot, CancellationToken cancellation)
    {
        var path = Path.Combine(gameRoot, "sce_sys", "param.json");
        if (!File.Exists(path)) return ("", "");
        EnsureNoLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("param.json exceeds the metadata size limit.");
        try
        {
            await using var stream = OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var title = "";
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("localizedParameters", out var local) && local.ValueKind == JsonValueKind.Object)
            {
                var language = Text(local, "defaultLanguage");
                if (language.Length > 0 && local.TryGetProperty(language, out var selected)) title = Text(selected, "titleName");
                if (title.Length == 0 && local.TryGetProperty("en-US", out var english)) title = Text(english, "titleName");
                if (title.Length == 0) title = local.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
                    .Select(p => Text(p.Value, "titleName")).FirstOrDefault(t => t.Length > 0) ?? "";
            }
            if (title.Length == 0) title = Text(json.RootElement, "titleName");
            var titleId = Text(json.RootElement, "titleId");
            return (title, Regex.IsMatch(titleId, @"^PPSA\d{5}$") ? titleId : "");
        }
        catch (JsonException) { return ("", ""); }
    }
    private static string Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public static async Task CopyAsync(string source, string destination, ImageScan scan, IProgress<InstallationProgress>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew(); long copied = 0; long reported = -250; var buffer = new byte[1024 * 1024];
        foreach (var directory in scan.Directories) { cancellation.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(destination, directory)); }
        foreach (var file in scan.Files)
        {
            cancellation.ThrowIfCancellationRequested(); EnsureNoLinks(Path.Combine(source, file.RelativePath)); EnsureNoLinks(Path.Combine(destination, file.RelativePath));
            await using var input = OpenRead(Path.Combine(source, file.RelativePath));
            if (input.Length != file.Length) throw new IOException("Image file changed during extraction: " + file.RelativePath);
            await using var output = new FileStream(Path.Combine(destination, file.RelativePath), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation); copied += read;
                if (clock.ElapsedMilliseconds - reported >= 250)
                {
                    reported = clock.ElapsedMilliseconds;
                    progress?.Report(new(InstallationState.Extracting, file.RelativePath, copied, scan.TotalBytes, copied / Math.Max(.001, clock.Elapsed.TotalSeconds), clock.Elapsed));
                }
            }
            await output.FlushAsync(cancellation); output.Flush(true);
        }
    }

    public static async Task<List<ImageFile>> VerifyAsync(string source, string destination, ImageScan scan, IProgress<InstallationProgress>? progress, CancellationToken cancellation)
    {
        await MatchStructureAsync(source, scan, cancellation, compareTimes: true);
        await MatchStructureAsync(destination, scan, cancellation);
        var destinationSnapshot = await ScanAsync(destination, cancellation, false);
        var hashes = new List<ImageFile>(); var clock = Stopwatch.StartNew(); long done = 0; long reported = -250;
        foreach (var file in scan.Files)
        {
            var a = await HashAsync(Path.Combine(source, file.RelativePath), cancellation, bytes => Report(bytes));
            var b = await HashAsync(Path.Combine(destination, file.RelativePath), cancellation, bytes => Report(bytes));
            if (a != b) throw new InvalidDataException("Full-content verification failed: " + file.RelativePath);
            hashes.Add(file with { Sha256 = a });
            void Report(int bytes)
            {
                done += bytes;
                if (clock.ElapsedMilliseconds - reported < 250) return;
                reported = clock.ElapsedMilliseconds;
                progress?.Report(new(InstallationState.Verifying, file.RelativePath, done, checked(scan.TotalBytes * 2),
                    done / Math.Max(.001, clock.Elapsed.TotalSeconds), clock.Elapsed));
            }
        }
        await MatchStructureAsync(source, scan, cancellation, compareTimes: true);
        await MatchStructureAsync(destination, destinationSnapshot, cancellation, compareTimes: true);
        return hashes;
    }

    public static async Task VerifyFinalAsync(string root, InstallationJob job, CancellationToken cancellation, IProgress<InstallationProgress>? progress = null)
    {
        var expected = new ImageScan(job.Files, job.Directories, job.GameRootRelative, job.Title, job.TitleId);
        await MatchStructureAsync(root, expected, cancellation);
        var snapshot = await ScanAsync(root, cancellation, false);
        var clock = Stopwatch.StartNew(); long done = 0; long reported = -250;
        foreach (var file in job.Files)
            if (file.Sha256.Length != 64 || await HashAsync(Path.Combine(root, file.RelativePath), cancellation, bytes =>
            {
                done += bytes;
                if (clock.ElapsedMilliseconds - reported < 250) return;
                reported = clock.ElapsedMilliseconds;
                progress?.Report(new(InstallationState.Verifying, file.RelativePath, done, job.TotalBytes,
                    done / Math.Max(.001, clock.Elapsed.TotalSeconds), clock.Elapsed));
            }) != file.Sha256) throw new InvalidDataException("Installed file failed SHA-256 verification: " + file.RelativePath);
        await MatchStructureAsync(root, snapshot, cancellation, compareTimes: true);
        var actual = await ScanAsync(root, cancellation, job.RequireGameRoot);
        if (actual.GameRootRelative != job.GameRootRelative) throw new InvalidDataException("Installed game root changed.");
    }

    private static async Task MatchStructureAsync(string root, ImageScan expected, CancellationToken cancellation, bool compareTimes = false)
    {
        var actual = await ScanAsync(root, cancellation, false);
        if (!actual.Directories.SequenceEqual(expected.Directories, StringComparer.Ordinal) || actual.Files.Count != expected.Files.Count)
            throw new InvalidDataException("Filesystem entry sets differ.");
        for (var i = 0; i < actual.Files.Count; i++)
            if (!actual.Files[i].RelativePath.Equals(expected.Files[i].RelativePath, StringComparison.Ordinal) || actual.Files[i].Length != expected.Files[i].Length ||
                (compareTimes && actual.Files[i].LastWriteTicks != expected.Files[i].LastWriteTicks))
                throw new InvalidDataException("File set, size or source modification time changed: " + actual.Files[i].RelativePath);
    }
    public static async Task<string> HashAsync(string path, CancellationToken cancellation, Action<int>? bytes = null)
    {
        EnsureNoLinks(path);
        await using var stream = OpenRead(path);
        return await HashAsync(stream, cancellation, bytes);
    }
    public static async Task<string> HashAsync(Stream stream, CancellationToken cancellation, Action<int>? bytes = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) > 0) { hash.AppendData(buffer, 0, read); bytes?.Invoke(read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
