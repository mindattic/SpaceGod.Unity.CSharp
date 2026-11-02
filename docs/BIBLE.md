---
codex: 1
project: Unity.SpaceGod
code: U.SPC
layer: bible
status: living
updated: 2026-11-02
---

# Unity.SpaceGod — Project Bible

> The rules that keep Unity.SpaceGod coherent. [README.md](../README.md) says what the game is and how to build and run
> it (its "Design principles" hold every change); this file holds the laws a change must not break. The design canon for
> each system lives in its own document under `docs/` ([EMBODIMENT.md](EMBODIMENT.md), [FLIGHT_MODELS.md](FLIGHT_MODELS.md),
> [SHIP_ROSTER.md](SHIP_ROSTER.md), [BOARDING.md](BOARDING.md)).

## Laws {#U.SPC-laws}

This project inherits the org-wide laws in [MindAttic.HouseRules.md](../../MindAttic.HouseRules.md).

### {#U.SPC-LAW-1} Multiplayer-ready
This game is built so co-op or PvP can be added without re-architecting, per Unity.Multiplayer's
[Multiplayer-ready standard](https://mindattic.github.io/Unity.Multiplayer/articles/multiplayer-ready.html):
every player intent is a serializable command stamped with a player id and submitted through `ISession`; one
authority owns game state and presentation only reads state and events; there is no global "the player", only
player ids and `ISession.LocalPlayer`; rules are deterministic (seeded RNG, fixed tick, no `UnityEngine.Random` or
frame time); entities have stable ids; state and commands serialize; the game runs on a `LocalSession` behind
`ISession`; and a test plays commands from two player ids. A change that breaks any of these is a bug.

Where Unity.SpaceGod stands (U.SPC-T410, 2026-11-02; no netcode package is added and none is planned yet):

- **The seam: in place.** `Core/Session`: `ISession` (`LocalPlayer`, `Players`, `State`, `EventRaised`, `Submit`,
  `IdOf`) and `LocalSession`, which owns a `SeatAuthority` in process, queues every command and applies it in order at
  the start of the next step, and raises `SeatTaken`, `SeatReleased` and `CommandRefused` to presentation.
  `LocalSession.Create(seed, players)` takes any number of players, so hot-seat play is the same session.
- **Commands: in place for every seat but the helm.** Taking a fighter's cockpit, a gun mount or a whole station, letting
  go, moving up from a gun to its host, and the stick and triggers each frame are `TakeSeatCommand`,
  `ReleaseSeatCommand`, `MoveToHostCommand` and `SeatControlCommand`, each a plain-data value carrying a `PlayerId`.
  `UnitySpaceApp.Embodiment.cs` only submits them for `session.LocalPlayer` and reads back what the session says that
  player holds; it no longer holds an `EmbodimentSession` of its own. `CommandCodec` writes each command as a line of
  text and reads it back equal.
  *Still direct calls:* the helm's pilot loop (`UnitySpaceApp` piloting writes the flown ship's flight state), the
  cockpit's extra keys (flight assist, lock, weak point, wing orders, power), and the strategy view's orders (warp,
  selection, docking, trade, the dev spawn). Each becomes a command through the same `Submit` when it is next touched.
- **One authority: in place for seats.** `SeatAuthority` alone writes who holds what, each player's own
  `EmbodimentSession`, and the controls each last sent; it references no view. The simulation itself (`World`, `Battle`)
  is engine-free (the `Unity.SpaceGod.Core` assembly is `noEngineReferences: true`) and its views only read it, but Runtime code still
  calls into it directly for the orders listed above.
- **Per player: seats in place; the helm not yet.** Seats are held per `PlayerId`, and two players can hold two seats
  in the same fight. `World.PilotedShip` (read by the war, the event director and the fleet) and `Law.Player` (one crime
  record) are still a single "the player". Keying them by player is deferred: `World`'s field list is part of the save
  format (`ObjectGraph` refuses a save made before a field was added), so it needs its own save-migration ticket.
- **Deterministic: seeded, not yet on a fixed tick.** Every rules generator is a `System.Random` built from a seed held
  in the state (`Fleet`, `War`, `Battle`, the economy's parts, `EventDirector`); `UnityEngine.Random` appears only in
  views (a sun's flares, a glow's phase, a structure's spin). `LocalSession.Step` advances on a fixed 30 Hz tick, which is
  how tests and replays drive it, but the shipped game still advances the world and its seats on frame time scaled by
  the warp (`LocalSession.Advance(simDt)`), exactly as before. Moving the world onto the fixed tick is the remaining
  rule-4 work.
- **Stable ids: in place.** Ships, settlements, battles, lanes, wrecks, contracts and charters carry
  authority-assigned integer ids; a seat gets one from `ISession.IdOf` the first time it is named (seats compare by what
  they control, so the same gun always gets the same id within a session).
- **State serializes: in place.** `SaveGame` and `ObjectGraph` write the whole simulation and read it back exact,
  generators included; commands serialize through `CommandCodec`, and `LocalSession.Log` with the seed is a replay.
- **Two-player test: in place.** `MultiplayerReadyTests`: two players take and fly their own craft in one fight, each
  craft follows only its own player's stick, a command from the wrong player (another's seat, a seat not in the session,
  nobody) is refused, every command round-trips through the codec, and the seed plus the command log replays the same
  fight.
