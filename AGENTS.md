# MindAttic project agent entrypoint

Read the shared protocol at ..\mindattic-agent-standard\AGENTS.md and this project's own
documentation. Common prompt commands are implemented by the shared runner; do not add a second
copy under a provider-specific command folder.

Hold all work to the design principles in README.md ("Design principles").

This repo is **SpaceGod.Unity.CSharp** ("SpaceGod – Unity C#"), one of three self-contained candidate copies of
SpaceGod (with SpaceGod.Unreal.CSharp and SpaceGod.Unreal.Cpp). Keep it self-contained: never reference a sibling
repo by path. The MindAttic packages are embedded under `Packages/` (re-vendor per `Packages/VENDORED.md`). Deploy
with the root `run.ps1`; `deploy.bat` is the Desktop shortcut's menu. `tools/bench.ps1` must keep the five digests
all three copies share. `docs/PARITY.md` records the bug fixes shared with the Unreal copies and the co-op port (`tools/coop-smoke.ps1` must pass its 15 checks).

A player-facing feature is not done until the in-game manual covers it (U.SPC-T497): write or update
its topic in the chapter it belongs to in `Assets/UnitySpace/Core/GameManual.cs`, and give any new key
its line in `Assets/UnitySpace/Core/ControlsHelp.cs` (the manual prints those, so a key is written once).
