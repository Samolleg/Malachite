param(
    [Parameter(Mandatory=$true)][string]$ModuleSdkDir,
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$ScriptCheckRoot = ''
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = $PSScriptRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md') -PathType Leaf)) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Project RULES.md not found above worktree.' }
    $workspaceRoot = $parent
}

if (!$ScriptCheckRoot) { $ScriptCheckRoot = Join-Path $workspaceRoot 'builds/scriptcheck/current' }
$sdk = (Resolve-Path -LiteralPath $ModuleSdkDir).Path
foreach ($name in @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sdk $name))) { throw "SDK file missing: $name" }
}
$destination = [IO.Path]::GetFullPath($PackageDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new package directory; existing deliveries are immutable.' }
$compilerRoot = (Resolve-Path -LiteralPath $ScriptCheckRoot).Path
$compilerExe = Join-Path $compilerRoot 'ScriptCheck.exe'
$compilerManifest = Get-Content -LiteralPath (Join-Path $compilerRoot 'manifest.json') -Raw | ConvertFrom-Json
$compilerCommit = [string]$compilerManifest.commit
if ($compilerCommit -notmatch '^[0-9a-f]{40}$' -or $compilerManifest.path -cne 'ScriptCheck.exe' -or $compilerManifest.runtime -cne 'static CRT/MFC, x64') { throw 'Invalid ScriptCheck delivery manifest.' }
$compilerBytes = [IO.File]::ReadAllBytes($compilerExe)
if ($compilerBytes.Length -lt 64 -or [BitConverter]::ToUInt16($compilerBytes,0) -ne 0x5a4d) { throw 'Invalid ScriptCheck executable.' }
$peOffset = [BitConverter]::ToInt32($compilerBytes,0x3c)
if ($peOffset -lt 64 -or $peOffset -gt $compilerBytes.Length - 6 -or [BitConverter]::ToUInt32($compilerBytes,$peOffset) -ne 0x00004550 -or [BitConverter]::ToUInt16($compilerBytes,$peOffset + 4) -ne 0x8664) { throw 'ScriptCheck must be an x64 executable.' }
$compilerHash = (Get-FileHash -LiteralPath $compilerExe).Hash
if ($compilerManifest.sha256 -notmatch '^[0-9a-f]{64}$' -or $compilerHash -ne $compilerManifest.sha256) { throw 'ScriptCheck executable does not match delivery manifest.' }
& dotnet build (Join-Path $PSScriptRoot 'modules/Malachite.Module/Malachite.Module.csproj') -c Release "-p:ModuleSdkDir=$sdk" --nologo -v minimal -m:1 /nr:false /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Module build failed.' }
$output = Join-Path $PSScriptRoot 'modules/Malachite.Module/bin/Release/net9.0-windows'
$files = @('Malachite.Module.dll','Malachite.Module.deps.json','Malachite.UI.Wpf.dll','Malachite.Core.dll','module.json')
foreach ($name in $files) { if (-not (Test-Path -LiteralPath (Join-Path $output $name))) { throw "Missing output: $name" } }
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $output $name) -Destination $destination }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'modules/README.md') -Destination $destination
$compilerDestination = Join-Path $destination 'tools/ScriptCheck'
New-Item -ItemType Directory -Path $compilerDestination | Out-Null
Copy-Item -LiteralPath $compilerExe -Destination $compilerDestination
Copy-Item -LiteralPath (Join-Path $compilerRoot 'README.md') -Destination $compilerDestination
$manifest = [ordered]@{
    scriptCheck = @{ commit=$compilerCommit; sha256=$compilerHash; runtime='static CRT/MFC, x64'; path='tools/ScriptCheck/ScriptCheck.exe' }
    sdk = @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll') | ForEach-Object { @{name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $sdk $_)).Hash} }
    files = Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object { @{name=$_.FullName.Substring($destination.TrimEnd('\').Length + 1).Replace('\','/'); length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Output $destination
