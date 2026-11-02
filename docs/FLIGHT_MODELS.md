# Flight models per class

Scoping for U.SPC-T17: each kind of ship flies by its own rules at the helm, not by one shared model with different numbers per class. Feel comes before physical accuracy. The owner reviews and tunes one family at a time, and each family is a ticket of its own (listed under [Work plan](#work-plan)).

This covers handling: how the stick, throttle and keys become motion. Weapons, lock-on, wingmen and attack runs belong to the dogfighting game (U.SPC-T424). Batteries, firing arcs, bracing, damage control and formations belong to the capital broadside game (U.SPC-T425, `Core/Combat/Broadsides.cs`, landed: see the README's Broadsides). Both games are built on the flight models described here.

## Where it stands

- **Flight is Newtonian** (U.SPC-T59, `Core/Combat/PilotFlight.cs`). Every design has a mass, thrusters on each axis and moments of inertia. Flight assist counter-thrusts toward the velocity the stick asks for, and F turns it off so the hull drifts.
- **Each family already has its own controls** (U.SPC-T269, `PilotAim`, `PilotControl`). A light fighter aims freely and the hull turns after the crosshair. A heavy fighter or hauler turns the nose directly with the mouse. A capital ship is steered on the keys at the helm while the mouse looks around.
- **What was still shared** was the motion itself: every class went through the same `PilotFlight.Step`, so the only differences between classes were the numbers in `PilotTuning`.

## The extension point (landed with T17)

`Core/Combat/FlightModels.cs`:

- **`FlightFamily`**: `LightFighter`, `HeavyFighter`, `Hauler`, `Capital`, since U.SPC-T484 `Interceptor` and `Bomber`, and since U.SPC-T444 `Runner`. A family describes a way of flying, not a particular hull.
- **`IFlightModel`**: one `Step` that takes the same arguments `PilotFlight.Step` always took (state, combat, hull dynamics, class tuning, input, gravity, dt). A model is stateless: anything that carries over between frames lives on `PilotState` or `ShipCombat`, so a single instance flies every hull in its family, and saves need no changes.
- **`FlightModelRegistry`**: two data tables, class to family and family to model. `FlightModelRegistry.Default` is the game's own. A family with no model registered flies `Fallback`, which is `NewtonianFlightModel`: the pre-T17 behaviour, unchanged.
- **`PilotFlight`'s stages** (`Rotate`, `DriveMultiplier`, `AssistThrust`, `RawThrust`, `Integrate`) are public. A new model is assembled from these stages plus rules of its own, so it never needs a branch inside `PilotFlight`.
- **The helm** (`UnitySpaceApp`) flies the piloted hull through `FlightModelRegistry.Default.For(class)`.

To add a way of flying: write an `IFlightModel` and its handling struct, then call `Register` for its family. To add a ship class: call `Assign` to put it in a family. Neither change touches the code that flies the other families. A class can also fly its family's rules on numbers of its own: `Register(ShipClass, model)` gives it a model of its own over the family's, which is how each capital class flies `CapitalFlightModel` on its own `CapitalHandling` (U.SPC-T499).

**Tunables are data.** Each model takes a `readonly struct` of handling numbers (for example `LightFighterHandling`) with a `Default`. When the settings registry (U.SPC-T431) lands, each field becomes a declared setting, and the model reads whatever value the registry holds.

**The light/heavy fighter split** is made at the family level. The heavy fighter is the Gunship (U.SPC-T467), an armoured multi-crew hull with two crewed turrets and a spinal light railgun, built for brawling and anti-corvette work. It holds the heavy-fighter slots in patrol wings, escort wings (at the heavy hire fee) and the scene tour that the Breacher used to fill. The Fighter is the light fighter; the Courier is no fighter but a blockade runner, with a family of its own (see [Couriers](#couriers)). The Breacher is not a fighter, and not a ship at all: it is a boarding round fired from a Breacher Bay, with no ship class or flight family (see [Fired rounds](#fired-rounds-the-breacher)).

| Class | Family | Model today |
| --- | --- | --- |
| Interceptor | Interceptor | `LightFighterFlightModel` on `LightFighterHandling.Interceptor` (see [Strike craft](#strike-craft-interceptors-fighters-and-bombers)) |
| Fighter (space superiority) | Light fighter | `LightFighterFlightModel` |
| Bomber | Bomber | `BomberFlightModel` (see [Strike craft](#strike-craft-interceptors-fighters-and-bombers)) |
| Assault shuttle (U.SPC-T486) | Bomber | `BomberFlightModel`: a heavy throttle and a weak strafe for coming alongside a prize (see `BoardingShuttles`) |
| Courier | Runner | `LightFighterFlightModel` on `LightFighterHandling.Runner` (see [Couriers](#couriers)) |
| Gunship | Heavy fighter | Newtonian, until its own model lands |
| Freighter, Miner | Hauler | Newtonian, until its own model lands |
| Carrier, Battleship, Cruiser, Destroyer (and the Command cruiser and Frigate) | Capital | `CapitalFlightModel`, each class on its own `CapitalHandling` (see [Capital ships](#capital-ships-black-flag-or-rebel-galaxy)) |

With flight assist off, every family flies the Newtonian hull. Drifting and turn-and-burn remain available to every pilot. The family models change only what the flight computer does with assist on.

## Light fighters: Star Conflict

**Implemented** (`LightFighterFlightModel`, tests in `Tests/Core.Tests/FlightModelTests.cs`).

The reference is Star Conflict's interceptors and fighters: fast, twitchy, arcade-coupled flight in which the ship goes where it points, sideways dodges matter as much as turning, and the pilot keeps the crosshair on target by carving tight circles rather than by sliding. The aiming half (the crosshair runs ahead and the hull chases it) already shipped in T269. The motion half follows these rules:

1. **The track carves after the nose.** With assist on, the flight computer can push across the nose far harder than the manoeuvre thrusters could. The `Grip` value is scaled so that at 1, the track holds to the nose through the tightest turn at the corner speed. A Newtonian Fighter breaking through 90° at top speed still travels about 80° off its nose a second later. A light fighter at its corner speed is never more than 3° off, and at top speed it slides only briefly.
2. **Corner speed.** The nose swings at the full class turn rate at or below `CornerSpeedFraction` of top speed (0.6). Above that, the rate tapers linearly down to `OverspeedTurnFactor` (0.6) of full at full boost speed. Boost gets you somewhere, and easing off tightens the turn, so throttle becomes a dogfighting decision. This is one reason the grip limit has the scale it does: past the corner speed the hull starts to slide wide.
3. **Hard strafing.** Assist's strafe set point is 0.5 of the forward one, against 0.35 for the shared model, so a light fighter can sidestep fire.
4. **Everything else stays shared.** That includes boost charge and drain, the cruise drive, the gravity near worlds, roll, and assist-off drift.

First-pass numbers (`LightFighterHandling.Default`): grip 1.0, strafe 0.5, corner 0.6, overspeed turn 0.6. They are tuned for feel, and the review ticket is where they get tuned.

Open for the review: whether the boost's turn penalty should apply only while boost is held (as a Star Conflict afterburner) instead of being based on speed alone, and whether AI dogfighters (`Battle`'s dogfight, U.SPC-T274) should fly through the same model so their behaviour matches what the player flies.

## Strike craft: interceptors, fighters and bombers

**Implemented** (U.SPC-T484, `ShipClass.Interceptor`, `ShipClass.Bomber`, tests in `Tests/Core.Tests/StrikeCraftTests.cs`).

The owner's roster ([docs/SHIP_ROSTER.md](SHIP_ROSTER.md)) has three strike craft, and each is now a `ShipClass` of its own with its own frame, mesh, crew, cost, carrier berth and flight model. Before this, a carrier's interceptors, heavy fighters and bombers were squadron roles (`FighterRole`) launched on a shared Courier hull, and the bomber flew the heavy fighter family.

- **Today's `Fighter` is the space superiority fighter.** It was already the dedicated dogfighter on the light fighter model, so it keeps its class, its design, its family and its numbers, and saves and tests stay as they were. Carriers launch it as their `FighterRole.SpaceSuperiority` squadrons.
- **The interceptor** (`FlightFamily.Interceptor`) flies the light fighter's rules pushed further, on `LightFighterHandling.Interceptor`: grip 1.25 (against 1.0), strafe 0.6 (against 0.5), corner speed 0.75 of top speed (against 0.6), and 0.75 of the turn left at full boost (against 0.6). It holds a tight turn to a higher speed, so it can run a fighter down and still turn with it. Its helm tuning is the fastest and the most agile of any strike craft (top speed 0.48 km/s, 180°/s), and its hull the lightest.
- **The bomber** (`FlightFamily.Bomber`, `BomberFlightModel` on `BomberHandling`) is a steady torpedo platform with its own rules when flight assist is on:
  1. **One turn rate at any speed.** It has no corner speed, so a run in is flown at one steady rate.
  2. **A wide carve.** Assist pushes across the nose only up to `Grip` (0.6) of the push that would hold the track to the nose through its tightest turn at top speed, so a hard turn swings wide.
  3. **A weak strafe.** The strafe set point is 0.25 of the forward one.
  4. **A throttle with heft.** Assist uses only `ThrottleAuthority` (0.6) of the drive and retros to change speed along the nose, so a bomber is slow to gather way and slow to shed it.
  5. **Assist off is Newtonian**, as for every family.

  It is steered nose-on with the mouse (`PilotControl.DirectNose`).

Carriers launch and recover them as squadrons (`FighterCraft.SquadronMix`): interceptors, space superiority fighters and bombers, then heavy fighters on a carrier with a fourth squadron. Each launched craft is a hull of its own class (`FighterCraft.Class`), drawn as that class and flown in a player's hands by that class's model (`FlightModelRegistry.For`). The heavy fighter flies on the gunship's hull (U.SPC-T467). The launched craft keep their own small dogfight specs (`FighterCraft.Spec`), while the class's `CombatSpec` is the hull a yard builds and a pilot flies at the helm.

Their pilots also pick their fights by class. An interceptor goes for enemy craft before hulls (`HuntsFightersKm`), and first for one closing on its own side's bombers (`ScreensBombers`). A bomber never picks a fighter, and goes first for a capital whose shield toward it is already down. A shield soaks a torpedo like any other hit, but a bomber's torpedo (`StrikeTorpedoes`) that arrives where the capital's shield is already down does `dogfight.torpedoBareHull` times its damage (3 by default, in Settings > Dogfighting). That is the roster's rule that bombers knock the shields down first and then torpedo.

## Heavy fighters: Everspace 2

**Proposed** (U.SPC-T441). Applies to the Gunship, the dedicated heavy-fighter hull.

Everspace 2's ships are always coupled and very responsive. The nose is the crosshair (control for this is already in from T269), strafing is a core move, and a heavy ship feels heavy because it carries real heft, not because it is sluggish. Proposed rules:

1. **Stiff, consistent turning.** There is no corner-speed penalty. The nose turns at the same rate at any speed, but that rate is capped lower than a light fighter's (about 70% of it), and it spins up hard: angular acceleration is high relative to the top rate, so the nose starts and stops crisply. That gives Everspace 2's "on rails" aim.
2. **Heft on the throttle.** Assist reaches its set point through a short ease (about 0.4 to 0.6 s to settle) instead of the shared model's thrust-limited push. Changes in speed read as weight without being slow.
3. **Equal-footing strafe.** Strafe and vertical set points are about 0.45 of forward, and ramp up as fast as forward thrust, for Everspace 2-style circle-strafing round a target.
4. **Boost overshoot.** When boost is released, assist doesn't brake hard. It bleeds the extra speed off over a couple of seconds, so a boosted run carries the hull through the target.
5. **No drift penalty.** Assist-off stays Newtonian, like every other family.

Tunables (`HeavyFighterHandling`): turn-rate cap fraction, settle time, strafe fraction, boost bleed time.

## Capital ships: Black Flag or Rebel Galaxy

**Implemented** (U.SPC-T499, `CapitalFlightModel` and `CapitalHandling` in `Core/Combat/FlightModels.cs`, tests in `Tests/Core.Tests/CapitalFlightTests.cs`; see [What landed](#what-landed)). Applies to the Carrier, Battleship, Cruiser and Destroyer, and the other capital classes (Command cruiser, Frigate). The helm controls already shipped in T269.

### What each reference does

**Assassin's Creed IV: Black Flag** (the Jackdaw):

- **Sail settings set the speed.** The throttle steps through stopped, half sail and full sail, and the setting holds without keeping a key down. Slowing down tightens the turn. Full sail is for closing distance and ramming, not for fighting.
- **Broadsides fire from the side the camera faces.** Holding fire shows the spread on the water and releasing it fires the volley. A side reloads in a few seconds, so each pass gets one good volley per side and maybe a second. Chase guns cover the bow, fire barrels go off the stern, and swivel guns hit highlighted weak points.
- **The enemy telegraphs its broadside, and the player braces** to take less damage.
- **A pass has a rhythm:** close on a converging course, turn to put the beam on the enemy at medium range, fire while it crosses the beam arc, then come about for the next pass. The fight is decided by the turn circle against the reload, and by keeping off the enemy's beam while getting yours onto it. Slowing to tighten the turn while the enemy is still at speed is how you win the position.

**Rebel Galaxy** (Double Damage):

- **The fight is held to a plane.** Capital ships sit on a 2D battle plane. Smaller craft fly in 3D around it, and turrets cover what is above and below.
- **Broadsides fire port and starboard by looking to that side**, with lead indicators. Turrets can run on their own or be aimed by hand.
- **Stepped throttle, slow turns, and a timed deflector** in place of the brace.
- **It reads clearly in space** because the plane turns 3D positioning into 2D positioning, which is the only kind a broadside game can make readable.

### Recommendation

**Use Black Flag's handling and pass rhythm, in full 3D.** Black Flag is the closer fit for how a capital ship should *fly*. Its stepped sails, its rule that a slower ship turns tighter, and its pass rhythm are what make positioning a skill, and that skill is what the owner asked to study. This study first recommended Rebel Galaxy's battle plane as well, to keep beam-on geometry readable; the owner decided against it (2026-10-23): **capital ships fight in full 3D, like the fighters**, so a capital climbs, dives and rolls to bring a broadside to bear above or below. Rebel Galaxy's deflector timing and Black Flag's brace are the same idea, and that belongs to T425.

Handling rules for `CapitalFlightModel`:

1. **Engine telegraph.** W and S step through a set of speeds instead of being held: Full Astern, Stop, Slow, Half, Full, and Flank, which is the boost and drains the boost charge. The setting holds after the key is let go. The HUD shows the current step, and the hull eases toward each step's speed at the class's own spool rate.
2. **Steerageway.** Turn rate depends on speed. It peaks around Half, which is the fighting speed, drops at Full and Flank, and falls to a slow thruster pivot at Stop (about 25% of peak). This is the Black Flag rule that slowing tightens the turn, plus the naval rule that a ship needs way on to steer.
3. **Keel.** With assist on, slip across the hull is damped strongly, so a turn traces a turning circle instead of a pivot-and-slide. The hull goes where it points, with a little visible slip at Flank.
4. **Full 3D, no battle plane.** Pitch and yaw are both slow and heavy but free in every direction, and roll is free too, so a capital can climb over a line, dive under it, or roll to put a broadside on a ship above or below. Nothing holds the hull to a plane or levels it by itself.
5. **Pass timing as data.** Each class gets a target come-about time, meaning a 180° turn at Half: Destroyer about 6 s, Cruiser about 10 s, Battleship about 14 s, Carrier about 18 s. These sit against T425's broadside reload, which should be about half a beam window, so a pass at the fighting speed gives one volley per side and a quick-handed captain gets two. The handling numbers are fitted to the come-about times, not the other way round, and a test checks them.
6. **The Destroyer is the skirmisher** (as in `Battle.BroadsideFacing`). It keeps the telegraph, but its turning circle is tighter and its Flank faster, so it can cut across a battleship's bow.

Tunables (`CapitalHandling`, with per-class overrides): telegraph speed fractions, spool time, steerage curve, keel damping, pitch, yaw and roll rates, and come-about target.

### What landed

`CapitalFlightModel` flies every capital class, each on its own `CapitalHandling` (`CapitalHandling.Of(class)`, registered per class with `FlightModelRegistry.Register(ShipClass, model)`; a capital class with no numbers of its own flies `CapitalHandling.Default`). With flight assist on:

1. **The engine telegraph** (`EngineTelegraph`, held on `PilotState.Telegraph`): W rings it a step ahead and S a step astern (`PilotInput.TelegraphStep`), through Full Astern (0.4 of top speed astern), Stop, Slow (0.25), Half (0.5), Full and Flank. The step holds with no key down. A hull already under way when it is taken in hand starts at the step nearest its way. The hull eases toward each step's speed with a time constant of `SpoolS` (6 s for a carrier down to 2.5 s for a destroyer), as fast as its drive and retros allow. Flank is the boost: it drains the boost charge, and once the charge is spent the telegraph rings itself down to Full. Shift still boosts, as a Flank held only while the key is. Full is the step the cruise drive spools from. The HUD reads the step out next to flight assist.
2. **Steerage** (`SteerageFactor`): the turn rate runs from the pivot at Stop (`PivotFactor`, 25% of the peak) up to the peak at Half, down to `FullFactor` (0.7) at Full and `FlankFactor` (0.6) at Flank speed and beyond, linearly with the hull's speed either way.
3. **The keel**: slip across the hull, and gravity across it, is killed with up to `Keel` (0.75) times top speed times the peak turn rate, far more than the manoeuvre thrusters, so a come-about at Half or a turn at Full leaves the track on the nose and traces a turning circle. Past Full (and on the destroyer, which keeps more of its turn at speed, from Full) the turn can outrun the keel and the hull slips a little.
4. **Full 3D**: pitch, yaw and roll are all free. `PitchFactor` (1) sets pitch against yaw, so a come-about over the top takes as long as one to starboard, and nothing levels the hull or holds it to a plane. Roll is held to the roll keys at `RollFactor` (1) times the peak turn rate and stays where it is left.
5. **The come-about is the fit, not a result.** The peak turn rate is solved from the class's `ComeAboutS` and its angular acceleration: a turn that spins up at α, swings at r and brakes takes π/r + r/α, so r is the smaller root of r² - αTr + απ = 0 (`PeakTurnRateDegS`). If a design's own attitude thrusters could not make the come-about at all, the helm uses the least angular acceleration that can, with a quarter to spare. Measured in the tests: Destroyer 6.0 s, Cruiser 10.0 s, Battleship 14.0 s, Carrier 18.0 s (the Command cruiser 12 and Frigate 5 are set the same way).
6. **The Destroyer is the skirmisher**: its Half is 0.35 of its top speed, so its turning circle is the tightest of the line (about 300 m across at Half against 370 to 410 m), it keeps more of its turn at Full (0.85) and Flank (0.7), its Flank is the fastest of the four, and its engines answer quickest.

At the helm the heading leads the nose by what this model's own swing needs (`AimLeadRad`), and the broadside HUD's turning circle and come-about time are this model's at the speed the hull is making (`TurnRateAt`, `ComeAboutSecondsAt`). With flight assist off the hull is the Newtonian one, as for every family; the telegraph keeps its step for when assist comes back on.

**Broadsides above and below** (checked for U.SPC-T425): a battery's arc is a wedge about the hull's own roof, from zenith to nadir, so a ship above and to starboard is already on the starboard beam. A hull flown at the helm now gives its battle unit its rolled roof (`BattleUnit.Up`, read only while it is player-flown), so rolling 90 degrees puts the starboard battery on a ship straight overhead and the port one on a ship beneath. Hulls flown by the battle's AI are still held level; rolling them to bring a broadside to bear, or to turn a weak patch away, is U.SPC-T502.

## Haulers: freighters and miners

**Proposed** (follow-up ticket). The controls already shipped in T269 (`Hauler`: nose steering with a slow mouse).

A hauler should be satisfying to fly because it is heavy: a run is planned, braking starts early, and a clean docking is an achievement. Proposed rules:

1. **Laden mass.** Cargo and propellant in the hold add to the hull's mass, so a full freighter accelerates, brakes and turns noticeably slower than an empty one. The mass comes from the grid hold and the tanks, which are already modelled.
2. **Weak retro, honestly shown.** The retro thrusters stay weak, and assist stops hiding it: the HUD shows the stop point (where the hull comes to rest at full retro) so the pilot can plan the braking.
3. **Docking mode.** Holding Ctrl (fine) inside a port's approach drops the set point to docking speed and lowers the turn rate for precise placement.
4. **No combat agility.** Strafe is weak (0.25), and boost is a short emergency burn, not a dodge.

Tunables (`HaulerHandling`): mass-per-tonne weighting, docking set point and turn fraction, strafe fraction.

## Couriers

**Implemented** (U.SPC-T444, `FlightFamily.Runner`, tests in `Tests/Core.Tests/FlightModelTests.cs`).

The Courier carries the mail, surveys and urgent parcels, and lands where warships hold orbit: it is a blockade runner, not a dogfighter, so it flies a **Runner** family of its own. It keeps the light fighter's free-aim controls (`PilotControl.FreeAim`) and flies the light fighter's rules on its own numbers (`LightFighterHandling.Runner`), the way the interceptor does:

1. **Low grip.** Assist pushes across the nose only 0.6 of the push that holds the track to it at the corner speed (against 1.0), so a hard break at speed swings wide on a big circle instead of carving.
2. **An early corner speed.** The nose swings at the full rate only up to 0.4 of top speed (against 0.6), and tapers to 0.45 of it at full boost (against 0.6). To turn, a runner has to slow right down.
3. **A modest strafe.** The strafe set point is 0.35 of the forward one (the shared model's, against the light fighter's 0.5). It gets away by running, not by dodging.

Its helm tuning (`PilotTuning`) makes it the fastest thing at the helm: the highest top speed in the game (0.52 km/s, against the interceptor's 0.48), the fastest boost (2.4 times) and the longest one (45 s, longer than any hauler's), and a cruise drive that spools up fastest (1.2 s). Its turn rate is lower than any fighter's (120°/s), so its turning circle at top speed is the widest of any small craft. A test checks each of these against every other class.

## Fired rounds: the Breacher

**Implemented** (U.SPC-T466, U.SPC-T521, U.SPC-T517, `BreacherBays` in `Core/Combat/BreacherBays.cs`, its flight in `Battle.StepBreacher`, tests in `Tests/Core.Tests/BreacherBayTests.cs`).

A Breacher is a whole marine team (70 by default) sealed in an armoured round and fired at high velocity from a Breacher Bay (the Assault Bay until U.SPC-T521). It is only ever a round, never a ship (U.SPC-T517): it has no ship class, design, yard entry, crew or flight family, and it is drawn in the scene as its own bullet-shaped round model, in flight and stuck in the hull it struck. In flight:

1. **It flies like a bullet.** It keeps the speed it was fired at (2.5 km/s past the firing ship's own by default). Nothing drives it on or slows it, and its nose stays along its track.
2. **Only a slight course correction.** Its thrusters bend the track toward the target by at most a small sideways push (0.02 km/s² by default). That is enough to follow a target that holds its course, but a hull that jinks hard after the round is fired is missed. A round that misses flies on and goes adrift with its team alive inside, for its side to recover after the fight (U.SPC-T519).
3. **It strikes the first hostile hull in its path.** A shield holding more than a quarter of its strength turns it aside. Otherwise the drill bores in, the round stays stuck in the plating, and its team storms out into the compartment it struck. The ordinary compartment-by-compartment boarding fight takes over from there, and the target's own marines muster from their barracks to stop them.

The Breacher Bay is a facility of two sections that any ship (navy, pirate or a merchant's escort) may fit, so long as it has the free internal space, the reactor power (30 MW) and the crew to man it (20) to spare, and a Marine Habitat to supply the teams. The yard's outfitting screen shows each facility's sections, power and crew, and the hull's reactor power and hands left. It fires again only as fast as the habitats ready the next team: two minutes for one habitat by default, faster with more. The gate breaker is designed with one, navies and raiders refit one into a hull with the room and a team's worth of marines, every raider band's leader carries one, and a raider destroyer fits a second habitat to supply it. Its captain, of whatever side, fires at the most weakened hostile hull in range whose shields are down (a lawful ship only at raiders) to take it as a prize. The twelve-marine boarding pods remain the lighter option. Every figure is in Settings > Broadsides.

## Work plan

Each family is its own ticket, linked part-of U.SPC-T17, approved and worked one at a time:

1. U.SPC-T440: the light-fighter review and tuning pass with the owner, covering the open questions above.
2. U.SPC-T441: the heavy-fighter model (Everspace 2).
3. U.SPC-T499: the capital model (Black Flag handling in full 3D; it replaces U.SPC-T442, which assumed a battle plane). Landed.
4. U.SPC-T443: the hauler model.
5. U.SPC-T444: the Courier, as a Runner family of its own. Landed.

Every one of these adds a model and a handling struct, registers it, and keeps the logic in Core with tests. None of them edits `PilotFlight` or another family's model.
