using System;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Who has their own FTL (U.SPC-T419, U.SPC-T472; docs/SHIP_ROSTER.md): carriers always carry a drive, battleships only
    /// while their design fits the FTL drive facility, the deep-space explorer its survey drive; cruisers, destroyers and
    /// the lane interdictor have none and cross by the gates. Carriers and battleships with a drive take fighters and small
    /// craft with them docked aboard, so a fleet can strike or explore off the gate network with its wings; what has no
    /// drive and does not fit a berth stays behind.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class FtlCarriageTests
    {
        const double StartJd = 2461324.0;

        static Faction Navy() => new Faction("Test Navy", "TNS", default) { IsMilitary = true };

        static Ship Hull(string name, ShipClass cls, Faction faction) => new Ship
        {
            Name = name, Spec = ShipClassSpec.Of(cls), Faction = faction, Role = ShipRole.Free, Combat = new ShipCombat(CombatSpec.Of(cls), cls),
        };

        [Test]
        public void TheOwnersRosterSaysWhoHasTheirOwnFtl()
        {
            // Carriers always; battleships when fitted; cruisers and destroyers by the gates; fighters and bombers in a hangar.
            Assert.That(FtlRoster.Of(ShipClass.Carrier), Is.EqualTo(FtlAccess.Own));
            Assert.That(FtlRoster.Of(ShipClass.Explorer), Is.EqualTo(FtlAccess.Own), "a long-legged survey ship keeps its drive");
            Assert.That(FtlRoster.Of(ShipClass.Battleship), Is.EqualTo(FtlAccess.Fitted));
            foreach (var c in new[] { ShipClass.Cruiser, ShipClass.Destroyer, ShipClass.Courier, ShipClass.Freighter })
                Assert.That(FtlRoster.Of(c), Is.EqualTo(FtlAccess.Gates), c.ToString());
            foreach (var c in new[] { ShipClass.Fighter, ShipClass.Interceptor, ShipClass.Bomber })
                Assert.That(FtlRoster.Of(c), Is.EqualTo(FtlAccess.Hangar), c.ToString());
            // A corvette never leaves its home system (U.SPC-T476); every other hull does, one way or another.
            Assert.That(FtlRoster.Of(ShipClass.Corvette), Is.EqualTo(FtlAccess.HomeSystem));
            foreach (ShipClass c in Enum.GetValues(typeof(ShipClass)))
                Assert.That(FtlRoster.LeavesHome(c), Is.EqualTo(c != ShipClass.Corvette), c + ": only a corvette never leaves its home system");

            var navy = Navy();
            Assert.That(CruiseDrives.Of(Hull("x", ShipClass.Carrier, navy)), Is.SameAs(CruiseDrives.Capital));
            Assert.That(CruiseDrives.Of(Hull("x", ShipClass.Battleship, navy)), Is.SameAs(CruiseDrives.Capital), "the stock battleship is designed with one");
            Assert.That(CruiseDrives.Of(Hull("x", ShipClass.Explorer, navy)), Is.SameAs(CruiseDrives.Survey));
            foreach (var c in new[] { ShipClass.Cruiser, ShipClass.Destroyer, ShipClass.Fighter, ShipClass.Bomber })
            {
                Assert.That(CruiseDrives.Of(Hull("x", c, navy)), Is.Null, c.ToString());
                Assert.That(InterstellarTraffic.CanCruise(Hull("x", c, navy)), Is.False, c.ToString());
            }
        }

        [Test]
        public void ABattleshipCarriesItsOwnFtlOnlyWhileItsDesignFitsTheDriveFacility()
        {
            var drive = FacilityKinds.Of(FacilityKinds.FtlDrive);
            Assert.That(drive, Is.Not.Null);
            Assert.That(drive.Size, Is.GreaterThan(0), "it takes hull space");
            Assert.That(drive.Fittable, Is.True);
            Assert.That(HullFrames.ForClass(ShipClass.Battleship).Facilities.Count(f => f.Kind == FacilityKinds.FtlDrive), Is.EqualTo(1));
            Assert.That(HullFrames.ForClass(ShipClass.Cruiser).CanCarry(drive), Is.False, "a cruiser cannot fit one");
            Assert.That(HullFrames.ForClass(ShipClass.Destroyer).CanCarry(drive), Is.False);
            Assert.That(HullFrames.ForClass(ShipClass.Carrier).CanCarry(drive), Is.False, "a carrier has its own already");

            // A command cruiser, a warship like the cruiser, is under the same rule as the battleship (U.SPC-T477).
            Assert.That(FtlRoster.Of(ShipClass.CommandCruiser), Is.EqualTo(FtlAccess.Fitted));
            Assert.That(HullFrames.ForClass(ShipClass.CommandCruiser).CanCarry(drive), Is.True);
            Assert.That(HullFrames.ForClass(ShipClass.CommandCruiser).Facilities.Count(f => f.Kind == FacilityKinds.FtlDrive), Is.EqualTo(1));
            var flag = new ConstructionPlan(HullFrames.ForClass(ShipClass.CommandCruiser));
            flag.RemoveFacility(flag.Facilities.ToList().FindIndex(f => f.Kind == FacilityKinds.FtlDrive));
            var stripped = Hull("TNS Herald", ShipClass.CommandCruiser, Navy());
            Assert.That(flag.Apply(stripped), Is.True);
            Assert.That(CruiseDrives.Of(stripped), Is.Null);
            Assert.That(CruiseDrives.Of(Hull("TNS Signal", ShipClass.CommandCruiser, Navy())), Is.SameAs(CruiseDrives.Capital));

            // Stripped out at the yard: its space is free again, and the battleship goes by the gates.
            var plan = new ConstructionPlan(HullFrames.ForClass(ShipClass.Battleship));
            double free = plan.FreeSpace;
            int at = plan.Facilities.ToList().FindIndex(f => f.Kind == FacilityKinds.FtlDrive);
            Assert.That(plan.RemoveFacility(at), Is.EqualTo(ConstructionOutcome.Ok));
            Assert.That(plan.FreeSpace, Is.EqualTo(free + drive.Size).Within(1e-9));
            var navy = Navy();
            var ship = Hull("TNS Bulwark", ShipClass.Battleship, navy);
            Assert.That(plan.Apply(ship), Is.True);
            Assert.That(FtlRoster.DriveFitted(ship), Is.False);
            Assert.That(CruiseDrives.Of(ship), Is.Null);
            Assert.That(FtlCarriage.BerthsOf(ship), Is.EqualTo(0), "no drive, no craft carried through FTL");
            Assert.That(HelmFtl.Refusal(ship), Does.Contain("fitted"));

            // Fitted back in, it has its drive again.
            var refit = new ConstructionPlan(HullFrames.ForClass(ShipClass.Battleship));
            refit.RemoveFacility(at);
            Assert.That(refit.FitFacility(drive), Is.EqualTo(ConstructionOutcome.Ok));
            var again = Hull("TNS Resolute", ShipClass.Battleship, navy);
            Assert.That(refit.Apply(again), Is.True);
            Assert.That(CruiseDrives.Of(again), Is.SameAs(CruiseDrives.Capital));
            Assert.That(FtlCarriage.BerthsOf(again), Is.EqualTo(FtlCarriage.BerthsOf(ShipClass.Battleship)));
        }

        [Test]
        public void OnlyCarriersAndBattleshipsWithADriveHaveBerthsAndCraftWithoutOneCanBeCarried()
        {
            Assert.That(FtlCarriage.BerthsOf(ShipClass.Carrier), Is.GreaterThan(FtlCarriage.BerthsOf(ShipClass.Battleship)));
            Assert.That(FtlCarriage.BerthsOf(ShipClass.Battleship), Is.GreaterThan(0));
            foreach (var c in new[] { ShipClass.Cruiser, ShipClass.Destroyer, ShipClass.Explorer })
                Assert.That(FtlCarriage.BerthsOf(c), Is.EqualTo(0), c + " has no berths");
            // Fighters and small craft can be carried; haulers cannot; anything with a drive of its own needs no carrying.
            foreach (var c in new[] { ShipClass.Fighter, ShipClass.Courier, ShipClass.Gunship })
                Assert.That(FtlCarriage.Carriable(Hull("x", c, Navy())), Is.True, c.ToString());
            foreach (var c in new[] { ShipClass.Freighter, ShipClass.Miner, ShipClass.Carrier, ShipClass.Explorer })
                Assert.That(FtlCarriage.Carriable(Hull("x", c, Navy())), Is.False, c.ToString());
            // Every class has its stats, so the new hull is built, flown and priced like any other.
            Assert.That(ShipClassSpec.Of(ShipClass.Explorer).DisplayName, Is.EqualTo("Explorer"));
            Assert.That(CombatSpec.Of(ShipClass.Explorer).DesignName, Is.EqualTo("Explorer"));
        }

        [Test]
        public void ANewClassSlotsIntoTheRosterAsData()
        {
            var navy = Navy();
            var hull = Hull("TNS Picket", ShipClass.Miner, navy);
            try
            {
                // As a corvette will: it never leaves its home system.
                FtlRoster.Register(ShipClass.Miner, FtlAccess.HomeSystem);
                Assert.That(FtlRoster.LeavesHome(hull), Is.False);
                Assert.That(HelmFtl.Refusal(hull), Does.Contain("home system"));
                // As a gate breaker will: a drive of its own, always.
                FtlRoster.Register(ShipClass.OreTug, FtlAccess.Own);
                CruiseDrives.Register(ShipClass.OreTug, CruiseDrives.Capital);
                Assert.That(CruiseDrives.Of(Hull("Breaker", ShipClass.OreTug, navy)), Is.SameAs(CruiseDrives.Capital));
            }
            finally
            {
                FtlRoster.Register(ShipClass.Miner, FtlAccess.Gates);
                FtlRoster.Register(ShipClass.OreTug, FtlAccess.Gates);
                CruiseDrives.Register(ShipClass.OreTug, null);
            }
            Assert.That(FtlRoster.LeavesHome(hull), Is.True);
            Assert.That(CruiseDrives.Of(Hull("Breaker", ShipClass.OreTug, navy)), Is.Null);
        }

        [Test]
        public void CraftAreStowedWhereThereIsRoomAndTheRestStayBehind()
        {
            var navy = Navy();
            var battleship = Hull("TNS Ardent", ShipClass.Battleship, navy);
            var destroyer = Hull("TNS Swift", ShipClass.Destroyer, navy);
            var explorer = Hull("Far Horizon", ShipClass.Explorer, navy);
            var fighters = Enumerable.Range(0, 6).Select(i => Hull("TNS Gnat " + i, ShipClass.Fighter, navy)).ToList();
            var gunship = Hull("TNS Ram", ShipClass.Gunship, navy);
            var freighter = Hull("Bulk", ShipClass.Freighter, navy);
            int room = FtlCarriage.BerthsOf(ShipClass.Battleship);

            var aboard = FtlCarriage.Stow(new[] { explorer, destroyer, battleship }, fighters.Concat(new[] { gunship, freighter, destroyer }));
            // The battleship fills its berths, in order; the explorer and the destroyer have none; the freighter and the
            // destroyer are never carried in a berth (only a carrier clamps a destroyer to its hull, U.SPC-T473).
            Assert.That(aboard.Count, Is.EqualTo(room));
            Assert.That(aboard.Keys, Is.EqualTo(fighters.Take(room)));
            Assert.That(aboard.Values.All(v => v == battleship), Is.True);
            Assert.That(aboard.ContainsKey(freighter), Is.False);
            Assert.That(aboard.ContainsKey(destroyer), Is.False);
            Assert.That(aboard.ContainsKey(gunship), Is.False, "no room left for a gunship");

            // A carrier takes the whole wing.
            var carrier = Hull("TNS Bastion", ShipClass.Carrier, navy);
            aboard = FtlCarriage.Stow(new[] { carrier }, fighters.Concat(new[] { gunship, freighter }));
            Assert.That(aboard.Count, Is.EqualTo(7));
            Assert.That(aboard.ContainsKey(freighter), Is.False);

            // A berth is data: registered for a new hull, it carries.
            try
            {
                FtlCarriage.RegisterBerths(ShipClass.Explorer, 1);
                aboard = FtlCarriage.Stow(new[] { explorer }, fighters);
                Assert.That(aboard.Count, Is.EqualTo(1));
            }
            finally
            {
                FtlCarriage.RegisterBerths(ShipClass.Explorer, 0);
            }
            Assert.That(FtlCarriage.BerthsOf(ShipClass.Explorer), Is.EqualTo(0));
        }

        [Test]
        public void CraftDockedAboardAreLostWithTheHullCarryingThem()
        {
            var g = new Galaxy();
            var a = StarSystem.CreateVoidBase("Void Base West", new DVec3(0, 0, 0), StartJd, g.Polities);
            var b = StarSystem.CreateVoidBase("Void Base East", new DVec3(4, 0, 0), StartJd, g.Polities);
            g.Systems.Add(a);
            g.Systems.Add(b);
            var navy = Navy();
            var carrier = Hull("TNS Bastion", ShipClass.Carrier, navy);
            var gnat = Hull("TNS Gnat", ShipClass.Fighter, navy);
            var battleship = Hull("TNS Sabre", ShipClass.Battleship, navy);
            var midge = Hull("TNS Midge", ShipClass.Fighter, navy);
            foreach (var s in new[] { gnat, battleship, midge }) s.Leader = carrier;
            var c = g.DeepSpace.Launch(new[] { carrier, gnat, battleship, midge }, a, DeepSpace.DeparturePointKm(a, b.Origin), b, CruiseDrives.Capital, StartJd);
            c.Aboard[gnat] = carrier;
            c.Aboard[midge] = battleship;

            g.DeepSpace.Destroy(c, carrier, StartJd + 1, "in a test");
            Assert.That(gnat.Destroyed, Is.True, "a fighter docked aboard goes down with its carrier");
            Assert.That(midge.Destroyed, Is.False);
            Assert.That(c.Ships, Is.EqualTo(new[] { battleship, midge }));
            Assert.That(c.Leader, Is.SameAs(battleship), "the next hull with a drive takes command");
            Assert.That(midge.Leader, Is.SameAs(battleship));
            Assert.That(c.Aboard.Keys, Is.EqualTo(new[] { midge }));
        }

        [Test]
        public void TheNextHullFlyingOnItsOwnDriveTakesCommandNotACraftDockedAboard()
        {
            var g = new Galaxy();
            var a = StarSystem.CreateVoidBase("Void Base West", new DVec3(0, 0, 0), StartJd, g.Polities);
            var b = StarSystem.CreateVoidBase("Void Base East", new DVec3(4, 0, 0), StartJd, g.Polities);
            g.Systems.Add(a);
            g.Systems.Add(b);
            var navy = Navy();
            var flagship = Hull("TNS Bastion", ShipClass.Battleship, navy);
            var carrier = Hull("TNS Hive", ShipClass.Carrier, navy);
            var gnat = Hull("TNS Gnat", ShipClass.Fighter, navy);
            foreach (var s in new[] { gnat, carrier }) s.Leader = flagship;
            // The fighter rides in the carrier, but is listed ahead of it.
            var c = g.DeepSpace.Launch(new[] { flagship, gnat, carrier }, a, DeepSpace.DeparturePointKm(a, b.Origin), b, CruiseDrives.Capital, StartJd);
            c.Aboard[gnat] = carrier;

            g.DeepSpace.Destroy(c, flagship, StartJd + 1, "in a test");
            Assert.That(c.Leader, Is.SameAs(carrier));
            Assert.That(gnat.Leader, Is.SameAs(carrier));
            Assert.That(carrier.Leader, Is.Null);
        }

        [Test]
        public void ACapitalShipCrossingOpenSpaceTakesItsFightersDockedAboardAndLeavesWhatHasNoDrive()
        {
            var g = Galaxy.CreateDefault(StartJd);
            var sol = g.Find("Sol System");
            var ac = g.Find("Alpha Centauri");
            Ship Free() => sol.World.Fleet.Ships.FirstOrDefault(s => s.Spec.IsMilitary && InterstellarTraffic.CanCruise(s) && !s.Faction.IsPirate
                && !s.PatrolWing && InterstellarTraffic.Idle(sol.World, s));
            double jd = StartJd;
            for (int i = 0; i < 300 && Free() == null; i++) g.Update(jd += 0.1);
            var ship = Free();
            Assume.That(ship, Is.Not.Null, "no warship with a drive came free to send");
            Assert.That(ship.Spec.Class, Is.AnyOf(ShipClass.Carrier, ShipClass.Battleship), "only a carrier or a battleship has a drive in a navy");
            var body = ship.Orbit.Body;
            var fleet = sol.World.Fleet;
            var gnat = fleet.SpawnAt("TNS Gnat", ShipClass.Fighter, ship.Faction, body, ship.Orbit.OffsetAt(jd), DVec3.UnitX, jd);
            var hauler = fleet.SpawnAt("TNS Tender", ShipClass.Freighter, ship.Faction, body, ship.Orbit.OffsetAt(jd), DVec3.UnitX, jd);
            // A destroyer has no FTL of its own (U.SPC-T472): it follows through open space only clamped to a carrier (U.SPC-T473).
            var screen = fleet.SpawnAt("TNS Screen", ShipClass.Destroyer, ship.Faction, body, ship.Orbit.OffsetAt(jd), DVec3.UnitX, jd);
            foreach (var e in new[] { gnat, hauler, screen })
            {
                e.Leader = ship;
                e.Role = ShipRole.Escort;
            }
            var v = g.Traffic.SendByCruise(ship, sol, ac, VoyagePurpose.Expedition, jd);
            Assert.That(v, Is.Not.Null);

            Cruise Out() => g.DeepSpace.Cruises.FirstOrDefault(c => c.Leader == ship);
            for (int i = 0; i < 400 && Out() == null; i++) g.Update(jd += 0.1);
            var cruise = Out();
            Assert.That(cruise, Is.Not.Null, "it never engaged its drive");
            if (!gnat.Destroyed && gnat.Battle == null)
            {
                Assert.That(cruise.Ships, Does.Contain(gnat), "its fighter was left behind");
                Assert.That(cruise.Aboard.TryGetValue(gnat, out var carrier) && cruise.Ships.Contains(carrier), Is.True, "the fighter is not docked aboard");
                Assert.That(fleet.Ships.Contains(gnat), Is.False);
            }
            Assert.That(cruise.Ships, Does.Not.Contain(hauler), "a freighter cannot dock and has no drive");
            Assert.That(fleet.Ships.Contains(hauler), Is.True);
            if (ship.Spec.Class == ShipClass.Carrier)
            {
                // A carrier takes it clamped to its hull (U.SPC-T473): a jump strike group.
                int used = cruise.Aboard.Where(kv => kv.Value == ship && FtlCarriage.RidesClamped(kv.Key)).Sum(kv => FtlCarriage.ClampSizeOf(kv.Key.Spec.Class));
                if (!screen.Destroyed && screen.Battle == null) Assert.That(used, Is.GreaterThan(0), "the carrier took no escort clamped to its hull");
                bool full = FtlCarriage.ClampsOf(ShipClass.Carrier) - used < FtlCarriage.ClampSizeOf(ShipClass.Destroyer);
                if (!screen.Destroyed && screen.Battle == null && !full)
                {
                    Assert.That(cruise.Aboard.TryGetValue(screen, out var hull) ? hull : null, Is.SameAs(ship), "the destroyer is not clamped to the carrier");
                    Assert.That(fleet.Ships.Contains(screen), Is.False);
                }
            }
            else
            {
                Assert.That(cruise.Ships, Does.Not.Contain(screen), "a destroyer has no drive, and a battleship no clamps to take it on");
                if (!screen.Destroyed) Assert.That(fleet.Ships.Contains(screen), Is.True);
            }
        }

        [Test]
        public void ACruiserOrDestroyerGoesBetweenTheStarsByTheGatesAndNeverUnderItsOwnDrive()
        {
            var g = Galaxy.CreateDefault(StartJd);
            var sol = g.Find("Sol System");
            var ac = g.Find("Alpha Centauri");
            Ship Free() => sol.World.Fleet.Ships.FirstOrDefault(s => (s.Spec.Class == ShipClass.Cruiser || s.Spec.Class == ShipClass.Destroyer)
                && !s.Faction.IsPirate && !s.PatrolWing && InterstellarTraffic.Idle(sol.World, s));
            double jd = StartJd;
            for (int i = 0; i < 300 && Free() == null; i++) g.Update(jd += 0.1);
            var ship = Free();
            Assume.That(ship, Is.Not.Null, "no cruiser or destroyer came free to send");
            Assert.That(g.Traffic.SendByCruise(ship, sol, ac, VoyagePurpose.Expedition, jd), Is.Null, "it has no drive of its own");
            var v = g.Traffic.Send(ship, sol, ac, VoyagePurpose.Expedition, jd);
            Assume.That(v, Is.Not.Null, "no open gate route from Sol to Alpha Centauri");
            Assert.That(v.ByCruise, Is.False);
            Assert.That(v.Route, Is.Not.Empty, "it goes by the gates");
        }
    }
}
