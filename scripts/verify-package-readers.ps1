$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$sources = Get-Content -LiteralPath (Join-Path $taskRoot 'ThirdParty/package-reader-sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sources) {
    $sourceRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot ('ThirdParty/' + $source.Directory + '/upstream')))
    $actual = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse)
    if ($actual.Count -ne $source.Files.Count) { throw ('Unexpected source file set: ' + $source.Directory) }
    foreach ($file in $source.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $sourceRoot $file.Path))
        if (!$path.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path' }
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) {
            throw ('Upstream source differs: ' + $source.Directory + '/' + $file.Path)
        }
    }
    $patched = @($source.Files | Where-Object { $_.PSObject.Properties['UpstreamSha256'] }).Count
    Write-Output ($source.Directory + ': ' + $source.Files.Count + ' source hashes verified (' + $patched + ' locally patched) at ' + $source.Commit)
}
