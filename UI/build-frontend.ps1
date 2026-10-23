param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$uiRoot = $PSScriptRoot
& (Join-Path $uiRoot 'verify-upstream.ps1')
Push-Location (Join-Path $uiRoot 'orbit-upstream')
try {
    if ($Restore -or !(Test-Path node_modules/.package-lock.json)) {
        npm ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Orbit dependency restore failed' }
    }
    & node_modules/.bin/tsc -p ../windows-adapter/tsconfig.json
    if ($LASTEXITCODE -ne 0) { throw 'Orbit/adapter TypeScript check failed' }
    & node_modules/.bin/vite build --config ../windows-adapter/vite.config.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Orbit frontend build failed' }
} finally { Pop-Location }
$assets = [ordered]@{}
$buildRoot = Join-Path $uiRoot 'build'
Get-ChildItem -LiteralPath $buildRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring(([IO.Path]::GetFullPath($buildRoot).TrimEnd('\') + '\').Length).Replace('\', '/')
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $assets[$relative] = [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($_.FullName))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}
$assets | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildRoot 'asset-sha256.json') -Encoding utf8
