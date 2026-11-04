#Requires -Version 5.1
<#
.SYNOPSIS
    The co-op smoke (US-T39, US-T43, US-T47): a host and a client of a built player on localhost, both muted and windowed,
    scripted by CoopSmoke.cs, with the same checks as the Unreal copies' tools/play.ps1 -Mode coop-smoke.
.DESCRIPTION
    The host (-CoopHost -port=<p> -SpaceGodCoopSmoke=host) starts first; once its app is up the client
    (-CoopJoin=127.0.0.1:<p> -SpaceGodCoopSmoke=client) joins. It loads the host's save, presses '.' and '3', takes a
    station's seat and keeps it through a deliberate resync, then spawns and flies a freighter and trades at a market. It
    takes a fighter's cockpit in a fight and uses its keys, takes the party to another star, and drives the galaxy map's
    strategy and envoy screens and a hail. Last, it asks the host to load its quicksave. Both log a digest of the shared
    state every 60 frames (-SpaceGodCoopLog=<file>); this script compares them and runs the checks.

    Two game processes are one heavy job: the script takes D:\Projects\SpaceGod.heavy.lock first (waiting while another
    job holds it) and always releases it; -NoLock skips that.
.PARAMETER PlayerDir
    The built player's folder (default: Builds\Windows of this repo), holding SpaceGod.Unity.CSharp.exe.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\coop-smoke.ps1
    powershell -ExecutionPolicy Bypass -File tools\coop-smoke.ps1 -PlayerDir C:\Apps\SpaceGod.Unity.CSharp
#>
param([string]$PlayerDir, [switch]$NoLock, [switch]$CaptureHost)
# -CaptureHost (US-T72, docs/CAPTURE.md): the host also runs with "-capture video", so the smoke's join, resync, trade,
# hail, party trip and load raise their capture events into its log and MindAttic.Capture records the session.

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not $PlayerDir) { $PlayerDir = Join-Path $repo 'Builds\Windows' }
$exe = Join-Path (Resolve-Path -LiteralPath $PlayerDir).Path 'SpaceGod.Unity.CSharp.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No SpaceGod.Unity.CSharp.exe in $PlayerDir (build with run.ps1 -BuildOnly)." }

$lock = 'D:\Projects\SpaceGod.heavy.lock'
$haveLock = $false
if (-not $NoLock) {
    while (-not $haveLock) {
        try { New-Item -ItemType File -Path $lock -Value "SpaceGod.Unity.CSharp coop-smoke $(Get-Date -Format o) pid $PID" -ErrorAction Stop | Out-Null; $haveLock = $true }
        catch { "co-op smoke: waiting for the heavy-job lock ($(Get-Content $lock -ErrorAction SilentlyContinue))"; Start-Sleep -Seconds 60 }
    }
}

$logs = Join-Path $repo 'Logs\CoopSmoke'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$run = (Get-Date).ToString('yyyyMMdd-HHmmss') + '-' + $PID
$hostLog = Join-Path $logs "CoopSmokeHost-$run.coop.log"
$clientLog = Join-Path $logs "CoopSmokeClient-$run.coop.log"
$hostPlayerLog = Join-Path $logs "CoopSmokeHost-$run.player.log"
$clientPlayerLog = Join-Path $logs "CoopSmokeClient-$run.player.log"
# A free TCP port, away from the default 7777.
$port = $null
foreach ($candidate in (Get-Random -InputObject (17800..18799) -Count 50)) {
    try { $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $candidate); $l.Start(); $l.Stop(); $port = $candidate; break } catch { }
}
if (-not $port) { throw 'co-op smoke: no free TCP port in 17800..18799' }
"co-op smoke: $exe, run $run on port $port; logs in $logs"

$hostP = $null; $clientP = $null; $result = 1
$previousMute = $env:MINDATTIC_CLI_MUTED
try {
    $env:MINDATTIC_CLI_MUTED = '1'
    $window = @('-muted', '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540')
    $hostArgs = $window + @('-CoopHost', "-port=$port", '-SpaceGodCoopSmoke=host', '-logFile', "`"$hostPlayerLog`"", "`"-SpaceGodCoopLog=$hostLog`"")
    if ($CaptureHost) { $hostArgs += @('-capture', 'video') }
    $hostP = Start-Process -FilePath $exe -PassThru -ArgumentList $hostArgs -WorkingDirectory (Split-Path $exe)
    $deadline = (Get-Date).AddMinutes(4)
    while (-not ((Test-Path -LiteralPath $hostLog) -and (Select-String -LiteralPath $hostLog -Pattern 'host app up' -Quiet))) {
        if ($hostP.HasExited -or (Get-Date) -gt $deadline) { throw "The co-op host did not come up (see $hostPlayerLog)." }
        Start-Sleep -Seconds 2
    }
    $clientArgs = $window + @("-CoopJoin=127.0.0.1:$port", '-SpaceGodCoopSmoke=client', '-logFile', "`"$clientPlayerLog`"", "`"-SpaceGodCoopLog=$clientLog`"")
    $clientP = Start-Process -FilePath $exe -PassThru -ArgumentList $clientArgs -WorkingDirectory (Split-Path $exe)
    $deadline = (Get-Date).AddMinutes(11)
    while (-not ($hostP.HasExited -and $clientP.HasExited)) {
        if ((Get-Date) -gt $deadline) {
            foreach ($p in @($hostP, $clientP)) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
            Write-Warning 'co-op smoke: timed out; both instances stopped'
            break
        }
        Start-Sleep -Seconds 2
    }

    $digests = @{ host = @{}; client = @{} }
    foreach ($side in @(@{ Name = 'host'; Log = $hostLog }, @{ Name = 'client'; Log = $clientLog })) {
        if (-not (Test-Path -LiteralPath $side.Log)) { Write-Warning "no $($side.Name) log"; continue }
        foreach ($line in (Get-Content -LiteralPath $side.Log | Where-Object { $_ -match 'SpaceGodCoop:' })) {
            "[$($side.Name)] " + ($line -replace '^\[[^\]]*\]\[\s*\d+\]', '')
            if ($line -match 'digest frame (\d+) = ([0-9A-F]{16}) \(') { $digests[$side.Name][[long]$Matches[1]] = $Matches[2] }
        }
    }
    foreach ($pl in @($hostPlayerLog, $clientPlayerLog)) {
        if (Test-Path -LiteralPath $pl) {
            $ex = @(Select-String -LiteralPath $pl -Pattern 'Exception' | Select-Object -First 5)
            "co-op smoke: $(Split-Path -Leaf $pl): $(@(Select-String -LiteralPath $pl -Pattern 'Exception').Count) exception line(s)"
            $ex | ForEach-Object { "    $($_.Line)" }
        }
    }
    $frames = @($digests.client.Keys | Where-Object { $digests.host.ContainsKey($_) } | Sort-Object)
    $same = @($frames | Where-Object { $digests.host[$_] -eq $digests.client[$_] })
    $range = if ($frames.Count -gt 0) { " (frames $($frames[0])..$($frames[-1]))" } else { '' }
    "co-op smoke: $($frames.Count) frames with both digests, $($same.Count) identical$range"
    $differ = @($frames | Where-Object { $digests.host[$_] -ne $digests.client[$_] })
    $hostText = if (Test-Path -LiteralPath $hostLog) { Get-Content -LiteralPath $hostLog -Raw } else { '' }
    $clientText = if (Test-Path -LiteralPath $clientLog) { Get-Content -LiteralPath $clientLog -Raw } else { '' }
    $syncs = @([regex]::Matches($hostText, 'sync #(\d+) at frame (\d+) \(([^)]*)\)') | ForEach-Object { "#$($_.Groups[1].Value) at $($_.Groups[2].Value): $($_.Groups[3].Value)" })
    function Both([string]$pattern) { ($hostText -match $pattern) -and ($clientText -match $pattern) }
    # The Unreal copies' checks (tools/play.ps1 -Mode coop-smoke), word for word.
    $checks = [ordered]@{
        'every digest identical'                    = $frames.Count -ge 10 -and $differ.Count -eq 0
        'no DESYNC'                                 = -not ($hostText -cmatch 'DESYNC at')
        'syncs only the join, the asked-for one and the load' = $syncs.Count -eq 3 -and $syncs[0] -match 'joined' -and $syncs[1] -match 'deliberate resync' -and $syncs[2] -match 'loaded a save'
        'seat kept through the resync (both peers)' = Both 'keeps seat \d+ \(.*\) through the sync'
        'helm taken in the frames (both peers)'     = Both 'takes the helm of .*Sandbox Freighter'
        'helm flown on the client''s input'         = ($clientText -match '(\d+) helm inputs sent') -and [int]([regex]::Match($clientText, '(\d+) helm inputs sent').Groups[1].Value) -gt 100
        'trade carried out (both peers)'            = Both "action 'buy "
        'cockpit keys carried out (both peers)'     = Both "cockpit key 'n 7'"
        'party trip (both peers), arrival'          = (Both "action 'travel ") -and ($clientText -match 'arrived in .* with the host''s frame')
        'client load asked of the host'             = $hostText -match "asked: load quick"
        'speaker picked in the frames (both peers)' = Both "action 'speak "
        'strategy orders (both peers)'              = (Both "action 'production ") -and (Both "action 'objective ")
        'hail (both peers)'                         = (Both "action 'hail ") -and (Both 'hail: The ')
        'diplomacy action (both peers)'             = Both "action 'gift "
        'statecraft carried out on the client'      = $clientText -match 'smoke: statecraft .*carried out: True'
    }
    "co-op smoke: syncs: $($syncs -join '; ')"
    foreach ($k in $checks.Keys) { "co-op smoke: $($k): $($checks[$k])" }
    $failed = @($checks.Values | Where-Object { -not $_ }).Count
    "co-op smoke: $($checks.Count - $failed)/$($checks.Count) checks passed"
    $result = if ($failed -gt 0) { 1 } else { 0 }
}
finally {
    foreach ($p in @($hostP, $clientP)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    if ($null -eq $previousMute) { Remove-Item Env:MINDATTIC_CLI_MUTED -ErrorAction SilentlyContinue } else { $env:MINDATTIC_CLI_MUTED = $previousMute }
    if ($haveLock) { Remove-Item $lock -Force -ErrorAction SilentlyContinue }
}
exit $result
