$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'orbit-upstream'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'upstream-files.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if (!$path.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe upstream manifest member' }
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($path))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ($actual -ne $entry.sha256) { throw "Upstream file differs: $($entry.path)" }
}
Write-Output "Verified $($manifest.Count) unmodified upstream files"
