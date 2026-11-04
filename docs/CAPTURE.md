# Capture: recording SpaceGod through MindAttic.Capture (US-T72)

The one spec all three copies follow: SpaceGod.Unity.CSharp (the reference, written here first),
SpaceGod.Unreal.CSharp and SpaceGod.Unreal.Cpp. A copy that differs from this file is wrong. Change it here first,
then in the other two.

Capture is a development tool for footage (trailers, devlogs, bug repro). It is **off unless `-capture` is literally
on the command line**: a plain launch is a plain launch in every copy, nothing is auto-loaded and there is no
environment variable. Everything below is an observer: no event, rule or hotkey changes the game, the Bench's five
digests stay identical, and the co-op smoke stays 15/15.

## 1. Flags

| Command line | What happens |
|---|---|
| `-capture` or `-capture video` | Records the game from start to exit at its native resolution (lossless FFV1 tape), exported on exit as `.mkv` + `.mp4`. |
| `-capture screen N` | A PNG every N (`500ms`, `5`, `10s`, `1m`) until exit. Combine: `-capture video screen 10`. |
| `-capture video hq` | Hardware HEVC tape instead of lossless. |
| `-capture video 8h` | Stops recording after that long (MindAttic.Capture's shut-off valve can stop it sooner). |
| `-capture-config <path>` | The player's own capture.json, used **instead of** the game's shipped defaults (section 4). |
| `-demo` with `-capture` | The scripted tour (CaptureRunner/SceneTour) plays and quits as before; with `-capture` on, each tour stop's screenshot goes through MindAttic.Capture (`ScreenshotAsync(<stop name>)`) into the session folder. Without `-capture` the tour still plays, with no screenshots. |
| `-capture` and saves | Autosave is off while `-capture` (or `-demo`) is on, as before (UnitySpaceApp.Saves `InitSaves`). Manual saves still work and raise `game-saved`. |
| `-capturedir=<dir>` | Unreal copies only, kept for tools/play.ps1: where their tour PNGs also go. Not a MindAttic.Capture flag. |

Anything after `-capture` that is not a capture word is left for the game, as MindAttic.Capture does.

## 2. Output

- Root: `R:\MindAttic\Capture\{RepoName}\`. If `R:` is missing: `%USERPROFILE%\Videos\MindAttic\Capture\{RepoName}\`.
- `{RepoName}` is the copy's own repo name: `SpaceGod.Unity.CSharp`, `SpaceGod.Unreal.CSharp`, `SpaceGod.Unreal.Cpp`,
  so the three never write into one folder. Unity stamps it into `StreamingAssets/mindattic-capture-app.txt` at build time.
- Per session, MindAttic.Capture's standard layout: the session video (`.mkv`, `.mp4`), every screenshot, clip and
  replay (`.png`, `.mp4`), a `.json` sidecar beside each file, `session.json` (the manifest, with every marker as a
  chapter), `{RepoName}_{start}.log` and `.stats.csv`.
- Every game event (section 3) is also written to the game's own log as one line, so the three copies can be compared
  without opening a video:
  `[capture] event <name> k1=v1 k2=v2 ...`, with keys in the catalogue's order and values formatted invariant-culture,
  numbers as integers. It is written only while capture is running.
  - **Quoting:** a value that is empty, or holds a space or a `"`, is written in double quotes, with any inner `"`
    doubled: `system="Alpha Centauri"`, `from=""`.
  - **Enums:** written by member name, e.g. `class=Frigate` (ShipClass), `kind=Ship` (UnitKind).
  - **Hotkeys:** each one also logs `[capture] hotkey <command>`.

## 3. Gameplay events

Raised through `CaptureEvents` (Unity: `Runtime/CaptureEvents.cs`). Each one goes to `Capture.Raise(name, data)`, so
the rules in section 4 can act on it, and to the `[capture] event` log line. Every event exists in all three copies
with the same name, the same fields in the same order, and the same values.

**The helm hull** below means `World.PilotedShip` of the active system: the ship being flown. In co-op that is the
party's one hull.

**Co-op:** every peer raises the events from its own simulation, since each records its own screen. The simulation is
lockstep, so both peers see the same events at the same frame. The exceptions are `hail`, raised only by the peer
whose player hailed, and the co-op events, where each peer raises what it saw.

**Polled events:** these are found by comparing state once per frame, after the frame's update (`CaptureEvents.Tick`).
The comparison is reset whenever the galaxy is rebuilt (`GalaxyRebuilt`: a load or a co-op sync), so a load never
produces arrival, warp or battle events.

| Event | Fires when | Data (in this order) |
|---|---|---|
| `battle-started` | Polled. The helm hull's `Battle` goes from none to a battle. | `battle` (Battle.Name), `system` (system name), `ship` (helm hull name) |
| `battle-won` / `battle-lost` / `battle-drawn` | Polled. The battle recorded at `battle-started` is `Over`. Won when `Winner` is the side the helm hull fought on (`BattleUnit.FoughtOn` of its unit, read at start), lost when it is the other side, drawn when it is -1. Fires once per battle, even if the helm hull died in it. | `battle`, `system`, `ship` |
| `ship-destroyed` | Polled. A unit of that battle becomes `Dead` while the battle is followed (the helm hull's own unit included). | `name` (BattleUnit.Name), `kind` (UnitKind), `side` (`ours` when the unit fought on the helm hull's side, else `theirs`), `battle` |
| `player-ship-destroyed` | Polled. The helm hull's `Destroyed` goes from false to true. | `ship`, `class` (ShipClass), `system` |
| `gate-departure` | The helm flies through a jump gate: `UpdateGateActivation`, right after `GateTransit.TryPerform` succeeds (before the far system is activated). AI hulls reaching gates (`World.ReachedGate`) are not events. | `ship` (the hull that went in), `gate` (gate name), `system` (the system left), `to` (destination system) |
| `system-arrival` | Polled. `ActiveSystem` changes, not through a rebuild: a gate crossing, FTL arrival, party trip. | `system` (new), `from` (old), `ship` (helm hull, or empty) |
| `orbit-arrival` / `orbit-departure` | Polled, for the same helm hull as last frame. Its `Phase` becomes `Parked` from another phase, or leaves `Parked`. | `ship`, `body` (catalogue name of `Orbit.Body`; for a departure, the body it was parked at) |
| `trade` | Hud `ClickBuy` / `ClickSell` after the market answers, with crates > 0. These run on every co-op peer at the entry's frame. | `side` (`buy` or `sell`), `good` (GoodInfo name), `crates` (crates moved), `market` (settlement name) |
| `hail` | `HailPolity` once a hail is valid, before co-op routing. | `from` (faction name), `to` (faction name) |
| `war-declared` | `World.Logged` in the active system's World, an entry of kind `Diplomacy` whose text contains ` declares war on `. | `text` (the log line) |
| `peace` | The same, for text matching `make peace` or `makes peace`. | `text` |
| `star-discovered` | `Galaxy.StarDiscovered`. | `star` (name), `by` |
| `time-warp` | Polled. `Clock.Warp.Index` changes. | `index` (warp index), `label` (Warp.Label) |
| `game-saved` | `SaveTo(slot)` succeeds. | `slot` (`SlotName(slot)`) |
| `game-loaded` | `LoadFrom(slot)` succeeds, on the peer that loaded. | `slot` |
| `coop-joined` | CoopPeer, on the host: a player joins (`"<name> joined as player N"`). | `player` (name), `seat` (1-based) |
| `coop-left` | CoopPeer, on the host: a player's link is dropped (`"<name> (player N) left"`). Saying bye comes first and is not an event of its own. | `player`, `seat` |
| `coop-resync` | CoopPeer: the host sends sync #n, or the client loads sync #n. | `sync` (number), `frame`, `reason` (host only; the client sends an empty string) |

**Order within one frame** (a log compared across copies depends on it): the direct events as their code runs, then
the polled ones in this order: `system-arrival`, `time-warp`, `player-ship-destroyed`, `orbit-arrival`/`orbit-departure`,
`battle-started`, `ship-destroyed` (in `Battle.Units` order), then the battle's end.

**Battle following:**
- One battle is followed at a time, the helm hull's. It is dropped without an event when the active system changes.
- Units already dead when it starts are not reported.
- The helm hull's side is `-1` if its unit is not found. Then every unit is `theirs` and a battle with a winner is `battle-lost`.

**Unity code points** (Assets/UnitySpace/Runtime):

| Hook | Code point |
|---|---|
| `CaptureEvents.Attach` | UnitySpaceApp.cs:430 (end of startup); again on every `GalaxyRebuilt` |
| `Tick` | UnitySpaceApp.cs:434 (`LateUpdate`) |
| hotkeys | UnitySpaceApp.cs:649 (right after `Input.Poll`); Input/InputState.cs:296 |
| `hail` | UnitySpaceApp.cs:249 |
| `gate-departure` | UnitySpaceApp.cs:1639 |
| `game-saved` | UnitySpaceApp.Saves.cs:155 |
| `game-loaded` | UnitySpaceApp.Saves.cs:192 |
| `trade` | UI/Hud.cs:2834 (buy), :2880 (sell) |
| `coop-resync` | Coop/CoopPeer.cs:485 (host), :842 (client) |
| `coop-left` | Coop/CoopPeer.cs:521 |
| `coop-joined` | Coop/CoopPeer.cs:630 |
| polled events, `star-discovered`, `war-declared`, `peace` | CaptureEvents.cs |

Contracts are not in the catalogue. The game has no single seam for accepting or completing a contract that a player
would film. Add one here first if it is wanted.

## 4. Default rules: the shipped capture.json

Every copy ships the same `capture.json`:
- Unity: `Assets/StreamingAssets/SpaceGod.capture.json`, which a player build puts in `<exe>_Data/StreamingAssets/`;
- Unreal: the packaged game's `<Project>/Content/Capture/SpaceGod.capture.json`, staged as a non-UFS file, or beside
  the exe.

**When it applies:** only while `-capture` is on and no `-capture-config` was given. The game loads it into the capture
options before the engine starts (Unity: `CaptureEvents` at AfterSceneLoad, applying `ApplyConfigFile`). With
`-capture-config <file>` the player's file is used instead, never both. Without `-capture`, nothing is read.

**Replay:** the file arms the 30 s replay ring. The game keeps it armed only when the session is recording video, so
`-capture screen N` alone never starts a tape.

```json
{
  "replay": { "armed": true, "seconds": 30 },
  "rules": [
    { "on": "player-ship-destroyed", "action": "replay", "before": "20s", "after": "5s", "tag": "ship-lost", "cooldown": "10s" },
    { "on": "battle-won", "action": "replay", "before": "25s", "after": "3s", "tag": "victory", "cooldown": "10s" },
    { "on": "battle-lost", "action": "replay", "before": "25s", "after": "3s", "tag": "defeat", "cooldown": "10s" },
    { "on": "system-arrival", "action": "screenshot", "tag": "arrival", "cooldown": "5s" },
    { "on": "star-discovered", "action": "screenshot", "tag": "discovery", "cooldown": "5s" },
    { "on": "*", "action": "marker" }
  ]
}
```

## 5. Hotkeys

Active only while capture is running (`Capture.Current.Enabled`). Read through the game's own input layer
(`InputState`), not as global Windows hotkeys. The capture.json `hotkeys` map is not used: the Unity package registers
those before game code can add to them, and only the game's input can stop the plain key's action as well.

| Keys | Command |
|---|---|
| **Alt+F9** | `screenshot` |
| **Alt+F10** | `toggle-recording` (an on-demand recording cut from the tape, exported when toggled off) |
| **Alt+F11** | `save-replay` (the last 30 s) |

**Why Alt, not Ctrl:**
- F9 (take-charge picker), F10 (fleet), F11 (clear screen) and F12 (hail) are game keys.
- Ctrl is a game key too. Holding it is `Slow`, and pressing it is `DropMine`, so Ctrl+F9 would drop a mine in battle
  before the screenshot.
- Alt alone is bound to nothing; the game reads it only as part of Shift+Alt+K (sky back).

**While capture is running and Alt is held:** F9, F10 and F11 do only the capture command. The picker, fleet screen
and clear screen do not toggle, because `InputState` clears `TogglePicker`, `ToggleFleet` and `ClearScreen` on that
frame.

**Without capture:** Alt+F9/F10/F11 behave exactly as before; Alt is ignored and the plain key acts.

**Scope:** these commands are local to the machine that pressed them. They are not co-op input and are never sent to
other peers.
- `InputState.Reset` clears them like every other flag.
- In Unity they are properties, not fields, because co-op's `PilotFrame` carries every public field of `InputState` to
  the other peers.
- Each copy keeps them out of its co-op input frame in its own way.

## 6. Build gating

- Capture code runs only in builds with the `MINDATTIC_CAPTURE` define.
- The published copies are **capture-enabled**: run.ps1's builds define it, so the Desktop shortcut's game honours
  `-capture`. With no `-capture` on the command line it does nothing extra: no ffmpeg and no files.
  - Unity: `ProjectSetup.Build` passes `extraScriptingDefines = { "MINDATTIC_CAPTURE" }` to both players.
  - Unreal C#: the vendored capture assemblies are referenced in every configuration the published game uses.
  - Unreal C++: the capture backend is compiled in all configurations.
- **Prerequisite: ffmpeg** on PATH or beside the exe. It is needed for video and replays; screenshots alone don't use
  it. It is not bundled with any copy, because the common Windows builds are GPL.
- **Missing ffmpeg:** video is skipped and one error is logged. Without `capture.exe` installed, the session export
  runs in the game at exit instead of in a detached process.

## 7. Scripted check

`tools/coop-smoke.ps1 -CaptureHost` (Unity) runs the co-op smoke with `-capture video` on the host, so one scripted
session raises most of the catalogue on the host: `coop-joined`, `coop-resync`, `trade`, `system-arrival` (the party trip),
`game-loaded`, `time-warp` and `coop-left`. The smoke's hail is the client's, so `hail` is raised on the client, not
the host (only the peer whose player hailed raises it). The Unreal copies give their own smoke the same switch.

## 8. Proof

Each copy, after a change to anything here:
- **Bench:** the Bench's five digests.
- **Audit:** 80 PASS, 0 FAIL, 5 INFO.
- **Co-op smoke:** 15/15.
- **Recording:** a short `-demo -capture video screen 10` run. Its session folder must hold the `.mkv`/`.mp4`, the
  PNGs, the sidecars and a `session.json` with markers. Its log must have `[capture] event` lines.
