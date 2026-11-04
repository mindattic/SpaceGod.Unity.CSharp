#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Bench/CSharp and runs the five headless scenarios of Bench/scenarios.json over this repo's own Core, then
    checks each digest against the one every SpaceGod copy (Unity C#, Unreal C#, Unreal C++) produces.
.PARAMETER Scenario
    Run only these scenarios (default: all five).
.PARAMETER Framework
    net10.0 (default) or net8.0.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\bench.ps1
#>
param([string[]]$Scenario, [string]$Framework = 'net10.0')

$ErrorActionPreference = 'Stop'
$repo  = Split-Path $PSScriptRoot
$bench = Join-Path $repo 'Bench\CSharp'

# The digests all three copies agree on (Unreal.SpaceGod docs/RACE.md, Bench/README.md).
$expected = [ordered]@{
    'sol-idle'     = 'B30509691F52C9E0'
    'high-warp'    = 'A8A790EC13FD23C8'
    'economy-year' = '7F6533416B74D14D'
    'battle'       = 'ABD80179E1D7F1AC'
    'galaxy-ftl'   = 'F1E614170D1B1960'
}
if (-not $Scenario) { $Scenario = @($expected.Keys) }

dotnet build (Join-Path $bench 'Bench.CSharp.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Bench build failed' }
$exe = Join-Path $bench "bin\Release\$Framework\Bench.CSharp.exe"
$results = Join-Path $repo 'Bench\results'

$bad = 0
foreach ($s in $Scenario) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $exe --scenarios (Join-Path $repo 'Bench\scenarios.json') --scenario $s --out $results | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "  $s : runner failed (exit $LASTEXITCODE)" -ForegroundColor Red; $bad++; continue }
    $digest = (Get-Content (Join-Path $results "csharp-$s.json") -Raw | ConvertFrom-Json).digestHash
    $want = $expected[$s]
    $ok = $want -and $digest -eq $want
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-13} {1}  (expected {2}, {3:N1} s)" -f $s, $digest, $want, $watch.Elapsed.TotalSeconds) -ForegroundColor $color
    if (-not $ok) { $bad++ }
}
if ($bad) { Write-Host "  $bad scenario(s) off." -ForegroundColor Red; exit 1 }
Write-Host '  All digests match.' -ForegroundColor Green
exit 0
