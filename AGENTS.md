# MindAttic project agent entrypoint

Read the shared protocol at ..\mindattic-agent-standard\AGENTS.md and this project's own
documentation. Common prompt commands are implemented by the shared runner; do not add a second
copy under a provider-specific command folder.

Hold all work to the design principles in README.md ("Design principles").

A player-facing feature is not done until the in-game manual covers it (U.SPC-T497): write or update
its topic in the chapter it belongs to in `Assets/UnitySpace/Core/GameManual.cs`, and give any new key
its line in `Assets/UnitySpace/Core/ControlsHelp.cs` (the manual prints those, so a key is written once).
