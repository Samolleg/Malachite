param([Parameter(Mandatory=$true)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$sourceFile = Join-Path $SourceRoot 'src/addon/NewScriptCommands.cpp'
$lines = [IO.File]::ReadAllLines($sourceFile)
$inTable = $false; $debug = $false; $commands = @()
for ($i=0; $i -lt $lines.Length; $i++) {
    $line=$lines[$i]
    if ($line.Contains('const SCustomHandler NewScriptCommands::NewScriptCmds[]')) {$inTable=$true; continue}
    if (!$inTable) {continue}
    if ($line.Trim() -eq '#ifdef _DEBUG') {$debug=$true; continue}
    if ($line.Trim() -eq '#endif') {$debug=$false; continue}
    if ($line -match '^\s*\{\s*"([^"\r\n]+)"\s*,\s*"([FGOSfgos?+]*)"\s*,\s*(\w+)\s*,\s*(0|CHF_HAS_BODY)\s*\}') {
        $commands += [ordered]@{name=$Matches[1];format=$Matches[2];handler=$Matches[3];body=($Matches[4] -eq 'CHF_HAS_BODY');debug=$debug;line=$i+1}
    }
    elseif ($line -match '^\s*\{\s*"[^"\r\n]+"') {throw "Unrecognized registration at line $($i+1): $line"}
    elseif ($line -match '^\s*#') {throw "Unsupported catalog preprocessor directive: $line"}
}
if($commands.Count -lt 90){throw 'Incomplete source catalog'}
$data=[ordered]@{sourceSha256=(Get-FileHash $sourceFile).Hash;commands=$commands}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'Data/addon.json'),($data | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
Write-Output "Imported $($commands.Count) entries; release $(($commands | Where-Object {!$_.debug}).Count), debug $(($commands | Where-Object debug).Count)"
