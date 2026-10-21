param(
    [Parameter(Mandatory=$true)][string]$ModuleSdkDir,
    [Parameter(Mandatory=$true)][string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $ModuleSdkDir).Path
foreach ($name in @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sdk $name))) { throw "SDK file missing: $name" }
}
$destination = [IO.Path]::GetFullPath($PackageDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new package directory; existing deliveries are immutable.' }
& dotnet build (Join-Path $PSScriptRoot 'modules/Malachite.Module/Malachite.Module.csproj') -c Release "-p:ModuleSdkDir=$sdk" --nologo -v minimal -m:1 /nr:false /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Module build failed.' }
$output = Join-Path $PSScriptRoot 'modules/Malachite.Module/bin/Release/net9.0-windows'
$files = @('Malachite.Module.dll','Malachite.Module.deps.json','Malachite.UI.Wpf.dll','Malachite.Core.dll','module.json')
foreach ($name in $files) { if (-not (Test-Path -LiteralPath (Join-Path $output $name))) { throw "Missing output: $name" } }
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $output $name) -Destination $destination }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'modules/README.md') -Destination $destination
$manifest = [ordered]@{
    sdk = @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll') | ForEach-Object { @{name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $sdk $_)).Hash} }
    files = Get-ChildItem -LiteralPath $destination -File | ForEach-Object { @{name=$_.Name; length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Output $destination
