#Requires -Version 5.1
<#
.SYNOPSIS
    SpaceGod.Unity.CSharp (the Unity C# candidate of SpaceGod): shuts down this copy's running game, clears the build output, rebuilds the Windows player
    from scratch (ProjectSetup.BuildWindows — this project has no separate dev/release split; its
    one build already uses BuildOptions.None), and publishes a standalone copy to
    C:\Apps\SpaceGod.Unity.CSharp\ — same process UnityEve/Automata/KdpPublish/IdiotProof use to always
    have a fresh, independent deployed copy instead of a possibly-stale one.

.DESCRIPTION
    1. Stops this copy's running Unity.SpaceGod.exe (its Builds\Windows or C:\Apps\SpaceGod.Unity.CSharp copy only;
       the original Unity.SpaceGod repo's deployed game is left alone).
    2. Removes Builds\Windows — the build OUTPUT only, never Library\ (Unity's asset
       import / script-compile cache, which survives exactly as it does for a normal
       Editor rebuild) — so the player build below is never a stale incremental copy.
    3. Runs Unity in batch mode (-executeMethod Unity.SpaceGod.EditorTools.ProjectSetup.SetupAndBuild,
       requires the Editor closed) -> Builds\Windows\Unity.SpaceGod.exe.
    4. Copies Builds\Windows\* -> C:\Apps\SpaceGod.Unity.CSharp\ and refreshes the Desktop shortcut
       "SpaceGod – Unity C#" (it opens deploy.bat).

.PARAMETER SkipHdrp
    Build only the URP player (the default), not the HDRP one beside it in HDRP\ that
    Settings > Render pipeline restarts into (U.SPC-T430). Faster; choosing HDRP then stays on URP.

.PARAMETER Launch
    After publishing, immediately run the deployed Unity.SpaceGod.exe.

.PARAMETER Force
    Rebuild and republish even when the deployed commit already matches the player source (skips
    only the up-to-date check; it clears nothing more than a normal rebuild does). The repo-root
    deploy.bat's "1) Deploy & Run" passes it, so that choice always republishes.

.PARAMETER LauncherOnly
    Refresh the Desktop shortcut (pointing at the repo-root deploy.bat, whose menu offers
    1) Deploy & Run and 2) Run) and exit, without pulling, building or stopping anything.

.PARAMETER BuildOnly
    Only build the player into this checkout's Builds\Windows: no pull, no shortcut, nothing published or
    launched (how a fresh clone proves it builds on its own).

.NOTES
    This script never prompts, so agents and /deploy can run it unattended. The choice between
    deploying and just running lives in the repo-root deploy.bat that the Desktop shortcut opens.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File run.ps1
    powershell -ExecutionPolicy Bypass -File run.ps1 -Launch
    powershell -ExecutionPolicy Bypass -File run.ps1 -Force -Launch
    powershell -ExecutionPolicy Bypass -File run.ps1 -LauncherOnly
#>
param([switch]$Launch, [switch]$SkipHdrp, [switch]$Force, [switch]$LauncherOnly, [switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoDir  = $PSScriptRoot   # the repo root (SpaceGod.Unity.CSharp\)
$out      = 'C:\Apps\SpaceGod.Unity.CSharp'
$exeName  = 'Unity.SpaceGod.exe'
$buildDir = Join-Path $repoDir 'Builds\Windows'
$deployBat = Join-Path $repoDir 'deploy.bat'
$marker    = Join-Path $out '.deploy-commit'

# Points at deploy.bat (not the exe directly), whose menu asks first: 1) Deploy & Run (pull latest,
# rebuild, launch) or 2) Run (start the published copy as it is).
# GetFolderPath, not $HOME\Desktop: OneDrive may have moved the Desktop folder.
function Set-Shortcut([string]$exe) {
    $shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SpaceGod – Unity C#.lnk'
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath       = $deployBat
    $link.WorkingDirectory = $repoDir
    if (Test-Path $exe) { $link.IconLocation = "$exe,0" }
    $link.Description      = 'SpaceGod – Unity C# (SpaceGod.Unity.CSharp): 1) Deploy & Run or 2) Run'
    $link.Save()
    Write-Host "    Shortcut -> $shortcut" -ForegroundColor Gray
}

# The shortcut is refreshed on every run, so an existing install picks up a changed launcher
# without a republish; -LauncherOnly does just that and stops.
if (-not $BuildOnly) { Set-Shortcut (Join-Path $out $exeName) }
if ($LauncherOnly) { exit 0 }

# ── Pull latest, skip the rebuild entirely if nothing changed ──────────────
# Untracked files (e.g. .prose/ session state) don't block a pull; changes to tracked files (staged
# or not) skip it, as does a pull that fails: the desktop shortcut runs this, and the ticket tools
# keep docs/USER_STORIES.md modified, so throwing here left the game unopenable (U.SPC-T368). It then
# deploys the commit already checked out.
$dirty = git -C $repoDir status --porcelain | Where-Object { $_ -notmatch '^\?\? ' }
if ($BuildOnly) {
    Write-Host "  -BuildOnly: building the commit checked out, no pull." -ForegroundColor DarkGray
} elseif ($dirty) {
    Write-Host "  $repoDir has uncommitted changes - not pulling; deploying the current commit." -ForegroundColor DarkYellow
} else {
    git -C $repoDir pull --ff-only
    if ($LASTEXITCODE -ne 0) { Write-Host "  git pull --ff-only failed in $repoDir - deploying the current commit." -ForegroundColor DarkYellow }
}
$head = (git -C $repoDir rev-parse HEAD).Trim()

# Up to date when nothing the player is built from (Assets, Packages, ProjectSettings) differs between
# the deployed commit and HEAD: a docs- or tickets-only commit doesn't rebuild the game. -Force skips this.
$upToDate = $false
if (-not $Force -and -not $BuildOnly -and (Test-Path (Join-Path $out $exeName)) -and (Test-Path $marker)) {
    $deployed = (Get-Content -LiteralPath $marker -Raw).Trim()
    git -C $repoDir diff --quiet $deployed $head -- Assets Packages ProjectSettings 2>$null
    $upToDate = $LASTEXITCODE -eq 0
    if ($upToDate -and $deployed -ne $head) { Set-Content -LiteralPath $marker -Value $head -NoNewline }
}
if ($upToDate) {
    Write-Host "  Already deployed at $head (no player source changed) - skipping rebuild." -ForegroundColor Green
    if ($Launch) { Start-Process -FilePath (Join-Path $out $exeName) -WorkingDirectory $out }
    exit 0
}

# ── Resolve Unity editor ────────────────────────────────────────────────────
$ver = (Select-String -Path "$repoDir/ProjectSettings/ProjectVersion.txt" -Pattern "m_EditorVersion: (.+)").Matches[0].Groups[1].Value.Trim()
$unity = "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe"
if (-not (Test-Path $unity)) { throw "Unity $ver not installed at $unity" }
$projName = Split-Path -Leaf $repoDir   # the Editor title carries the folder name
if (Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$projName*" }) {
    throw "$projName is open in the Unity Editor. Close it (File > Quit) and re-run."
}

# ── Stop running instance ──────────────────────────────────────────────────
Write-Host ''
Write-Host '  Stopping running instance...' -ForegroundColor Yellow
# Only this copy's game (its build output or its published folder), never the original repo's deployed one.
$procs = Get-Process 'Unity.SpaceGod' -ErrorAction SilentlyContinue | Where-Object {
    $path = try { $_.Path } catch { $null }
    $path -and ($path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($buildDir, [StringComparison]::OrdinalIgnoreCase))
}
if ($procs) {
    $procs | Stop-Process -Force
    Write-Host "    Stopped ($(@($procs).Count) process(es))" -ForegroundColor DarkYellow
    Start-Sleep -Milliseconds 800
} else {
    Write-Host '    Nothing running.' -ForegroundColor DarkGray
}

# ── Clear build output ───────────────────────────────────────────────────────
# Builds\Windows only — never Library\, which is Unity's import/compile cache, not
# build output, and must survive a redeploy exactly as it survives a normal rebuild.
Write-Host ''
Write-Host '  Clearing build output (Builds\Windows)...' -ForegroundColor Cyan
if (Test-Path $buildDir) {
    Remove-Item -Recurse -Force $buildDir
}

# ── Build ────────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Building Windows player (Unity batch mode)...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force "$repoDir/Logs" | Out-Null
$logFile = "$repoDir/Logs/deploy-build.log"
# Start-Process joins these with bare spaces, so paths are quoted here or a space in one would split it.
$uargs = @("-batchmode", "-quit", "-projectPath", "`"$repoDir`"", "-executeMethod", "Unity.SpaceGod.EditorTools.ProjectSetup.SetupAndBuild", "-logFile", "`"$logFile`"")
# The same build makes the HDRP player in Builds\Windows\HDRP (U.SPC-T430) unless told not to.
if ($SkipHdrp) { $uargs += "-skipHdrp" }
$p = Start-Process -FilePath $unity -ArgumentList $uargs -Wait -PassThru -NoNewWindow
$errors = Select-String -Path $logFile -Pattern "error CS\d+" -ErrorAction SilentlyContinue | Select-Object -First 40
if ($errors) { $errors | ForEach-Object { Write-Host $_.Line -ForegroundColor Red } }
if ($p.ExitCode -ne 0) {
    Write-Host "Build failed (exit $($p.ExitCode)). Log: $logFile" -ForegroundColor Red
    exit $p.ExitCode
}

$builtExe = Join-Path $buildDir $exeName
if (-not (Test-Path $builtExe)) {
    Write-Host "Build completed but $exeName not found at $builtExe." -ForegroundColor Red
    exit 1
}

if ($BuildOnly) {
    Write-Host "  Built $builtExe (-BuildOnly: not published)." -ForegroundColor Green
    exit 0
}

# ── Publish ─────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host "  Publishing $builtExe" -ForegroundColor Cyan
Write-Host "    -> $out\$exeName"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item -Recurse -Force (Join-Path $buildDir '*') $out

$exe = Join-Path $out $exeName
if (-not (Test-Path $exe)) {
    Write-Host "Publish completed but $exeName not found at $exe." -ForegroundColor Red
    exit 1
}

Set-Content -LiteralPath $marker -Value $head -NoNewline

# ── Desktop shortcut ─────────────────────────────────────────────────────────
Set-Shortcut $exe

# ── Summary ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Published successfully.' -ForegroundColor Green
Write-Host "    Exe : $exe ($([math]::Round(((Get-Item $exe).Length / 1MB), 1)) MB)" -ForegroundColor Gray
Write-Host ''

if ($Launch) {
    Write-Host '  Launching...' -ForegroundColor Cyan
    Start-Process -FilePath $exe -WorkingDirectory $out
}

# An explicit success code: nothing above sets $LASTEXITCODE (Unity runs via Start-Process), so a
# caller checking it (launch.ps1) would otherwise read an empty value as a failure.
exit 0
