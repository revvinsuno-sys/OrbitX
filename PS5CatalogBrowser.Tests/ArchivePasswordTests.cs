using System.IO;
using PS5CatalogBrowser.Core;

internal static class ArchivePasswordTests
{
    internal const string Password = "SyntheticArchivePassword";
    // Synthetic archives: game/eboot.bin and sce_sys/param.json only.
    internal static readonly (string Name, string Bytes)[] Fixtures =
    [
        ("encrypted-data.rar", "UmFyIRoHAQAzkrXlCgEFBgAFAQGAgACwGNIaWwIDPLAABKAAIOr0TaSAAwAOZ2FtZS9lYm9vdC5iaW4wAQADDxiymzXiLzAiAcRzRGGAXIOf1ZfZ4CFyEy8lv6Ge8GtmG4OS+h1iJqShccanCgMCCsoYT85c3QHyPNKP/dmKk9HJ6pZ9KcruU53K/NSB7n9gz0M/VHWWBIZe63lQfophKKAoK6ttmKbyDLLYZAIDPNAABMAAIGBfAFaAAwAXZ2FtZS9zY2Vfc3lzL3BhcmFtLmpzb24wAQADDxiymzXiLzAiAcRzRGGAXIOtRoU/dgyHO4qY4FkzHD7SG4OS+h1iJqShccanCgMCGvEYT85c3QFLtKDu6w0zoNcTTYT9SSDXgFkycpGZBcHKvPKKkck4mNUa3ipvxeNEnuYv+lvPLpdcCYeALJ3ZnsSf3HqmCX2sZt+KfgJ6g6CRGHMDiWPxX0x2FywmAgMLAAUAEAAAAACAAAAMZ2FtZS9zY2Vfc3lzCgMCCsoYT85c3QEshVxbHgIDCwAFABAAAAAAgAAABGdhbWUKAwL5ohhPzlzdAR13VlEDBQQA"),
        ("encrypted-headers.rar", "UmFyIRoHAQAxQlf4IQQAAAEP4c4cA9vre3jNgLKy/Uo7tJUvEneJyzN01E+ilVGmTVW7YjYK4bExNhqH6N0NfAzSy6uf6OVKtL28Mz9cg524n2TvqIC0KyRVVu5QOICAXMKzm7O4+vqwdhqaKFloIttfRvl5oUlt9XTa44RZdxtJgVjjVZoA+Qk0zjJFTebZH4Dj/oRu9CZASG7S2UEIVdurTURde5gG0joUuwTS3LrLgLeHM2SQ66bdnwvQILvEPPuzXso5WTrjdd5ov/7/QnWujeirsjhdhNOGqfn5eMY2go6jL3e4y3UCqa4wEqBUbfUL8MsKhMAq/PrE9//8nFUdxFCCm9gITAWssiZI/Y9uVrHl+hOvVOiug1lEmVoHDJJmbkwDcYSBYnUfME/mA0R2rsOn3WIpCkPzltJdjqejofa4MrjOLwX//V6A8CKN1h2r1A7gjOETGHlXqpnpHd1UVJhSrTByTL1CxW1y1x7a1tn5miP09tGfjfK5FQigLPPPjwISqRMgv8qS4g6zmM2d50v0wyT9/pLnvyCNKFkmbwfraUBKGo71X3N4+C5ixt8F6Ar81mJoV0zuZR8/9AlB2hIYZmJD4zV/4pyZ1RZeTYVq4M2HCWl2ZQRlxhuEAhJK8MvCeni2xjPYFNevqYd5Pd1cFz0aHiEBC42SPI6X9SCHV4/ec6ue1F56nEaaa/dTPhXh6tHow9MfMBpZ42VC3pNfbgVYGWKQR4WAsoQWLZF/FLrmc4RC1OkEheyLZs66i36tjJqpHg4wYTuO4EuVmTmoAFxep4SUtO3W"),
        ("encrypted.zip", "UEsDBAoAAAAAAOKWT10AAAAAAAAAAAAAAAAFAAAAZ2FtZS9QSwMEFAAJAGMA4pZPXSofOFI+AAAAIAAAAA4ACwBnYW1lL2Vib290LmJpbgGZBwABAEFFAwgAG2QDQKKa/DiGNaSN6Lpy8ncPty1GklRrjjRCLcVluVHmclMlszAh4ypuDc/TZrwo6iqWQsXU5D0OJiKGXydQSwcIKh84Uj4AAAAgAAAAUEsDBAoAAAAAAOKWT10AAAAAAAAAAAAAAAANAAAAZ2FtZS9zY2Vfc3lzL1BLAwQUAAkAYwDilk9dfexw7FcAAABAAAAAFwALAGdhbWUvc2NlX3N5cy9wYXJhbS5qc29uAZkHAAEAQUUDCAD+uGBYPkB4ZGAQciZcmsVsDSeyEEMak+xtzwiVfegFE0yunEz6B6K4AtJ8oOSMOA+AYeVUkFPnz2gXw9DyUNApHgX9wT2pMfvzNWLddbC0hLh5Jn8+ll5QSwcIfexw7FcAAABAAAAAUEsBAh8ACgAAAAAA4pZPXQAAAAAAAAAAAAAAAAUAJAAAAAAAAAAQAAAAAAAAAGdhbWUvCgAgAAAAAAABABgA+aIYT85c3QFclDFPzlzdAajIFU/OXN0BUEsBAh8AFAAJAGMA4pZPXSofOFI+AAAAIAAAAA4ALwAAAAAAAAAgAAAAIwAAAGdhbWUvZWJvb3QuYmluCgAgAAAAAAABABgACsoYT85c3QEKyhhPzlzdAfmiGE/OXN0BAZkHAAEAQUUDCABQSwECHwAKAAAAAADilk9dAAAAAAAAAAAAAAAADQAkAAAAAAAAABAAAACoAAAAZ2FtZS9zY2Vfc3lzLwoAIAAAAAAAAQAYAArKGE/OXN0BXJQxT85c3QE1QxdPzlzdAVBLAQIfABQACQBjAOKWT1197HDsVwAAAEAAAAAXAC8AAAAAAAAAIAAAANMAAABnYW1lL3NjZV9zeXMvcGFyYW0uanNvbgoAIAAAAAAAAQAYABrxGE/OXN0BGvEYT85c3QEKyhhPzlzdAQGZBwABAEFFAwgAUEsFBgAAAAAEAAQAlQEAAHoBAAAAAA=="),
        ("legacy.zip", "UEsDBAoAAAAAAOKWT10AAAAAAAAAAAAAAAAFAAAAZ2FtZS9QSwMEFAAJAAgA4pZPXSofOFIuAAAAIAAAAA4AAABnYW1lL2Vib290LmJpbgK8YIfy7c2lC7wL+3DTXUDO05duRPSJcgiIPQH1EVIC7jtsH8R2P5kh1+SEm3BQSwcIKh84Ui4AAAAgAAAAUEsDBAoAAAAAAOKWT10AAAAAAAAAAAAAAAANAAAAZ2FtZS9zY2Vfc3lzL1BLAwQUAAkACADilk9dfexw7EcAAABAAAAAFwAAAGdhbWUvc2NlX3N5cy9wYXJhbS5qc29uXTTLpYdsyv7ty8tTp/Jo2+gvOX+ouNLSjoVq5En6XxfYZUhrlPli7kwIcGcpjMThqO6hWnrM76GC6ZsiKwIpIZLGNMjfhLJQSwcIfexw7EcAAABAAAAAUEsBAh8ACgAAAAAA4pZPXQAAAAAAAAAAAAAAAAUAJAAAAAAAAAAQAAAAAAAAAGdhbWUvCgAgAAAAAAABABgA+aIYT85c3QFclDFPzlzdAajIFU/OXN0BUEsBAh8AFAAJAAgA4pZPXSofOFIuAAAAIAAAAA4AJAAAAAAAAAAgAAAAIwAAAGdhbWUvZWJvb3QuYmluCgAgAAAAAAABABgACsoYT85c3QEKyhhPzlzdAfmiGE/OXN0BUEsBAh8ACgAAAAAA4pZPXQAAAAAAAAAAAAAAAA0AJAAAAAAAAAAQAAAAjQAAAGdhbWUvc2NlX3N5cy8KACAAAAAAAAEAGAAKyhhPzlzdAVyUMU/OXN0BNUMXT85c3QFQSwECHwAUAAkACADilk9dfexw7EcAAABAAAAAFwAkAAAAAAAAACAAAAC4AAAAZ2FtZS9zY2Vfc3lzL3BhcmFtLmpzb24KACAAAAAAAAEAGAAa8RhPzlzdARrxGE/OXN0BCsoYT85c3QFQSwUGAAAAAAQABAB/AQAARAEAAAAA"),
        ("rar4-encrypted-compressed.rar", "UmFyIRoHAM+QcwAADQAAAAAAAAAnYnQklDoAMAAAAJQRAAACYp9nnOOgT10dNQ0AIAAAAGdhbWVcZGF0YS50eHQOULaEWNMeAACwGb8l5s4J6yIxT3YabbxAmaMlDD6XCyUyywYKI8GMfjGcuXxUm2RsKenJLPcpJUoZRkP/wHd0JJQ7ADAAAAAgAAAAAiofOFJxn09dHTUOACAAAABnYW1lXGVib290LmJpbg5QtoRY0x4AAPBQwTFSu2E2a+YC803zPbBZyXBz5hanceNK+WT8EEhCmHFcVgCs/G11ZSkNCslF7eo29kLyLnQklEQAUAAAAEAAAAACfexw7HGfT10dNRcAIAAAAGdhbWVcc2NlX3N5c1xwYXJhbS5qc29uDlC2hFjTHgAA8GDoMXoI+Cs3EezXLSoAAKcS48aWE80XosmBcAc3s2/RdKuzvwGngZadYecoJZEflaryUNbXrQF2Kr/XVbNOFR2pQlF4f+sKw3CGCWL9V/vVu0xpxD17AEAHAA=="),
        ("rar4-encrypted-stored.rar", "UmFyIRoHAM+QcwAADQAAAAAAAADhBXQklDsAIAAAACAAAAACKh84UnGfT10dMA4AIAAAAGdhbWVcZWJvb3QuYmluQMpFkjvNsxUA8FDBMSu/jXToFUPlTyWeP4leN0UiLcWgeLLbtkBMcQh/0bysCnd0JJREAEAAAABAAAAAAn3scOxxn09dHTAXACAAAABnYW1lXHNjZV9zeXNccGFyYW0uanNvbkDKRZI7zbMVAPBg6DEe0GtVJdvXLdAA+d/WqL/wZk1050vYh2mrwpksquyXFs2OWhOlWjqMRcDVAGX6A1oNR6PJSSWHkWlTdWcD2pV9cNN04JAxAAAAAAAAAAAAAgAAAABxn09dHTAMABAAAABnYW1lXHNjZV9zeXMA8GDoMYsBdOCQKQAAAAAAAAAAAAIAAAAAcZ9PXR0wBAAQAAAAZ2FtZQDwUMExxD17AEAHAA=="),
    ];
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS5ArchivePasswords-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var candidates = ArchiveExtraction.Candidates(Password, ["wrong", Password, "syntheticarchivepassword", ""]);
            Check(candidates.SequenceEqual(new[] { Password, "wrong", "syntheticarchivepassword" }), "supplied first, distinct and case-sensitive candidates");
            foreach (var (name, bytes) in Fixtures)
            {
                var source = Path.Combine(root, name); File.WriteAllBytes(source, Convert.FromBase64String(bytes));
                var destination = source + ".out";
                await ArchiveExtraction.ExtractAsync(source, destination, (name == "legacy.zip" ? Enumerable.Range(0, 512).Select(i => "wrong-" + i) : ["wrong", "syntheticarchivepassword"]).Append(Password), default);
                Check(File.ReadAllText(Path.Combine(destination, "game", "eboot.bin")) == "synthetic unsupported executable", name + " retries and extracts exact bytes");
                Check(File.Exists(source) && !Directory.EnumerateDirectories(root, ".ps5catalog-extract-*").Any(), name + " keeps source and cleans failed attempts");
                try { await ArchiveExtraction.ExtractAsync(source, source + ".bad", ["wrong", "syntheticarchivepassword"], default); throw new Exception("Wrong passwords accepted"); }
                catch (ArchivePasswordException) { Check(!Directory.Exists(source + ".bad"), name + " exhausted passwords publish nothing"); }
                if (name.EndsWith(".zip", StringComparison.Ordinal))
                {
                    var corrupt = Convert.FromBase64String(bytes);
                    var entryName = System.Text.Encoding.UTF8.GetBytes("game/eboot.bin");
                    var nameOffset = corrupt.AsSpan().IndexOf(entryName);
                    var extraLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(corrupt.AsSpan(nameOffset - 2));
                    corrupt[nameOffset + entryName.Length + extraLength + 20] ^= 0x40;
                    var corruptSource = source + ".corrupt.zip"; File.WriteAllBytes(corruptSource, corrupt);
                    try { await ArchiveExtraction.ExtractAsync(corruptSource, source + ".corrupt-out", [Password], default); throw new Exception("Corrupt ciphertext accepted"); }
                    catch (IOException) { Check(!Directory.Exists(source + ".corrupt-out"), "encrypted ZIP integrity failure cannot publish content"); }
                }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await ArchiveExtraction.ExtractAsync(source, source + ".cancelled", [Password], cancelled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Check(!Directory.Exists(source + ".cancelled"), "cancelled extraction not published"); }
            }
            Check(!Directory.EnumerateDirectories(root, ".ps5catalog-extract-*").Any(), "all password attempt staging cleaned");
            Console.WriteLine("PASS encrypted ZIP AES/ZipCrypto and RAR4/RAR5 headers/data: ordered retries, wrong case/exhaustion, exact bytes, cancellation and staging cleanup");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
