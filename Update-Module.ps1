[CmdletBinding()]
param(
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = $PSScriptRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md') -PathType Leaf)) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Project RULES.md not found above worktree.' }
    $workspaceRoot = $parent
}

$editorRoot = Join-Path $workspaceRoot 'dbeditor'
$sdk = Join-Path $workspaceRoot 'builds/dbeditor/current'
$updater = Join-Path $editorRoot 'tools/Update-Malachite.ps1'
if (-not $BuildOnly -and -not (Test-Path -LiteralPath $updater -PathType Leaf)) {
    throw "Host updater missing: $updater. Restore the host tooling before updating."
}

# A unique temporary package avoids changing historical deliveries. Keep it on
# failure as evidence; installation and compatibility checks belong to the host.
$package = Join-Path ([IO.Path]::GetTempPath()) ('malachite-module-' + [Guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'Build-Module.ps1') -ModuleSdkDir $sdk -PackageDirectory $package
if (-not (Test-Path -LiteralPath (Join-Path $package 'manifest.json') -PathType Leaf)) {
    throw "Module package was not completed: $package"
}
if ($BuildOnly) {
    Write-Host "Build only. Current installation unchanged. Package: $package"
    return
}

& $updater -PackagePath $package
Write-Host "Host updater completed. Temporary package: $package"
