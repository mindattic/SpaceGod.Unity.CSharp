<#
  UserPromptSubmit hook - answer /deploy and /run on the spot, with no model turn.

  /deploy starts deploy.bat in its own console window (a clean build published to
  C:\Apps\SpaceGod.Unity.CSharp, plus the Desktop shortcut) and returns at once; /run starts the
  published copy. Both block the prompt, so no tool call is made and the orchestrator's pace
  gate never holds them. Any other prompt passes through untouched.
#>
$ErrorActionPreference = 'Stop'

$raw = [Console]::In.ReadToEnd()
try { $j = $raw | ConvertFrom-Json } catch { $j = $null }
$prompt = if ($j -and $j.prompt) { ([string]$j.prompt).Trim() } else { '' }

if ($prompt -notmatch '^/(deploy|run)(\s|$)') { Write-Output '{}'; exit 0 }
$command = $Matches[1].ToLower()

$repoRoot = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
$exe = 'C:\Apps\SpaceGod.Unity.CSharp\Unity.SpaceGod.exe'

function Block([string]$reason) {
  $json = @{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress
  Write-Output $json
  exit 0
}

try {
  if ($command -eq 'deploy') {
    # --deploy skips deploy.bat's 1) Deploy & Run / 2) Run menu (that is for the Desktop shortcut);
    # flags after /deploy (e.g. -SkipHdrp, -Force) pass through to run.ps1.
    $flags = ($prompt -replace '^/deploy\s*', '').Trim()
    $bat = Join-Path $repoRoot 'deploy.bat'
    Start-Process -FilePath $bat -ArgumentList ("--deploy $flags").Trim() -WorkingDirectory $repoRoot
    Block "Deploy started in its own window: clean build, publish to C:\Apps\SpaceGod.Unity.CSharp, Desktop shortcut. /run starts it when done."
  }

  if (-not (Test-Path $exe)) { Block "Not deployed yet ($exe is missing). Run /deploy first." }
  # An agent's /run launches muted (Unity.Shared's session mute, U.SPC-T536); the env var is set only around the launch.
  $previous = $env:MINDATTIC_CLI_MUTED
  $env:MINDATTIC_CLI_MUTED = '1'
  try { Start-Process -FilePath $exe -ArgumentList '-muted' -WorkingDirectory (Split-Path $exe) }
  finally { if ($null -eq $previous) { Remove-Item Env:MINDATTIC_CLI_MUTED -ErrorAction SilentlyContinue } else { $env:MINDATTIC_CLI_MUTED = $previous } }
  Block "SpaceGod - Unity C# started muted from C:\Apps\SpaceGod.Unity.CSharp (tools\launch.ps1 -Sound for audio)."
} catch {
  Block "/$command failed: $($_.Exception.Message)"
}
