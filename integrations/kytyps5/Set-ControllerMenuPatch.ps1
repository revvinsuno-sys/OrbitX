[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply', 'Remove')]
    [string]$Action = 'Check',
    [string]$RepositoryPath = (Join-Path $PSScriptRoot '..\..\..\Tools\KytyPS5-source')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$taskRepository = (Resolve-Path -LiteralPath $RepositoryPath).Path
$taskRoot = & git -C $taskRepository rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($taskRoot) -ne [IO.Path]::GetFullPath($taskRepository)) {
    throw 'RepositoryPath must name the KytyPS5 repository root.'
}
$taskPatch = Join-Path $PSScriptRoot 'controller-menu.patch'

function Test-ControllerPatch([bool]$Reverse) {
    $ErrorActionPreference = 'Continue'
    $taskArguments = @('-C', $taskRepository, 'apply', '--check', '--index')
    if ($Reverse) { $taskArguments += '--reverse' }
    $taskArguments += @('--', $taskPatch)
    $taskDiagnostic = & git @taskArguments 2>&1
    $taskExitCode = $LASTEXITCODE
    if ($taskExitCode -ne 0) { $taskDiagnostic | ForEach-Object { Write-Verbose "$_" } }
    return $taskExitCode -eq 0
}

$taskApplied = Test-ControllerPatch $true
$taskApplicable = Test-ControllerPatch $false
if ($Action -eq 'Check') {
    if ($taskApplied) { Write-Output 'Controller menu patch is already applied and removable.'; return }
    if ($taskApplicable) { Write-Output 'Controller menu patch can be applied to this revision.'; return }
    throw 'The controller menu patch needs review for this revision or local edits. No files were changed.'
}
if ($Action -eq 'Apply') {
    if ($taskApplied) { Write-Output 'Controller menu patch is already applied.'; return }
    $taskDirty = & git -C $taskRepository status --porcelain
    if ($LASTEXITCODE -ne 0 -or $taskDirty) { throw 'Apply requires a clean KytyPS5 checkout. Preserve or commit your own changes first.' }
    if (-not $taskApplicable) { throw 'Patch no longer applies to this upstream revision. Port the patch before building; no files were changed.' }
    & git -C $taskRepository apply --index -- $taskPatch
} else {
    if (-not $taskApplied) {
        if ($taskApplicable) { Write-Output 'Controller menu patch is already removed.'; return }
        throw 'Patch files have changed or upstream differs. Remove was refused to preserve local work.'
    }
    & git -C $taskRepository apply --reverse --index -- $taskPatch
}
if ($LASTEXITCODE -ne 0) { throw "Patch $Action failed. Inspect git status before proceeding." }
Write-Output "Controller menu patch: $Action completed."
