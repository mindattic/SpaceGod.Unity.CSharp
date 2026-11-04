<#
.SYNOPSIS
  Deploys the current build to C:\Apps\SpaceGod.Unity.CSharp, then offers Normal Play, Demo +
  Capture, or Quit.
.DESCRIPTION
  Every menu choice launches muted by default (-muted and MINDATTIC_CLI_MUTED=1), Unity.Shared's
  session mute, so a CLI or agent launching the game for development never plays sound. Pass
  -Sound to launch with audio instead. The mute lasts for that run only and never touches the
  player's saved settings.json.
.PARAMETER Sound
  Launch with audio (no -muted, no MINDATTIC_CLI_MUTED).
.NOTES
  Always redeploys from source first (the repo-root run.ps1), so this never runs a stale build.
  Requires the Unity Editor closed for this project. Capture needs ffmpeg on PATH and the
  MindAttic.Capture library compiled in (see README.md).
#>
param([switch]$Sound)

$ErrorActionPreference = "Stop"

& (Join-Path (Split-Path $PSScriptRoot) "run.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "Deploy failed (exit $LASTEXITCODE); not launching." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = "C:\Apps\SpaceGod.Unity.CSharp\Unity.SpaceGod.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Deploy reported success but $exe is missing." -ForegroundColor Red
    exit 1
}

$muteArgs = @()
if (-not $Sound) { $muteArgs = @("-muted") }

Write-Host ""
Write-Host "  SpaceGod - Unity C#" -ForegroundColor Cyan
Write-Host "  [1] Normal Play"
Write-Host "  [2] Demo + Capture"
Write-Host "  [3] Quit"
Write-Host ("  ({0}; pass -Sound to launch.ps1 for audio)" -f $(if ($Sound) { "with sound" } else { "muted" })) -ForegroundColor DarkGray
Write-Host ""
$choice = Read-Host "Select (default 1)"
if ([string]::IsNullOrWhiteSpace($choice)) { $choice = "1" }

# The child inherits this process's environment; set the mute only around the launch.
$previous = $env:MINDATTIC_CLI_MUTED
if ($Sound) { Remove-Item Env:MINDATTIC_CLI_MUTED -ErrorAction SilentlyContinue } else { $env:MINDATTIC_CLI_MUTED = '1' }
try {
    # Start-Process rejects an empty -ArgumentList, so it is passed only when there are arguments.
    function Start-Game([string[]]$GameArgs) {
        $launch = @{ FilePath = $exe }
        if ($GameArgs.Count -gt 0) { $launch.ArgumentList = $GameArgs }
        Start-Process @launch
    }
    switch ($choice) {
        "1" { Start-Game $muteArgs }
        "2" { Start-Game ($muteArgs + @("-demo", "-capture", "video", "screen", "5")) }
        "3" { exit 0 }
        default {
            Write-Host "Unrecognized choice, launching Normal Play." -ForegroundColor Yellow
            Start-Game $muteArgs
        }
    }
} finally {
    if ($null -eq $previous) { Remove-Item Env:MINDATTIC_CLI_MUTED -ErrorAction SilentlyContinue } else { $env:MINDATTIC_CLI_MUTED = $previous }
}
