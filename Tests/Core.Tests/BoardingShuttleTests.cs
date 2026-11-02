using System;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Boarding shuttles (U.SPC-T486): a piloted, armoured, unarmed craft a carrier keeps in its hangars that carries a
    /// marine team to a disabled ship or a silenced station, docks alongside and latches on, and lets the team in through
    /// the airlock to take it intact: slower and gentler than a fired Breacher round, no drill and no breach. A carrier sends
    /// its shuttles rather than closing to board with its own hull, and a war fleet with shuttles takes a silenced gate sooner.
    /// </summary>
    [TestFixture]
    public class BoardingShuttleTests
    {
        const double StartJd = 2461324.0;

        static readonly Faction Blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true, MarineQuality = 1.2 };
        static readonly Faction Red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true, MarineQuality = 1.0 };

        static Ship Hull(ShipClass c, Faction f, int id) =>
            new Ship { Id = id, Name = c + "" + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c) };

        // No squadrons launched: the carrier's shuttles are the only craft it puts out.
        static Battle NewBattle(int seed) => new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, seed) { FighterStep = FighterAI.Step };

        /// <summary>A hull lying dead in space: no engines, no shields, no guns.</summary>
        static void Cripple(BattleUnit u)
        {
            foreach (var s in u.Combat.Subsystems)
                if (s.Kind == SubsystemKind.ShieldGenerator || s.Kind == SubsystemKind.Engine || s.Def.IsWeapon) s.Hp = 0;
            u.Combat.ZeroAllShields();
            u.MoraleThreshold = double.MaxValue;
        }

        static BattleUnit Shuttle(Battle b) => b.Units.FirstOrDefault(u => BoardingShuttles.Is(u));

        /// <summary>A carrier with its boarding pods shot away, so its shuttles are the only boarders it sends.</summary>
        static Ship Carrier(int id)
        {
            var s = Hull(ShipClass.Carrier, Blue, id);
            foreach (var sub in s.Combat.Subsystems)
                if (sub.Kind == SubsystemKind.BoardingPods) sub.Hp = 0;
            return s;
        }

        // ---------------------------------------------------------------- the craft

        [Test]
        public void TheAssaultShuttleIsACarriersHangarCraftWithATroopBayForOneTeam()
        {
            Assert.That(ShipClassSpec.Of(ShipClass.AssaultShuttle).DisplayName, Is.EqualTo("Assault shuttle"));
            Assert.That(FighterCraft.OfClass(ShipClass.AssaultShuttle), Is.SameAs(FighterCraft.AssaultShuttle));
            Assert.That(FighterCraft.AssaultShuttle.Role, Is.EqualTo(FighterRole.AssaultShuttle));
            Assert.That(FighterCraft.SquadronMix, Does.Not.Contain(FighterRole.AssaultShuttle), "not one of the squadrons");
            Assert.That(FtlRoster.Of(ShipClass.AssaultShuttle), Is.EqualTo(FtlAccess.Hangar));
            Assert.That(HullFrames.CategoryOf(ShipClass.AssaultShuttle), Is.EqualTo(HullCategory.SmallCraft));

            // Its team rides in a troop bay, not a habitat.
            var design = CombatSpec.Of(ShipClass.AssaultShuttle);
            Assert.That(design.Marines, Is.EqualTo(24));
            Assert.That(FacilityLayouts.For(ShipClass.AssaultShuttle).Any(s => s.Kind == FacilityKinds.TroopCompartment), Is.True);
            Assert.That(FacilityLayouts.For(ShipClass.AssaultShuttle).Any(s => s.Kind == FacilityKinds.MarineHabitat), Is.False);

            // Unarmed, and slower than a bomber: it is escorted in, not fought with.
            Assert.That(new ShipCombat(design, ShipClass.AssaultShuttle).Armed, Is.False);
            Assert.That(new ShipCombat(FighterCraft.AssaultShuttle.Spec, ShipClass.AssaultShuttle).Armed, Is.False);
            Assert.That(FighterCraft.AssaultShuttle.Spec.MaxSpeed, Is.LessThan(FighterCraft.Bomber.Spec.MaxSpeed));
            Assert.That(PilotTuning.Of(ShipClass.AssaultShuttle).MaxSpeed, Is.LessThan(PilotTuning.Of(ShipClass.Bomber).MaxSpeed));

            // A carrier keeps two beside its squadrons; nothing else carries any.
            Assert.That(CombatSpec.Of(ShipClass.Carrier).Shuttles, Is.EqualTo(2));
            Assert.That(new ShipCombat(CombatSpec.Of(ShipClass.Carrier), ShipClass.Carrier).Shuttles, Is.EqualTo(2));
            Assert.That(new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser).Shuttles, Is.EqualTo(0));
            var carrier = new ShipCombat(CombatSpec.Of(ShipClass.Carrier), ShipClass.Carrier) { Shuttles = 0 };
            carrier.RestoreShuttles(5);
            Assert.That(carrier.Shuttles, Is.EqualTo(2), "the hangars hold no more than their berths");
        }

        [Test]
        public void ADockPointIsOnTheFlankFacingTheBoarderAndStaysThereSoItNeverCrossesTheHull()
        {
            var b = NewBattle(7);
            var prize = b.AddShip(Hull(ShipClass.Cruiser, Red, 1), 1, new DVec3(0, 0, 0), new DVec3(0, 0, 1));
            var boarder = b.AddShip(Hull(ShipClass.Cruiser, Blue, 2), 0, new DVec3(0, 0, 0), new DVec3(0, 0, 1));
            prize.Basis(out var right, out _);
            boarder.Pos = prize.Pos - right * 20;
            var port = boarder.DockPoint(prize, 0.06);
            Assert.That(DVec3.Dot(port - prize.Pos, right), Is.LessThan(0), "port side, facing the boarder");
            boarder.Pos = prize.Pos + right * 0.01;
            Assert.That(boarder.DockPoint(prize, 0.06).X, Is.EqualTo(port.X).Within(1e-9), "kept once chosen");
            Assert.That(boarder.DockPoint(prize, 0.06).Y, Is.EqualTo(port.Y).Within(1e-9));
            Assert.That(boarder.DockPoint(prize, 0.06).Z, Is.EqualTo(port.Z).Within(1e-9));
        }

        // ---------------------------------------------------------------- in battle

        [Test]
        public void ACarrierSendsAShuttleThatDocksLatchesOnAndItsTeamTakesThePrizeIntact()
        {
            var b = NewBattle(3);
            var carrier = b.AddShip(Carrier(0), 0, new DVec3(10, 0, 0), -DVec3.UnitX);
            var prize = b.AddShip(Hull(ShipClass.Freighter, Red, 1), 1, new DVec3(-5, 0, 0), DVec3.UnitX);
            Cripple(prize);
            double marines = carrier.Combat.Interior.Marines;
            double team = BoardingShuttles.TeamSize.Value;

            BattleUnit shuttle = null;
            for (int i = 0; i < 20 && shuttle == null; i++)
            {
                b.Advance(1);
                shuttle = Shuttle(b);
            }
            Assert.That(shuttle, Is.Not.Null, "the carrier sent no shuttle");
            Assert.That(shuttle.Kind, Is.EqualTo(UnitKind.Fighter));
            Assert.That(shuttle.Fighter.Carrier, Is.SameAs(carrier));
            Assert.That(shuttle.BoardTarget, Is.SameAs(prize));
            Assert.That(shuttle.Combat.Interior.Marines, Is.EqualTo(team).Within(1e-6), "a whole team rides across");
            Assert.That(carrier.Combat.Interior.Marines, Is.EqualTo(marines - team).Within(1e-6), "drawn from the carrier's barracks");
            Assert.That(carrier.Combat.Shuttles, Is.EqualTo(1));

            double hull = prize.Combat.Hull;
            for (int i = 0; i < 1200 && shuttle.Latched == null; i++)
            {
                b.Advance(1);
                Assert.That(carrier.BoardTarget, Is.Null, "a carrier with shuttles does not close to board with its own hull");
            }
            Assert.That(shuttle.Latched, Is.SameAs(prize), "the shuttle never docked");
            Assert.That(b.Fx.Any(f => f.Kind == FxKind.Latch && f.Unit == prize), Is.True);
            Assert.That(prize.Combat.Interior.Boarder, Is.SameAs(Blue), "the team did not go in");
            Assert.That(shuttle.Combat.Interior.Marines, Is.EqualTo(0).Within(1e-6));
            // Gentle: no drill, no breach, nothing holed.
            Assert.That(prize.Combat.Hull, Is.EqualTo(hull), "docking damaged the hull");
            Assert.That(prize.Combat.Interior.Compartments.Any(c => c.Vented), Is.False, "docking holed the hull");
            Assert.That(b.Embedded.Any(e => e.Unit == prize), Is.False);

            for (int i = 0; i < 3000 && !prize.Captured; i++) b.Advance(1);
            foreach (var note in prize.Combat.Interior.Log) TestContext.Out.WriteLine(note.Text);
            Assert.That(prize.Captured, Is.True, "the team did not take the freighter");
            Assert.That(prize.Side, Is.EqualTo(0));

            // Cast off and home: every shuttle it sent lands back in its berths.
            for (int i = 0; i < 3000 && b.Units.Any(u => BoardingShuttles.Is(u) && u.Present); i++) b.Advance(1);
            Assert.That(b.Units.Where(BoardingShuttles.Is).All(u => u.Docked), Is.True, "a shuttle never landed");
            Assert.That(carrier.Combat.Shuttles, Is.EqualTo(2));
        }

        [Test]
        public void NoShuttleGoesForAHullStillFightingNorForOneOutOfReach()
        {
            var b = NewBattle(4);
            var carrier = b.AddShip(Carrier(0), 0, new DVec3(20, 0, 0), -DVec3.UnitX);
            b.AddShip(Hull(ShipClass.Destroyer, Red, 1), 1, new DVec3(-20, 0, 0), DVec3.UnitX);
            for (int i = 0; i < 30; i++) b.Advance(1);
            Assert.That(Shuttle(b), Is.Null, "a shuttle went for a destroyer still under way and armed");
            Assert.That(carrier.Combat.Shuttles, Is.EqualTo(2));

            var far = NewBattle(5);
            var c2 = far.AddShip(Carrier(0), 0, new DVec3(BoardingShuttles.RangeKm.Value, 0, 0), -DVec3.UnitX);
            var wreck = far.AddShip(Hull(ShipClass.Freighter, Red, 1), 1, new DVec3(-30, 0, 0), DVec3.UnitX);
            Cripple(wreck);
            far.Advance(5);
            Assert.That(Shuttle(far), Is.Null, "a prize beyond the shuttles' range");
            Assert.That(c2.Combat.Shuttles, Is.EqualTo(2));
        }

        [Test]
        public void AShuttleBoardsASilencedStationAndTakesIt()
        {
            var b = NewBattle(6);
            var carrier = b.AddShip(Carrier(0), 0, new DVec3(8, 0, 0), -DVec3.UnitX);
            // A gate fort's guns silenced, its garrison and crew thinned in the fighting.
            var fort = b.NewUnit(UnitKind.Platform, 1, Red, new ShipCombat(CombatSpec.Platform), CombatSpec.PlatformLengthKm, new DVec3(-6, 0, 0), DVec3.UnitX);
            foreach (var s in fort.Combat.Subsystems)
                if (s.Def.IsWeapon || s.Kind == SubsystemKind.ShieldGenerator) s.Hp = 0;
            fort.Combat.ZeroAllShields();
            fort.Combat.Hull = fort.Combat.Spec.Hull * 0.1;
            fort.Combat.Interior.TakeMarines(fort.Combat.Interior.Marines - 5);
            fort.Combat.Interior.TakeCrew(fort.Combat.Interior.Crew - 20);
            Assert.That(fort.Combat.Disabled, Is.True);

            for (int i = 0; i < 4000 && !fort.Captured; i++) b.Advance(1);
            foreach (var note in fort.Combat.Interior.Log) TestContext.Out.WriteLine(note.Text);
            Assert.That(b.Fx.Any(f => f.Kind == FxKind.Latch && f.Unit == fort), Is.True, "no shuttle docked with the fort");
            Assert.That(fort.Captured, Is.True, "the fort was not taken");
            Assert.That(fort.Dead, Is.False, "taken, not wrecked");
        }

        [Test]
        public void AShuttleShotDownIsLostWithItsTeam()
        {
            var b = NewBattle(7);
            var carrier = b.AddShip(Carrier(0), 0, new DVec3(10, 0, 0), -DVec3.UnitX);
            var prize = b.AddShip(Hull(ShipClass.Freighter, Red, 1), 1, new DVec3(-5, 0, 0), DVec3.UnitX);
            Cripple(prize);
            BattleUnit shuttle = null;
            for (int i = 0; i < 20 && shuttle == null; i++)
            {
                b.Advance(1);
                shuttle = Shuttle(b);
            }
            Assert.That(shuttle, Is.Not.Null);
            // Shot down on the way across, the carrier's last shuttle.
            shuttle.Dead = true;
            carrier.Combat.Shuttles = 0;
            double marines = carrier.Combat.Interior.Marines;
            for (int i = 0; i < 30; i++) b.Advance(1);
            Assert.That(prize.Combat.Interior.BoardersLanded, Is.EqualTo(0), "the lost team still boarded");
            Assert.That(carrier.Combat.Interior.Marines, Is.LessThanOrEqualTo(marines + 1e-6), "the lost team came home");
        }

        // ---------------------------------------------------------------- real hulls (U.SPC-T590)

        [Test]
        public void EveryShuttleIsARealHullDockedInItsBerthBesideTheSquadrons()
        {
            var c = new ShipCombat(CombatSpec.Of(ShipClass.Carrier), ShipClass.Carrier);
            Assert.That(c.ShuttleBay.Count, Is.EqualTo(2));
            Assert.That(c.ShuttleBay.Select(s => s.Craft.Berth), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(c.ShuttleBay.All(s => s.Spec.Class == ShipClass.AssaultShuttle && s.Craft.Role == FighterRole.AssaultShuttle), Is.True);
            Assert.That(c.Hangar.Any(CarrierWings.IsShuttle), Is.False, "the shuttles keep berths of their own, not the squadrons'");

            // A shuttle that comes home hurt keeps its damage until a yard mends it; only an empty berth is built for.
            var first = c.ShuttleBay[0];
            Assert.That(CarrierWings.Undock(c, first), Is.True);
            first.Combat.Hull *= 0.5;
            Assert.That(CarrierWings.Dock(c, first), Is.True);
            Assert.That(c.ShuttleBay[0], Is.SameAs(first), "back in its own berth");
            Assert.That(CarrierWings.Damaged(c), Is.GreaterThanOrEqualTo(1));
            c.RestoreShuttles(5);
            Assert.That(c.ShuttleBay[0], Is.SameAs(first), "a yard builds only for empty berths");
            CarrierWings.MendAboard(c);
            Assert.That(first.Combat.Damage, Is.LessThanOrEqualTo(0.001));

            // A refit keeps the same shuttles aboard.
            var refit = c.Refitted(CombatSpec.Of(ShipClass.Carrier), ShipClass.Carrier, -1);
            Assert.That(refit.ShuttleBay[0], Is.SameAs(first));
        }

        [Test]
        public void AShuttleFliesAsItselfAndLandsBackAsItselfWithItsPilot()
        {
            var b = NewBattle(3);
            var carrier = b.AddShip(Carrier(0), 0, new DVec3(10, 0, 0), -DVec3.UnitX);
            var prize = b.AddShip(Hull(ShipClass.Freighter, Red, 1), 1, new DVec3(-5, 0, 0), DVec3.UnitX);
            Cripple(prize);
            var docked = carrier.Combat.ShuttleBay.ToList();

            BattleUnit shuttle = null;
            for (int i = 0; i < 20 && shuttle == null; i++)
            {
                b.Advance(1);
                shuttle = Shuttle(b);
            }
            Assert.That(shuttle, Is.Not.Null, "the carrier sent no shuttle");
            Assert.That(shuttle.CraftHull, Is.SameAs(docked[0]), "it flies as the shuttle that was docked");
            Assert.That(shuttle.Combat, Is.SameAs(docked[0].Combat), "on its own hull");
            Assert.That(carrier.Combat.ShuttleBay, Does.Not.Contain(docked[0]));
            var pilot = docked[0].Craft.Pilot;
            Assert.That(pilot, Is.Not.Null);
            Assert.That(shuttle.Fighter.Skill, Is.EqualTo(pilot.Skill));

            for (int i = 0; i < 6000 && b.Units.Any(u => BoardingShuttles.Is(u) && u.Present); i++) b.Advance(1);
            Assert.That(shuttle.Docked, Is.True, "the shuttle never landed");
            Assert.That(carrier.Combat.ShuttleBay, Does.Contain(docked[0]), "docked again as itself");
            Assert.That(docked[0].Craft.Pilot, Is.SameAs(pilot), "with the same pilot");
        }

        // ---------------------------------------------------------------- gates

        [Test]
        public void AWarFleetWithShuttlesSeizesASilencedGateSooner()
        {
            var carrier = Hull(ShipClass.Carrier, Blue, 0);
            var cruiser = Hull(ShipClass.Cruiser, Blue, 1);
            Assert.That(BoardingShuttles.CanSeizeGate(new[] { carrier, cruiser }), Is.True);
            Assert.That(BoardingShuttles.CanSeizeGate(new[] { cruiser }), Is.False, "no shuttles in the group");

            carrier.Combat.Shuttles = 0;
            Assert.That(BoardingShuttles.CanSeizeGate(new[] { carrier }), Is.False, "its shuttles all lost");
            carrier.Combat.Shuttles = 2;
            carrier.Combat.Interior.TakeMarines(carrier.Combat.Interior.Marines - 10);
            Assert.That(BoardingShuttles.CanSeizeGate(new[] { carrier }), Is.False, "no marines for a team");

            Assert.That(BoardingShuttles.GateHoldDays(InterstellarWars.GateHoldDays, true), Is.LessThan(InterstellarWars.GateHoldDays));
            Assert.That(BoardingShuttles.GateHoldDays(InterstellarWars.GateHoldDays, false), Is.EqualTo(InterstellarWars.GateHoldDays));
        }
    }
}
