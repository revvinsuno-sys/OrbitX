[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimePath,
    [Parameter(Mandatory)][AllowEmptyString()][string]$InstalledVersion,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$taskRuntime = [IO.Path]::GetFullPath($RuntimePath)
$taskInstall = Split-Path -Parent $taskRuntime
if ([IO.Path]::GetFileName($taskRuntime) -ne 'kyty_emulator.exe' -or [IO.Path]::GetFileName($taskInstall) -ne 'KytyPS5') {
    throw 'Select the managed Tools/KytyPS5/kyty_emulator.exe before updating.'
}
$taskTools = Split-Path -Parent $taskInstall
$taskSource = Join-Path $taskTools 'KytyPS5-source'
$taskPatchHelper = Join-Path $PSScriptRoot 'Set-ControllerMenuPatch.ps1'
$taskPatch = Join-Path $PSScriptRoot 'controller-menu.patch'
$taskBuild = Join-Path $taskSource '_Build\windows'
$taskStage = Join-Path $taskTools ('.KytyPS5-stage-' + [guid]::NewGuid().ToString('N'))
$taskBackup = Join-Path $taskTools ('.KytyPS5-backup-' + [guid]::NewGuid().ToString('N'))
$taskUpdated = $false
$taskRollbackComplete = $false
$taskMutex = $null
$taskMutexAcquired = $false
$taskDetached = $false
$taskPatchApplied = $false

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkingDirectory = $taskSource) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}

function Get-BuildDate([string]$Version) {
    $match = [regex]::Match($Version, 'KytyPS5-(\d{4}-\d{2}-\d{2})-')
    if (-not $match.Success) { return $null }
    try { return [datetime]::ParseExact($match.Groups[1].Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } catch { return $null }
}

function Initialize-BuildEnvironment {
    $cache = Join-Path $taskBuild 'CMakeCache.txt'
    if (-not (Test-Path -LiteralPath $cache -PathType Leaf)) { throw 'Configure the existing Windows Release build before updating.' }
    function Read-CachePath([string]$Name) {
        $line = @(Get-Content -LiteralPath $cache | Where-Object { $_ -match ('^' + [regex]::Escape($Name) + ':[^=]+=') })
        if ($line.Count -ne 1) { throw "The configured Windows build is missing $Name." }
        $value = $line[0].Substring($line[0].IndexOf('=') + 1)
        if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "The configured $Name is unavailable: $value" }
        return $value
    }
    $script:taskCMake = Read-CachePath 'CMAKE_COMMAND'
    $compiler = Read-CachePath 'CMAKE_CXX_COMPILER'
    $ninja = Read-CachePath 'CMAKE_MAKE_PROGRAM'
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio Build Tools discovery (vswhere.exe) is unavailable.' }
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'Install the Visual Studio C++ Build Tools before updating KytyPS5.' }
    $devShell = Join-Path $vsRoot 'Common7\Tools\Launch-VsDevShell.ps1'
    if (-not (Test-Path -LiteralPath $devShell -PathType Leaf)) { throw 'The Visual Studio developer environment is unavailable.' }
    # DevShell runs a batch file; stray quotes in inherited PATH entries break cmd parsing.
    $env:PATH = ($env:PATH -split ';' | ForEach-Object { $_.Replace('"', '') }) -join ';'
    & $devShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
    if (-not $env:VSCMD_VER -or -not $env:INCLUDE -or -not $env:LIB) {
        throw 'The Visual Studio C++ environment could not be initialized; no update was attempted.'
    }
    $env:PATH = (Split-Path -Parent $script:taskCMake) + ';' + (Split-Path -Parent $compiler) + ';' + (Split-Path -Parent $ninja) + ';' + (Join-Path $taskSource '_Build\toolchain\glslang\bin') + ';' + $env:PATH
}

function Assert-NoKytyProcess {
    foreach ($process in @(Get-Process -Name kyty_emulator,kyty_launcher -ErrorAction SilentlyContinue)) {
        if ($process.Id -ne $PID) { throw "KytyPS5 is still running (process $($process.Id)); the runtime was left unchanged." }
    }
}

function Restore-KytySource {
    if (-not $script:taskDetached) { return }
    if ($script:taskPatchApplied) {
        & $script:taskPatchHelper -RepositoryPath $script:taskSource -Action Remove | Out-Null
        $script:taskPatchApplied = $false
    }
    Invoke-Checked 'git' @('-C', $script:taskSource, 'checkout', 'main')
    Invoke-Checked 'git' @('-C', $script:taskSource, 'merge', '--ff-only', 'origin/main')
    $script:taskDetached = $false
}

try {
    $taskMutex = [Threading.Mutex]::new($false, 'Local\PS5CatalogBrowser-KytyUpdate')
    try { $taskMutexAcquired = $taskMutex.WaitOne([timespan]::FromHours(2)) } catch [Threading.AbandonedMutexException] { $taskMutexAcquired = $true }
    if (-not $taskMutexAcquired) { throw 'Timed out waiting for another KytyPS5 update to finish; the installed runtime was left unchanged.' }
    $taskRelease = Invoke-RestMethod -Uri 'https://api.github.com/repos/KytyPS5/KytyPS5/releases/latest' -Headers @{ 'User-Agent' = 'PS5-Catalog-Browser' } -TimeoutSec 15
    if ($taskRelease.tag_name -notmatch '^KytyPS5-(\d{4}-\d{2}-\d{2})-([0-9a-f]{7,40})$') { throw 'The latest KytyPS5 release has an unrecognized tag; no update was attempted.' }
    $taskReleaseDate = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $taskReleaseTag = $taskRelease.tag_name
    if (-not $Force -and ($InstalledVersion -replace '-dirty$', '') -eq $taskReleaseTag) {
        Write-Output "KytyPS5 is current ($($taskRelease.tag_name))."
        exit 0
    }

    if (-not (Test-Path -LiteralPath $taskSource -PathType Container) -or -not (Test-Path -LiteralPath $taskBuild -PathType Container)) {
        throw 'The KytyPS5 source checkout or configured Windows build is unavailable; the installed runtime was left unchanged.'
    }
    $taskRoot = (& git -C $taskSource rev-parse --show-toplevel).Trim()
    if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($taskRoot) -ne [IO.Path]::GetFullPath($taskSource)) { throw 'The configured KytyPS5 source path is not a repository root.' }
    $taskBranch = (& git -C $taskSource branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $taskBranch -ne 'main') { throw 'KytyPS5 updates require the existing main checkout; no source or runtime files were changed.' }
    $taskOrigin = (& git -C $taskSource remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $taskOrigin -ne 'https://github.com/KytyPS5/KytyPS5.git') { throw 'The configured KytyPS5 origin is not the official upstream; no update was attempted.' }
    $taskDirty = & git -C $taskSource status --porcelain
    if ($LASTEXITCODE -ne 0 -or $taskDirty) { throw 'The KytyPS5 source checkout has local changes; no update was attempted.' }

    Assert-NoKytyProcess
    Write-Output 'KytyPS5: Preparing the configured Windows build environment...'
    Initialize-BuildEnvironment
    Write-Output "KytyPS5: Downloading source for $taskReleaseTag..."
    Invoke-Checked 'git' @('-C', $taskSource, 'fetch', '--no-tags', 'origin', '+refs/heads/main:refs/remotes/origin/main')
    Invoke-Checked 'git' @('-C', $taskSource, 'fetch', '--no-tags', 'origin', "refs/tags/$($taskRelease.tag_name):refs/tags/$($taskRelease.tag_name)")
    $taskHead = (& git -C $taskSource rev-parse HEAD).Trim()
    $taskRemoteHead = (& git -C $taskSource rev-parse 'origin/main').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the fetched KytyPS5 source revision.' }
    & git -C $taskSource merge-base --is-ancestor $taskHead $taskRemoteHead
    if ($LASTEXITCODE -ne 0) { throw 'KytyPS5 main cannot be fast-forwarded safely; the installed runtime was left unchanged.' }
    $taskReleaseCommit = (& git -C $taskSource rev-parse "refs/tags/$($taskRelease.tag_name)^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $taskReleaseCommit) { throw 'Could not identify the latest KytyPS5 release source revision.' }
    & git -C $taskSource merge-base --is-ancestor $taskReleaseCommit $taskRemoteHead
    if ($LASTEXITCODE -ne 0) { throw 'The latest official release is not present in upstream main; no update was attempted.' }

    Assert-NoKytyProcess
    $taskDetached = $true
    Invoke-Checked 'git' @('-C', $taskSource, 'checkout', '--detach', "refs/tags/$($taskRelease.tag_name)")
    Write-Output 'KytyPS5: Applying the custom controller menu patch...'
    & $taskPatchHelper -RepositoryPath $taskSource -Action Check | Out-Null
    & $taskPatchHelper -RepositoryPath $taskSource -Action Apply | Out-Null
    $taskPatchApplied = $true
    Write-Output 'KytyPS5: Configuring Release dependencies (downloads enabled)...'
    Invoke-Checked $taskCMake @('-S', $taskSource, '-B', $taskBuild, '-DFETCHCONTENT_FULLY_DISCONNECTED=OFF')
    Write-Output 'KytyPS5: Compiling the patched Release build...'
    Invoke-Checked $taskCMake @('--build', $taskBuild, '--config', 'Release', '--parallel', '8')
    Write-Output 'KytyPS5: Staging the compiled build...'
    Invoke-Checked $taskCMake @('--install', $taskBuild, '--config', 'Release', '--prefix', $taskStage)

    $taskStagedExe = Join-Path $taskStage 'kyty_emulator.exe'
    $taskStagedLauncher = Join-Path $taskStage 'kyty_launcher.exe'
    $taskStagedLauncherAlias = Join-Path $taskStage 'launcher.exe'
    if (-not (Test-Path -LiteralPath $taskStagedExe -PathType Leaf) -or -not (Test-Path -LiteralPath $taskStagedLauncher -PathType Leaf) -or -not (Test-Path -LiteralPath $taskStagedLauncherAlias -PathType Leaf)) { throw 'The staged build is missing a KytyPS5 executable; installed runtime was left unchanged.' }
    $taskBuiltBytes = [IO.File]::ReadAllBytes($taskStagedExe)
    $taskBuiltVersion = [Text.Encoding]::ASCII.GetString($taskBuiltBytes)
    $taskBuiltDate = Get-BuildDate $taskBuiltVersion
    if ($null -eq $taskBuiltDate -or $taskBuiltDate -lt $taskReleaseDate) { throw 'The staged binary does not identify the requested release or a newer source build; installed runtime was left unchanged.' }
    Restore-KytySource
    Assert-NoKytyProcess
    Write-Output 'KytyPS5: Installing and verifying the staged build...'

    New-Item -ItemType Directory -Path $taskBackup | Out-Null
    $taskFiles = @(Get-ChildItem -LiteralPath $taskStage -File -Recurse)
    if ($taskFiles.Count -eq 0) { throw 'The staged KytyPS5 installation is empty.' }
    $taskJournal = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($file in $taskFiles) {
            $relative = $file.FullName.Substring($taskStage.TrimEnd('\').Length + 1)
            $destination = Join-Path $taskInstall $relative
            $backup = Join-Path $taskBackup $relative
            $parent = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            $hadOld = Test-Path -LiteralPath $destination -PathType Leaf
            if ($hadOld) {
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-Item -LiteralPath $destination -Destination $backup
            }
            $taskJournal.Add([pscustomobject]@{ destination = $destination; backup = $backup; hadOld = $hadOld })
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
        foreach ($relativeExecutable in @('kyty_emulator.exe', 'kyty_launcher.exe', 'launcher.exe')) {
            $installedExecutable = Join-Path $taskInstall $relativeExecutable
            $stagedExecutable = Join-Path $taskStage $relativeExecutable
            if ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $stagedExecutable -Algorithm SHA256).Hash) {
                throw "Installed $relativeExecutable hash did not match the staged build."
            }
        }
        $taskInstalledHash = (Get-FileHash -LiteralPath $taskRuntime -Algorithm SHA256).Hash
        $taskInfo = [ordered]@{
            upstreamCommit = $taskReleaseCommit
            patchPath = $taskPatch
            patchSha256 = (Get-FileHash -LiteralPath $taskPatch -Algorithm SHA256).Hash
            installedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            emulatorSha256 = $taskInstalledHash
            launcherSha256 = (Get-FileHash -LiteralPath (Join-Path $taskInstall 'kyty_launcher.exe') -Algorithm SHA256).Hash
            version = [regex]::Match($taskBuiltVersion, 'KytyPS5-\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}(?:-\d+-g[0-9a-f]{7,40})?(?:-dirty)?').Value
            release = $taskRelease.tag_name
            checks = @('Release build', 'Staged install and Kyty executable SHA-256 verification')
            limitations = @('Game continues running while the menu is open', 'Physical controller and in-game rendering require manual acceptance')
            sourceCheckout = 'Clean upstream main; patch stored separately'
        }
        $taskInfoDir = Join-Path $taskInstall 'build-info'
        New-Item -ItemType Directory -Path $taskInfoDir -Force | Out-Null
        $taskInfoPath = Join-Path $taskInfoDir 'controller-menu-build.json'
        $taskInfoBackup = Join-Path $taskBackup 'build-info\controller-menu-build.json'
        $taskHadInfo = Test-Path -LiteralPath $taskInfoPath -PathType Leaf
        if ($taskHadInfo) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $taskInfoBackup) -Force | Out-Null
            Copy-Item -LiteralPath $taskInfoPath -Destination $taskInfoBackup
        }
        $taskJournal.Add([pscustomobject]@{ destination = $taskInfoPath; backup = $taskInfoBackup; hadOld = $taskHadInfo })
        $taskInfoTemp = Join-Path $taskStage 'controller-menu-build.json'
        [IO.File]::WriteAllText($taskInfoTemp, ($taskInfo | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $taskInfoTemp -Destination $taskInfoPath -Force
        $taskUpdated = $true
    }
    catch {
        for ($index = $taskJournal.Count - 1; $index -ge 0; $index--) {
            $entry = $taskJournal[$index]
            if ($entry.hadOld) { Copy-Item -LiteralPath $entry.backup -Destination $entry.destination -Force }
            else { Remove-Item -LiteralPath $entry.destination -Force -ErrorAction SilentlyContinue }
        }
        $taskRollbackComplete = $true
        throw
    }
    Write-Output "KytyPS5: Updated to $($taskRelease.tag_name) with the controller-menu patch."
}
finally {
    try { Restore-KytySource }
    finally {
        if (Test-Path -LiteralPath $taskStage) { Remove-Item -LiteralPath $taskStage -Recurse -Force }
        if (($taskUpdated -or $taskRollbackComplete) -and (Test-Path -LiteralPath $taskBackup)) { Remove-Item -LiteralPath $taskBackup -Recurse -Force }
        if ($taskMutexAcquired) { $taskMutex.ReleaseMutex() }
        if ($taskMutex) { $taskMutex.Dispose() }
    }
}
