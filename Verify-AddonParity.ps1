param([Parameter(Mandatory=$true)][string]$EvidenceDirectory,
      [Parameter(Mandatory=$true)][string]$ReferenceExe)
$ErrorActionPreference='Stop'
$directory=Join-Path $EvidenceDirectory 'parity'
& $ReferenceExe $directory
if($LASTEXITCODE -ne 0){throw 'Native reference failed'}
$count=0; $mismatches=@()
foreach($file in Get-ChildItem -LiteralPath $directory -Filter '*.input') {
    $base=Join-Path $directory $file.BaseName
    $managed=Get-Content -LiteralPath "$base.json" -Raw | ConvertFrom-Json
    $native=Get-Content -LiteralPath "$base.native-meta"
    $same=(Get-FileHash -LiteralPath "$base.managed").Hash -eq (Get-FileHash -LiteralPath "$base.native").Hash
    if(!$same -or ($managed.Changed -ne ($native[0] -eq '1')) -or ($managed.HasError -ne ($native[1] -eq '1'))) {$mismatches+=$file.Name}
    $count++
}
if($count -eq 0){throw 'No parity fixtures'}
[ordered]@{cases=$count;mismatches=$mismatches;referenceSha256=(Get-FileHash -LiteralPath $ReferenceExe).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'parity-result.json')
if($mismatches.Count -gt 0){throw ('Native parity mismatch: '+($mismatches -join ', '))}
Write-Output "PASS native parity: $count cases, byte-identical text and matching changed/error decisions."
