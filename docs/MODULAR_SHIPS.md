# Modular ships

U.SPC-T596. The long-term plan is for every hull to be built from modules that can break apart in
combat, drift off as wreckage, and be scavenged, salvaged and sold. This ticket adds the Core
module/joint model that damage, wrecks, salvage and trade build on
(`Assets/UnitySpace/Core/Combat/ModularHull.cs`). It also adds the first two uses: a destroyed
hull now breaks into drifting pieces, and each piece knows what it breaks down into.

## Two accounts, not one

`ShipCombat.Hull` still decides whether a ship lives. `ShipCombat.Structure` (a `ModularHull`)
is a second account beside it and tracks where the hull is coming apart. Keeping them separate
means no existing balance changes. Every number that decides a fight today (hull points,
hardpoint health, shields, armour) works as before, and the structure gets its say only where a
caller asks for it.

## The model

- **`HullModule`** is one structural piece of the hull. It is either a keel section (`Bow`,
  `Midsection`, `Engineering`) or a `Sponson`, an outrigger off one side of a section. A module
  has a box in the hull frame (ship lengths: right, up, forward, the same frame as
  `SubsystemDef`'s X/Y/Z), a share of the dry mass, structure points, and the hardpoints
  mounted on it.
- **`HullJoint`** joins two modules: a ring of frames between keel sections, or a sponson's
  root. Each joint has its own strength. A broken joint no longer holds its two modules together.
- **`Root`** is the keel section holding the bridge (else the reactor, else the middle
  section). Whatever is still joined to it is the ship. Anything else has broken away.
- **`HullFragment`** is a piece that has come away: one or more modules still joined to each
  other, and every hardpoint on them. Everything downstream works on fragments.

### Laid out from the design

`ModularHull.For(CombatSpec)` lays out every design's modules automatically, so no design needs
a hand-made breakdown:

- The keel is cut into `2 + round(length / 0.4 km)` sections, from 2 up to a maximum of 8. That
  gives a fighter 2 sections, a destroyer 3, a cruiser 5, and a battleship or carrier 8.
- A mount set out past 60% of the half-beam rides on a sponson off its section, on its side. A
  buried bridge, a spinal gun, the reactor, a hangar or a drill prow always stays on the keel.
- Each sponson takes 5% of the mass, and the keel sections share the rest evenly. A module's
  structure is 1.5 × its mass share × the hull's points. A keel joint holds 20% of the hull's
  points and a sponson root 6%.

### Damage

`Strike(localPoint, damage)` takes a hit that has already got through the shields and armour.
The module the hit lands on takes all of it into its structure. The joints round that module
share half of it, and the nearest joint takes the most. A module whose structure is gone
shatters and lets go of every joint. `Separate()` reports each piece no longer held to the root,
once only, and `ModularHulls.ApplyBreak` makes the hull pay for it: every hardpoint on the piece
is lost, and the hull loses half the piece's mass share of its points (never down to zero).

The tuning means a hull is far more often holed than cut apart. A sponson shatters and comes away
once about 7% of the hull's points has landed on it. Cutting the keel takes concentrated fire
around one joint: about 40% of the hull's points on the sections either side of it.

### Wrecks

`Wrecks.Add` now calls `ModularHull.BreakUp(wreck id)` on a ship with a combat state. Each joint
fails with a chance that grows with how strained it already was: 25% for a sound joint, close to
certain for one nearly gone. At least one joint always fails, and the same wreck always breaks
the same way. `Wreck.Pieces` holds the main hulk first and then every piece blown off it. A piece
starts where it sat on the hull and drifts out along its own heading as the debris field spreads
(`WreckPiece.OffsetKm`), so it stays inside the field the wreck already draws and collides with.

### Salvage and trade

`HullSalvage.Scrap` works out what a piece breaks down into:

- The structure gives alloys: 30% of its dry mass, scaled by what is left of it, or half that if
  the module shattered.
- A part a yard fits gives back half its own bill of materials (`ModuleSpec.Bill`), scaled by its
  health.
- A part no yard fits (a bridge, a reactor, a hangar) gives the one good it is mostly made of
  (`SalvageSession.ScrapOf`).

`HullSalvage.Value` prices a pile of scrap at a port's prices, or at base prices with no port.
`HullSalvage.PartValue` prices a sound part sold whole at 60% of its bill, as a secondhand part.

## What builds on it next

Each of these is a separate ticket. None of them needs the model changed.

1. **Battle damage.** Call `Structure.Strike` from `Battle.ApplyDamage` and
   `Collisions.ApplyDamage` once the shields and armour have taken their share. Apply each
   fragment with `ModularHulls.ApplyBreak`, raise an event, and add the fragment to the battle as
   a drifting body (a collision volume), so a ship visibly loses a sponson mid-fight. Put this
   behind a setting until it has been tuned in play.
2. **Wreckage views.** The Runtime draws `Wreck.Pieces` as the hull's own sections (cut from the
   ship's mesh by module box) instead of generic debris.
3. **Scavenging.** Let `SalvageSession` latch on to a single `WreckPiece`. The player then cuts
   its scrap into the grid hold, or strips a sound part from it to sell whole.
4. **Trade.** Breakers' yards and the black market buy scrap and secondhand parts
   (`HullSalvage.Value`, `PartValue`). Wrecks and their scrap become something raiders, salvage
   companies and the player compete for.
5. **Repair.** A yard repair (`ShipCombat.Repair(1)`) rebuilds the structure whole, pieces lost
   included. A field repair mends only what is still attached. A later ticket can make a yard
   charge for a lost module by its mass and bill.
