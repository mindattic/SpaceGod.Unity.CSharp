# Parity with the Unreal copies

SpaceGod.Unity.CSharp is the Unity original (Unity.SpaceGod at `45dc06b`), made self-contained. It is one of three
candidate copies of the same game, next to SpaceGod.Unreal.CSharp (UnrealSharp running this C# code) and
SpaceGod.Unreal.Cpp (the Core translated to C++). The Unreal ports found some genuine bugs in the original while
porting it. This page lists those bugs and what happened to each one here. Per the faithful-port rule, only bugs are
fixed and gameplay is never changed.

## Bug fixes ported from the Unreal tracks

| Fix | Where | Found by |
|---|---|---|
| **A gone escort keeps its leader's motion** (the high-warp crash: `Fleet.StateOf` threw every frame at 1 month/s and 1 yr/s). `Ship.Tombstone` and `Escorts.PayOffOrphans` call `Ship.TakeMotionOf(leader)` before the leader is cleared. | `Assets/UnitySpace/Core/Ships.cs`, `Core/Economy/Escorts.cs` | Unreal.SpaceGod US-T59, already in Unity.SpaceGod as U.SPC-T602 (`45dc06b`) |
| **The player's own gate transit no longer throws every frame after the jump.** `Activate` on the far system runs `Galaxy.Promote`, which steps that system's fleet. The hull `GateTransit.TryPerform` had just parked there (`DestinationBody` -1, its phase already over) was not yet the far world's piloted ship, so it departed for body -1 and `BodyCatalog`'s indexer threw. The far world now holds it as its piloted ship before `Activate`. | `Assets/UnitySpace/Runtime/UnitySpaceApp.cs` (UpdateGateActivation) | Unreal.SpaceGod US-T58 (both tracks) |
| **Saves no longer name live objects through weak-table caches.** The static-object walk (`ObjectGraph.Statics.Ensure`) enumerated two `ConditionalWeakTable` caches keyed by live simulation objects (`FlightDecks.decks`, `ArmourAngling.Tables`). It named live battle units, ships, factions and worlds by cache paths that differ in every process, so a fresh process could not load a save made after `Galaxy.CreateDefault` ("The save refers to a static object this build does not have"). The walk now leaves those caches out, as the Unreal C# track's SaveStatics shim and the C++ Core's walk do. | `Assets/UnitySpace/Core/Persistence/ObjectGraph.cs` | Unreal.SpaceGod US-T41 (docs/PERSISTENCE.md there) |
| **F12's hail edge is cleared with the others.** `InputState.Reset` left `HailPolity` alone, so a suspended frame (Poll returns right after Reset) kept the last frame's hail. | `Assets/UnitySpace/Runtime/Input/InputState.cs` | the Unreal C# track's InputState port |

The Bench's five digests are the same after these fixes (tools/bench.ps1). The fixes only touch saves, the
player's gate transit and an input edge, none of which the headless scenarios reach.

## Unreal-only, not ported (engine adaptations, not bugs in the original)

These show up as differences between the Unreal copies and this one, but they are not bugs in the original:

- **The UnityCompat facade and UnrealHost:**
  - IMGUI on Slate (US-T28, US-T54);
  - the input device seam (`IInputDevice`);
  - the render backend on Unreal's post-process (US-T27, US-T51, US-T55, US-T57);
  - near-plane and camera mapping (US-T40, US-T45);
  - the master volume reaching Unreal (US-T50);
  - display refresh rates through DXGI (US-T56).
- **Scene loading:** `GameLaunch.SceneLoader`, `Bootstrap` and `SessionAudio.OnLoad` replace Unity's scene loads and
  `RuntimeInitializeOnLoadMethod` hooks.
- **Unreal's own harness tools:**
  - the race probes and tour (US-T46);
  - the screenshot runs' fixed start (US-T45);
  - FidelityShots, UiSmoke, ViewsSmoke (US-T40). FeatureAudit / DisplayAudit / SettingsAudit (US-T48, US-T50, US-T51,
    US-T55, US-T56, US-T58) **now run here too** (US-T70, below).
- (Cross-track saves, `SaveCompat`, were left out by US-T69; **US-T70 brought them in**, below.)
- **Unreal-specific bugs:** packaging and cooking (US-T61, US-T65, US-T66, US-T67) and the C++ build fixes (US-T49,
  US-T52, US-T53, US-T63).

## Co-op (ported from the Unreal C# track)

Both Unreal copies add one feature to the faithful port, and this copy now has it too: **cooperative multiplayer**
(Unreal.SpaceGod US-T39, US-T43, US-T47, US-T64, US-T67). It is host-authoritative lockstep over the original's own
Session layer. Each host frame's clock and commands go out, every peer replays them, and the world digests are compared
every 60 frames. A join, a load on the host or a desync sends the host's save to everyone.

**Ported line for line** from Unreal.SpaceGod `CSharp/Script/.../Runtime`:

- `Coop/` (CoopPeer, CoopWire, CoopInput, CoopSettings, CoopSmoke);
- `UnitySpaceApp.Coop.cs`, `UI/Hud.Coop.cs` and `UI/GalaxyMapScreen.Coop.cs`;
- every hook marked US-T39/T43/T47 in UnitySpaceApp (with Helm), Embodiment, Course, Ftl, Practice, Saves, Hud,
  Hud.Trade, GalaxyMapScreen and GameLaunch.

The only changes are logging (`Debug.Log`) and `Application.Quit`. The save-statics prime calls `SaveStatics.EnsureFixed`
as there since US-T70 (a no-op rebuild of the same table here, the weak-table fix being in this Core too).

**New here, in place of Unreal's networking:**

- **`Coop/CoopNet.cs`:** one TCP connection per client (System.Net.Sockets). Each message is length-prefixed UTF-8. A
  background reader feeds a main-thread queue, so CoopPeer runs on the main thread as it did under Unreal. This class
  does what `ASpaceCoopLink` and the game mode's co-op hooks did there.
- **The client's frame loop:** a client's `Update` runs only when CoopNet replays a host frame, on that frame's seconds
  (`UnitySpaceApp.CoopNet.cs`), up to 8 frames per Unity frame. Under Unreal this was `UnityBridge.Tick(rec.Dt)`.
- **Input:** `InputState` reads through the `IInputDevice` seam the Unreal ports added (`Input/InputDevice.cs`): the
  Input System's keyboard and mouse, or the co-op smoke's script.
- **Command line, as on the Unreal copies:**
  - `-CoopHost` (with `-port=`, default 7777);
  - `-CoopJoin=<address[:port]>`;
  - `-CoopName=`;
  - `-SpaceGodCoopLog=<file>`;
  - `-SpaceGodCoopSmoke=host|client`.

  The Unreal copies have no in-game menu entry for co-op either.
- **Smoke test:** `tools/coop-smoke.ps1 [-PlayerDir <dir>]` runs a host and a client of a built player, both muted and
  windowed, under the heavy-job lock. It applies `tools/play.ps1 -Mode coop-smoke`'s 15 checks, including that every
  digest the host and client both logged is identical.
## US-T70: functionally the same as SpaceGod.Unreal.CSharp

Every file was compared against SpaceGod.Unreal.CSharp and each difference classified; SpaceGod.Unreal.CSharp's
`docs/PARITY.md` has the full table, file by file. In short:

- **Core:** this copy's `Assets/UnitySpace/Core` and that copy's `Vendor/Unity.SpaceGod/Core` are **byte-identical**
  (184 files). Its `ObjectGraph.cs` was re-vendored from here.
- **Shared core:** byte-identical too.
- **Runtime:** 36 files identical. The other 32 differ only in engine plumbing (scene loads, the input device, the
  logger, the camera facade, the capture tool). Every key of the `Key` enum maps to the same physical key there.
- **Same code on both sides:** the HUD, every screen and menu, the views, the settings declarations and the co-op layer.
- **(c) behaviour differences:** none in the game. The engine-level ones:
  - **Render pipeline:** URP, or HDRP through a restart into the HDRP player. Unreal has one renderer and keeps the
    choice without restarting.
  - **Co-op transport:** TCP here, Unreal replication there.
  - **Visual-only timers on a co-op client:** they run on the client's clock here and the host's seconds there.

What US-T70 changed here:

| Change | Files | Why |
|---|---|---|
| **Bug fix:** the pointer-over-UI test reads `Input.Device` instead of `Mouse.current` | `Runtime/UnitySpaceApp.cs` (`IsPointerOverUi`) | A scripted device (the co-op smoke's client, the audit) was tested against wherever the real mouse was. The Unreal copies read the device. |
| **Saves from the C++ copy load here:** SaveCompat copied in unchanged, with its two hooks (`InitSaves`, `ApplyLoaded`) and the co-op prime | `Runtime/SaveCompat/SaveStatics.cs`, `SaveWiring.cs`; `UnitySpaceApp.Saves.cs`; `Coop/CoopPeer.cs` | The Unreal C# copy already read the C++ copy's saves (which carry no delegates); now both C# copies do. |
| **The audit runs here:** `-SpaceGodSmoke` / `-SpaceGodAudit`, `tools/audit.ps1` | `Runtime/Audit/AuditRunner.cs`, `FeatureAudit.cs` (verbatim, but for the logger and `Time.timeScale`), `SettingsAudit.cs`, `DisplayAudit.cs`; `GameLaunch.SkipsMenu`; the app's dt hook (`CoopUnscaledDeltaTime`) | Lets the three copies run the same probes, with the same ids, and compare the logs row for row. |

### How the audit runs under Unity

The Unreal smoke is a straight-line script that steps the player loop itself (`Frames(n)`). Unity owns its loop, so
`AuditRunner` runs the same script on a worker thread. The worker and Unity's main thread take turns, never at the same
time.

- **Frames:** each frame the script asks for is a real Unity frame at a fixed 1/60 s, the Unreal smoke's step
  (`Time.captureDeltaTime` and the app's own dt).
- **Engine calls:** what needs Unity's main thread runs there between frames (`AuditRunner.OnMain`): `Time.timeScale`,
  the audio listener, settings that apply to the engine, and save and load.
- **What the settings and display probes read here:**
  - `audio.*` reads the audio listener.
  - `render.*` reads the URP volume on the game's camera, where the Unreal copies read Unreal's post-process.
  - `render.msaa` reads the camera's MSAA flag and the URP asset's 4 samples.
  - `display.modes` is the Unreal probe's own code; `Screen` is Unity's own here.
  - `display.exclusive-rate` reads the rate `DisplayOptions.Apply` would ask for from the original's `TryFindMode`.
    Unity has no read-back of a requested rate, and applying exclusive fullscreen would switch the player's monitor.
- **Mode:** it runs headless (`-batchmode -nographics`), as the Unreal audit runs `-nullrhi`.

Result (2026-11-04, `tools/audit.ps1` on a player of `e5efa29`): **80 PASS, 0 FAIL, 5 INFO**, the Unreal C# copy's figures.
The 5 INFO are the same keys, silent for a practice fighter on every copy: helm.ftl-engage, lane-reenter, phasers,
run-hot and lock. Its first run here also found the thumbnail bug below.

Commits (US-T70):

| Commit | What |
|---|---|
| `ad968a1` | the pointer-over-UI test through `Input.Device` |
| `86d6834` | **bug fix:** the spawn screen's ship thumbnails. Asked from `OnGUI`, which Unity 6 runs inside URP's camera stack, `ShipThumbnails`' manual `Camera.Render` threw ("UniversalCameraData has already been created") and cached a blank snapshot for good, so the Spawn & Pilot grid drew no hulls. It now renders at the next `LateUpdate`. The Unreal copies never had this: their engine draws the UI outside such a pass. |
| `0093ab7` | SaveCompat: saves from the C++ copy load here |
| `e5efa29` | the audit (`Runtime/Audit/`, `tools/audit.ps1`) |

Proof after these: the Bench gives the 5 digests; the co-op smoke passes 15/15 checks with 55 of 55 digests identical
and 0 exception lines; the player log of the audit run has no exceptions.
