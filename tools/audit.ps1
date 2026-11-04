#Requires -Version 5.1
<#
.SYNOPSIS
    The scripted smoke and faithfulness audit (US-T70): the Unreal copies' tools/play.ps1 -Mode audit, for the Unity copy.
.DESCRIPTION
    Starts the built player in a small muted window with -SpaceGodSmoke -SpaceGodAudit (a real graphics device: the game's
    GPU-propagated views need compute shaders, which -batchmode -nographics has none of). Runtime/Audit/AuditRunner.cs
    runs the Unreal copies' smoke and then FeatureAudit's probes (the same file as SpaceGod.Unreal.CSharp's, the same ids
    as SpaceGod.Unreal.Cpp's Smoke/FeatureAudit.cpp): each presses one of the original's bindings on a scripted device and
    checks the state the original's code changes, logging 'AUDIT <id> PASS|FAIL|INFO: ...'. Prints those lines and
    'audit (Unity): <n> PASS, <m> FAIL', the Unreal script's summary; exits 1 on any FAIL.

    One game process is a heavy job: the script takes D:\Projects\SpaceGod.heavy.lock first (waiting while another job
    holds it) and always releases it; -NoLock skips that.
.PARAMETER PlayerDir
    The built player's folder (default: Builds\Windows of this repo), holding SpaceGod.Unity.CSharp.exe.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\audit.ps1
    powershell -ExecutionPolicy Bypass -File tools\audit.ps1 -PlayerDir C:\Apps\SpaceGod.Unity.CSharp
#>
param([string]$PlayerDir, [switch]$NoLock)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not $PlayerDir) { $PlayerDir = Join-Path $repo 'Builds\Windows' }
$exe = Join-Path (Resolve-Path -LiteralPath $PlayerDir).Path 'SpaceGod.Unity.CSharp.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No SpaceGod.Unity.CSharp.exe in $PlayerDir (build with run.ps1 -BuildOnly)." }

$lock = 'D:\Projects\SpaceGod.heavy.lock'
$haveLock = $false
if (-not $NoLock) {
    while (-not $haveLock) {
        try { New-Item -ItemType File -Path $lock -Value "SpaceGod.Unity.CSharp audit $(Get-Date -Format o) pid $PID" -ErrorAction Stop | Out-Null; $haveLock = $true }
        catch { "audit: waiting for the heavy-job lock ($(Get-Content $lock -ErrorAction SilentlyContinue))"; Start-Sleep -Seconds 60 }
    }
}

$logs = Join-Path $repo 'Logs\Audit'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$run = (Get-Date).ToString('yyyyMMdd-HHmmss') + '-' + $PID
$auditLog = Join-Path $logs "Audit-$run.log"
$playerLog = Join-Path $logs "Audit-$run.player.log"
"audit: $exe, run $run; logs in $logs"

$p = $null; $result = 1
try {
    $env:MINDATTIC_CLI_MUTED = '1'
    $playerArgs = @('-muted', '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540', '-SpaceGodSmoke', '-SpaceGodAudit', '-logFile', "`"$playerLog`"", "`"-SpaceGodAuditLog=$auditLog`"")
    $p = Start-Process -FilePath $exe -PassThru -ArgumentList $playerArgs -WorkingDirectory (Split-Path $exe)
    if (-not $p.WaitForExit(30 * 60 * 1000)) {
        Stop-Process -Id $p.Id -Force
        Write-Warning 'audit: timed out after 30 minutes; the player was stopped'
    }
    $lines = @()
    if (Test-Path -LiteralPath $auditLog) { $lines = @(Get-Content -LiteralPath $auditLog | Where-Object { $_ -match 'AUDIT |SpaceGodSmoke|UnitySpaceApp up' }) }
    else { Write-Warning "audit: no audit log ($auditLog); see $playerLog" }
    $lines
    if (Test-Path -LiteralPath $playerLog) {
        Select-String -LiteralPath $playerLog -Pattern 'Exception' | Select-Object -First 10 | ForEach-Object { "player log: $($_.Line)" }
    }
    $pass = @($lines | Where-Object { $_ -match 'AUDIT \S+ PASS' }).Count
    $fail = @($lines | Where-Object { $_ -match 'AUDIT \S+ FAIL' }).Count
    $info = @($lines | Where-Object { $_ -match 'AUDIT \S+ INFO' }).Count
    "audit (Unity): $pass PASS, $fail FAIL, $info INFO"
    $result = if ($fail -gt 0 -or $pass -eq 0) { 1 } else { 0 }
}
finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    if ($haveLock) { Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue }
}
exit $result
