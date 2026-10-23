param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the source checkout.' }
    if ($dirty) { throw 'Commit source changes before packaging a corresponding-source release.' }
    $sourceCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
    $shortCommit = $sourceCommit.Substring(0, 12)
    $releaseName = "OrbitX-$shortCommit-win-x64"
    $releaseRoot = Join-Path $repositoryRoot "artifacts/$releaseName"
    if (Test-Path -LiteralPath $releaseRoot) { throw "Release output already exists: $releaseRoot" }
    $applicationRoot = Join-Path $releaseRoot 'OrbitX'
    New-Item -ItemType Directory -Path $applicationRoot | Out-Null
    dotnet publish PS5CatalogBrowser.App/PS5CatalogBrowser.App.csproj -c Release -r win-x64 --self-contained false -o $applicationRoot --nologo
    if ($LASTEXITCODE -ne 0) { throw 'OrbitX publish failed; no release archives were created.' }
    $dirty = git status --porcelain
    $currentCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $dirty -or $currentCommit -ne $sourceCommit) { throw 'Source changed during publish; no release archives were created.' }
    $sourceName = "OrbitX-$shortCommit-source.zip"
    $sourceArchive = Join-Path $releaseRoot $sourceName
    git -c core.autocrlf=false archive --format=zip --prefix=OrbitX-source/ "--output=$sourceArchive" $sourceCommit
    if ($LASTEXITCODE -ne 0) { throw 'Corresponding-source archive failed.' }
    @"
OrbitX corresponding source
Commit: $sourceCommit
Source archive: $sourceName (distributed alongside this binary ZIP)
Repository: https://github.com/revvinsuno-sys/OrbitX
Exact source: https://github.com/revvinsuno-sys/OrbitX/tree/$sourceCommit
Licence: GNU GPL version 3. See LICENSE, NOTICE.md and THIRD-PARTY-NOTICES.md.
Rebuild instructions: README.md and DISTRIBUTING.md in the matching source archive.
"@ | Set-Content -LiteralPath (Join-Path $applicationRoot 'SOURCE.txt') -Encoding utf8
    $binaryArchive = Join-Path $releaseRoot "$releaseName.zip"
    Compress-Archive -LiteralPath $applicationRoot -DestinationPath $binaryArchive
    @($binaryArchive, $sourceArchive) | ForEach-Object {
        $digest = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS') -Encoding ascii
    Write-Output "Release pair ready: $releaseRoot"
} finally { Pop-Location }
