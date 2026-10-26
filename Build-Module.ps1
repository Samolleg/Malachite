param(
    [Parameter(Mandatory=$true)][string]$ModuleSdkDir,
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$ScriptCheckRoot = (Join-Path $PSScriptRoot '../work/eirebuild-wt/MAL18')
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $ModuleSdkDir).Path
foreach ($name in @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sdk $name))) { throw "SDK file missing: $name" }
}
$destination = [IO.Path]::GetFullPath($PackageDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new package directory; existing deliveries are immutable.' }
$compilerRoot = (Resolve-Path -LiteralPath $ScriptCheckRoot).Path
$compilerExe = Join-Path $compilerRoot 'out/Win32/Release/ScriptCheck.exe'
$compilerCommit = (& git -c "safe.directory=$compilerRoot" -C $compilerRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify ScriptCheck source.' }
$compilerState = & git -c "safe.directory=$compilerRoot" -C $compilerRoot status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $compilerState) { throw 'ScriptCheck source must be committed before building and packaging.' }
$builtCommit = (Get-Content -LiteralPath (Join-Path $compilerRoot 'obj/ScriptCheck/Win32/Release/compiler-commit.txt') -Raw).Trim()
if ($builtCommit -ne $compilerCommit.Substring(0,12)) { throw 'ScriptCheck build does not match source commit.' }
$compilerHash = (Get-FileHash -LiteralPath $compilerExe).Hash
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
Copy-Item -LiteralPath (Join-Path $compilerRoot 'ScriptCheck/README.md') -Destination $compilerDestination
$manifest = [ordered]@{
    scriptCheck = @{ commit=$compilerCommit; sha256=$compilerHash; runtime='static CRT/MFC, x86'; path='tools/ScriptCheck/ScriptCheck.exe' }
    sdk = @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll') | ForEach-Object { @{name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $sdk $_)).Hash} }
    files = Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object { @{name=$_.FullName.Substring($destination.TrimEnd('\').Length + 1).Replace('\','/'); length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Output $destination
