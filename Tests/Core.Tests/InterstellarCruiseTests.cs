using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Continuous interstellar flight (U.SPC-T412): places held to the same precision anywhere in the galaxy, cruise
    /// drives whose trips are long but finishable, groups flying through open space between the stars (meeting, fighting,
    /// stripping wrecks), and arriving by dropping out at the edge of a star's well and flying in.
    /// </summary>
    [TestFixture]
    public class InterstellarCruiseTests
    {
        const double StartJd = 2461324.0;

        // ---------------------------------------------------------------- hierarchical coordinates

        [Test]
        public void AGalacticPositionHoldsAMillimetreFiftyThousandLightYearsOut()
        {
            var far = GalacticPosition.FromLy(new DVec3(50000, 1234.5, -20.25));
            var nudged = far.Offset(new DVec3(1e-6, -2e-6, 3e-6)); // a millimetre or so
            var d = nudged.KmFrom(far);
            // To a tenth of a millimetre.
            Assert.That(d.X, Is.EqualTo(1e-6).Within(1e-7));
            Assert.That(d.Y, Is.EqualTo(-2e-6).Within(1e-7));
            Assert.That(d.Z, Is.EqualTo(3e-6).Within(1e-7));
            // One double of km from Sol cannot even see a metre out there.
            double naive = 50000 * Astro.LightYearKm;
            Assert.That(naive + 1e-3 - naive, Is.Not.EqualTo(1e-3), "a plain double would have held it too");
            // The offset never strays more than half a sector from its sector's centre.
            foreach (var p in new[] { far, nudged })
            {
                Assert.That(Math.Abs(p.LocalKm.X), Is.LessThanOrEqualTo(GalacticPosition.SectorKm / 2));
                Assert.That(Math.Abs(p.LocalKm.Y), Is.LessThanOrEqualTo(GalacticPosition.SectorKm / 2));
                Assert.That(Math.Abs(p.LocalKm.Z), Is.LessThanOrEqualTo(GalacticPosition.SectorKm / 2));
            }
        }

        [Test]
        public void AGalacticPositionRoundTripsAndCarriesAcrossSectors()
        {
            var ly = new DVec3(-8.77, 4.3, 0.12);
            var p = GalacticPosition.FromLy(ly);
            Assert.That((p.Ly - ly).Length, Is.LessThan(1e-12));
            // Light-years out and back again lands where it started, to a fraction of a millimetre.
            var hop = new DVec3(3.3, -1.7, 0.4) * Astro.LightYearKm;
            var there = p.Offset(hop);
            Assert.That(there.DistanceLy(p), Is.EqualTo(hop.Length / Astro.LightYearKm).Within(1e-12));
            Assert.That(there.Offset(-hop).DistanceKm(p), Is.LessThan(1e-3));
            Assert.That(GalacticPosition.Lerp(p, there, 0.5).KmFrom(p).Length, Is.EqualTo(hop.Length / 2).Within(1e-3));
            Assert.That(p.Offset(DVec3.Zero), Is.EqualTo(p));
        }

        [Test]
        public void ASystemsOwnKilometresMapIntoTheGalaxyAndBack()
        {
            var g = new Galaxy();
            var a = StarSystem.CreateVoidBase("Void Base Alpha", new DVec3(30000, -12000, 40), StartJd, g.Polities);
            var b = StarSystem.CreateVoidBase("Void Base Beta", new DVec3(30003, -12000, 40), StartJd, g.Polities);
            var local = new DVec3(1.2e8, -3.4e7, 5.5e6);
            var place = a.At(local);
            Assert.That((a.LocalKm(place) - local).Length, Is.LessThan(1e-6));
            // Seen from the next system, three light-years on: the two frames agree to the millimetre, thirty thousand
            // light-years out (where the catalogue's own light-years only place the stars to some tens of km).
            var fromB = b.LocalKm(place);
            Assert.That((fromB - (local + a.Origin.KmFrom(b.Origin))).Length, Is.LessThan(1e-6), "lost precision between neighbouring systems far from Sol");
            Assert.That(b.Origin.KmFrom(a.Origin).X, Is.EqualTo(3 * Astro.LightYearKm).Within(200));
        }

        // ---------------------------------------------------------------- the drive

        [Test]
        public void CapitalShipsAndSurveyHullsCarryACruiseDriveAndSmallCraftDoNot()
        {
            foreach (var c in new[] { ShipClass.Carrier, ShipClass.Battleship })
                Assert.That(CruiseDrives.Of(c), Is.SameAs(CruiseDrives.Capital), c.ToString());
            Assert.That(CruiseDrives.Of(ShipClass.Explorer), Is.SameAs(CruiseDrives.Survey));
            // Cruisers and destroyers have no FTL of their own (U.SPC-T472): they go by the gates.
            foreach (var c in new[] { ShipClass.Cruiser, ShipClass.Destroyer, ShipClass.Fighter, ShipClass.Courier, ShipClass.Freighter,
                                      ShipClass.Miner, ShipClass.Gunship })
                Assert.That(CruiseDrives.Of(c), Is.Null, c.ToString());
            // A new drive plugs in by registering it.
            var tug = new CruiseDriveSpec("Tug drive", 50, 1, 0.5);
            try
            {
                CruiseDrives.Register(ShipClass.Miner, tug);
                Assert.That(CruiseDrives.Of(ShipClass.Miner), Is.SameAs(tug));
            }
            finally
            {
                CruiseDrives.Register(ShipClass.Miner, null);
            }
            Assert.That(CruiseDrives.Of(ShipClass.Miner), Is.Null);
        }

        [Test]
        public void ACruiseIsLongButFinishableAndSlowerThanAGate()
        {
            var drive = CruiseDrives.Capital;
            Assert.That(drive.LyPerDay, Is.EqualTo(1).Within(1e-9), "a capital drive is rated at a light-year a day");
            var sol = CruiseProfile.For(drive, 4.37);
            Assert.That(sol.TotalDays, Is.EqualTo(drive.SpoolDays + drive.RampDays + 4.37).Within(1e-9));
            // A gate is the shortcut: six hours plus two a light-year.
            Assert.That(sol.TotalDays, Is.GreaterThan(GateNetwork.TransitDays(4.37) * 3));
            // Across the whole galaxy: centuries, but a number, and the clock runs at up to a year a second.
            var galaxy = CruiseProfile.For(drive, 100000);
            Assert.That(galaxy.TotalDays / 365.25, Is.InRange(200, 400));

            // The speed rises, holds and falls; the distance only grows, and ends at the leg's length.
            double last = 0;
            for (double t = 0; t <= sol.TotalDays + 0.5; t += 0.01)
            {
                double ly = sol.LyAt(t);
                Assert.That(ly, Is.GreaterThanOrEqualTo(last - 1e-12));
                Assert.That(sol.SpeedAt(t), Is.LessThanOrEqualTo(drive.LyPerDay + 1e-9));
                last = ly;
            }
            Assert.That(sol.LyAt(sol.TotalDays), Is.EqualTo(4.37).Within(1e-9));
            Assert.That(sol.LyAt(drive.SpoolDays), Is.EqualTo(0), "nothing moves while it charges");
            // A hop too short to reach cruise speed peaks and comes straight down.
            var hop = CruiseProfile.For(drive, 0.01);
            Assert.That(hop.LyAt(hop.TotalDays), Is.EqualTo(0.01).Within(1e-12));
            Assert.That(hop.SpeedAt(hop.SpoolDays + hop.FlightDays / 2), Is.LessThan(drive.LyPerDay));
        }

        [Test]
        public void TheMenuTunesCruiseSpeedAndTheWellLimit()
        {
            var speed = CruiseDrives.SpeedScale;
            var well = CruiseDrives.WellLimit;
            Assert.That(speed.Group, Is.EqualTo(SettingGroups.Ftl));
            Assert.That(well.Group, Is.EqualTo(SettingGroups.Ftl));
            try
            {
                speed.Set(2);
                Assert.That(CruiseDrives.Capital.LyPerDay, Is.EqualTo(2).Within(1e-9));
                well.Set(5);
                Assert.That(CruiseDrives.WellLimitKm(null), Is.EqualTo(5 * Astro.AuKm).Within(1));
            }
            finally
            {
                speed.Reset();
                well.Reset();
            }
            Assert.That(CruiseDrives.Capital.LyPerDay, Is.EqualTo(1).Within(1e-9));
        }

        // ---------------------------------------------------------------- open space, on its own

        /// <summary>Two empty relays some light-years apart (no planets, no traffic): open space to fly across and little else.</summary>
        static (Galaxy g, StarSystem a, StarSystem b) Relays(double ly = 4, double far = 0)
        {
            var g = new Galaxy();
            var a = StarSystem.CreateVoidBase("Void Base West", new DVec3(far, 0, 0), StartJd, g.Polities);
            var b = StarSystem.CreateVoidBase("Void Base East", new DVec3(far + ly, 0, 0), StartJd, g.Polities);
            g.Systems.Add(a);
            g.Systems.Add(b);
            return (g, a, b);
        }

        static Ship Hull(string name, ShipClass cls, Faction faction) => new Ship
        {
            Name = name, Spec = ShipClassSpec.Of(cls), Faction = faction, Role = ShipRole.Free, Combat = new ShipCombat(CombatSpec.Of(cls), cls),
        };

        static void Run(Galaxy g, double from, double to, double step = 0.05)
        {
            for (double jd = from + step; jd <= to + 1e-9; jd += step) g.Update(jd);
        }

        [Test]
        public void AGroupFliesThroughOpenSpaceAndDropsOutAtTheEdgeOfTheFarWell()
        {
            var (g, a, b) = Relays(4);
            var navy = new Faction("Test Navy", "TNS", default) { IsMilitary = true };
            var flagship = Hull("TNS Meridian", ShipClass.Cruiser, navy);
            var escort = Hull("TNS Ardent", ShipClass.Destroyer, navy);
            escort.Leader = flagship;
            escort.Role = ShipRole.Escort;
            var from = DeepSpace.DeparturePointKm(a, b.Origin);
            Assert.That(from.Length, Is.EqualTo(CruiseDrives.WellLimitKm(a)).Within(1e-3), "it engages at the edge of the well");

            var c = g.DeepSpace.Launch(new[] { flagship, escort }, a, from, b, CruiseDrives.Capital, StartJd);
            Assert.That(g.DeepSpace.InFlight().Count(), Is.EqualTo(2));
            double eta = c.ArriveJd;
            Assert.That(eta - StartJd, Is.EqualTo(CruiseProfile.For(CruiseDrives.Capital, c.Profile.Ly).TotalDays).Within(1e-9));

            // Somewhere real the whole way, moving steadily from one star toward the other.
            double lastLy = -1;
            for (double jd = StartJd + 0.25; jd < eta; jd += 0.25)
            {
                g.Update(jd);
                var at = c.PositionAt(jd);
                double out_ = at.KmFrom(a.Origin).X / Astro.LightYearKm;
                Assert.That(out_, Is.GreaterThanOrEqualTo(lastLy));
                lastLy = out_;
                Assert.That(at.KmFrom(a.Origin).Y, Is.EqualTo(0).Within(1e-3), "off the line between the stars");
            }
            Assert.That(lastLy, Is.InRange(3, 4));
            Assert.That(b.World.Fleet.Ships.Any(s => s.Name == flagship.Name), Is.False, "arrived early");

            Run(g, Math.Floor(eta / 0.05) * 0.05, eta + 0.2);
            Assert.That(g.DeepSpace.Cruises, Is.Empty);
            var arrived = b.World.Fleet.Ships.Single(s => s.Name == flagship.Name);
            var arrivedEscort = b.World.Fleet.Ships.Single(s => s.Name == escort.Name);
            Assert.That(arrivedEscort.Leader, Is.SameAs(arrived), "it arrives as a group");
            // In at the near edge of the far well, on the side it came from, to fly the rest of the way.
            var where = arrived.Orbit.Center;
            Assert.That(where.Length, Is.EqualTo(CruiseDrives.WellLimitKm(b)).Within(1e-3));
            Assert.That(where.X, Is.LessThan(0), "it comes in on the side facing the star it left");
            Assert.That(b.World.Events.Any(e => e.Text.Contains("drop") && e.Text.Contains("out of cruise")), Is.True);
        }

        [Test]
        public void FarOutInTheGalaxyAnArrivalIsPlacedAsExactlyAsNextToSol()
        {
            var (g, a, b) = Relays(5, far: 40000);
            var navy = new Faction("Test Navy", "TNS", default) { IsMilitary = true };
            var ship = Hull("TNS Faraway", ShipClass.Battleship, navy);
            var c = g.DeepSpace.Launch(new[] { ship }, a, DeepSpace.DeparturePointKm(a, b.Origin), b, CruiseDrives.Capital, StartJd);
            Run(g, StartJd, c.ArriveJd + 0.1);
            var arrived = b.World.Fleet.Ships.Single(s => s.Name == ship.Name);
            // Forty thousand light-years out, one double of km would be off by tens of km; here the well's edge is hit to the metre.
            Assert.That(arrived.Orbit.Center.Length, Is.EqualTo(CruiseDrives.WellLimitKm(b)).Within(1e-3));
        }

        [Test]
        public void HostileGroupsWhosePathsCrossBetweenTheStarsFightAndTheBeatenAreLeftWrecked()
        {
            var (g, a, b) = Relays(4);
            var navy = new Faction("Test Navy", "TNS", default) { IsMilitary = true };
            var clan = new Faction("Test Clan", "", default) { IsPirate = true };
            var east = g.DeepSpace.Launch(new[] { Hull("TNS Dauntless", ShipClass.Battleship, navy), Hull("TNS Sabre", ShipClass.Cruiser, navy) },
                a, DeepSpace.DeparturePointKm(a, b.Origin), b, CruiseDrives.Capital, StartJd);
            var west = g.DeepSpace.Launch(new[] { Hull("Black Gull", ShipClass.Destroyer, clan) },
                b, DeepSpace.DeparturePointKm(b, a.Origin), a, CruiseDrives.Capital, StartJd);
            Run(g, StartJd, StartJd + 6);

            // Head on along the same line: they met, and one of them is wrecks out here now.
            Assert.That(g.DeepSpace.Events.Any(e => e.Text.Contains("pulls") && e.Text.Contains("out of cruise")), Is.True, "they never met");
            Assert.That(g.DeepSpace.Events.Any(e => e.Text.Contains("destroyed in open space")), Is.True);
            int survivors = (a.World.Fleet.Ships.Count(s => !s.Destroyed) > 0 ? 1 : 0) + (b.World.Fleet.Ships.Count(s => !s.Destroyed) > 0 ? 1 : 0);
            Assert.That(survivors, Is.EqualTo(1), "one side should have got through");
            var wrecks = g.DeepSpace.Objects.Where(o => o.Kind == DeepSpaceObjectKind.Wreck).ToList();
            // Left half way between the stars, not in either system: still there, or stripped by the winners going on past it.
            bool stripped = g.DeepSpace.Events.Any(e => e.Text.Contains("to strip the wreck"));
            Assert.That(wrecks.Count > 0 || stripped, Is.True, "no wreck was left out here");
            foreach (var w in wrecks)
                Assert.That(w.Position.KmFrom(a.Origin).X / Astro.LightYearKm, Is.InRange(1, 3));
            Assert.That(east.Ships.Count == 0 || west.Ships.Count == 0, Is.True);
        }

        [Test]
        public void RaidersRobALadenHullBetweenTheStarsAndLetItGo()
        {
            var (g, a, b) = Relays(4);
            var clan = new Faction("Test Clan", "", default) { IsPirate = true };
            var traders = new Faction("Test Traders", "", default);
            var courier = Hull("Swift Margin", ShipClass.Explorer, traders);
            courier.Combat = null; // nothing to fight with
            courier.HasCargo = true;
            courier.CargoGood = (Good)0;
            courier.CargoTonnes = 40;
            courier.CargoCost = 90000;
            var raider = Hull("Black Gull", ShipClass.Destroyer, clan);
            g.DeepSpace.Launch(new[] { courier }, a, DeepSpace.DeparturePointKm(a, b.Origin), b, CruiseDrives.Survey, StartJd);
            g.DeepSpace.Launch(new[] { raider }, b, DeepSpace.DeparturePointKm(b, a.Origin), a, CruiseDrives.Capital, StartJd);
            Run(g, StartJd, StartJd + 6);

            Assert.That(g.DeepSpace.Events.Any(e => e.Text.Contains("rob")), Is.True, "the raiders never ran it down");
            Assert.That(courier.Destroyed, Is.False, "a hull is worth more robbed than shot apart");
            Assert.That(courier.HasCargo, Is.False);
            Assert.That(raider.Plunder[0], Is.EqualTo(40).Within(1e-9));
            Assert.That(g.DeepSpace.Objects, Is.Empty);
            // Robbed, it still gets where it was going.
            Assert.That(b.World.Fleet.Ships.Any(s => s.Name == courier.Name), Is.True);
        }

        [Test]
        public void AGroupPassingAWreckDropsOutToStripItAndGoesOn()
        {
            var (g, a, b) = Relays(4);
            var traders = new Faction("Test Traders", "", default);
            var courier = Hull("Long Odds", ShipClass.Explorer, traders);
            var from = DeepSpace.DeparturePointKm(a, b.Origin);
            var plain = CruiseProfile.For(CruiseDrives.Survey, DeepSpace.ArrivalPoint(b.Origin, a.At(from), CruiseDrives.WellLimitKm(b)).DistanceLy(a.At(from)));
            g.DeepSpace.Add(new DeepSpaceObject
            {
                Kind = DeepSpaceObjectKind.Wreck, Name = "the wreck of the Old Mercy", Position = a.Origin.Offset(new DVec3(2 * Astro.LightYearKm, 2e8, 0)),
                SalvageCredits = 125000, SinceJd = StartJd - 400,
            });
            var c = g.DeepSpace.Launch(new[] { courier }, a, from, b, CruiseDrives.Survey, StartJd);
            Run(g, StartJd, StartJd + plain.TotalDays + 2);

            Assert.That(g.DeepSpace.Objects, Is.Empty, "it flew past the wreck");
            Assert.That(courier.Credits, Is.EqualTo(125000).Within(1e-6));
            Assert.That(traders.Treasury, Is.EqualTo(125000).Within(1e-6));
            var arrived = b.World.Fleet.Ships.SingleOrDefault(s => s.Name == courier.Name);
            Assert.That(arrived, Is.Not.Null, "it never went on after stripping the wreck");
            // Stopping to strip it cost it the time: the salvage, then a fresh charge and run-up from a standstill.
            Assert.That(arrived.PhaseStartJd - StartJd, Is.GreaterThan(plain.TotalDays + WreckSalvage.SalvageDays));
        }

        [Test]
        public void AGroupBetweenTheStarsSavesAndCarriesOnExactlyAsItWas()
        {
            var (g, a, b) = Relays(4);
            var navy = new Faction("Test Navy", "TNS", default) { IsMilitary = true };
            var c = g.DeepSpace.Launch(new[] { Hull("TNS Constant", ShipClass.Carrier, navy) }, a, DeepSpace.DeparturePointKm(a, b.Origin), b,
                CruiseDrives.Capital, StartJd);
            Run(g, StartJd, StartJd + 2);
            var copy = ObjectGraph.Clone(g);
            var cc = copy.DeepSpace.Cruises.Single();
            Assert.That(cc.PositionAt(StartJd + 2.3).KmFrom(c.PositionAt(StartJd + 2.3)).Length, Is.LessThan(1e-6));
            Assert.That(cc.ArriveJd, Is.EqualTo(c.ArriveJd));
            Run(copy, StartJd + 2, StartJd + 6);
            Assert.That(copy.Systems[1].World.Fleet.Ships.Any(s => s.Name == "TNS Constant"), Is.True, "the loaded cruise never arrived");
        }

        // ---------------------------------------------------------------- with the traffic

        static double RunUntil(Galaxy g, double fromJd, double days, Func<bool> done, double step = 0.25)
        {
            double jd = fromJd;
            while (jd < fromJd + days && !done())
            {
                jd += step;
                g.Update(jd);
            }
            return jd;
        }

        [Test]
        public void AWarshipLeavesSolUnderItsOwnDriveAndFliesIntoAlphaCentauri()
        {
            var g = Galaxy.CreateDefault(StartJd);
            var sol = g.Find("Sol System");
            var ac = g.Find("Alpha Centauri");
            Ship Free() => sol.World.Fleet.Ships.FirstOrDefault(s => s.Spec.IsMilitary && InterstellarTraffic.CanCruise(s) && !s.Faction.IsPirate
                && !s.PatrolWing && InterstellarTraffic.Idle(sol.World, s));
            double jd = RunUntil(g, StartJd, 30, () => Free() != null);
            var ship = Free();
            Assume.That(ship, Is.Not.Null, "no warship came free to send");
            string name = ship.Name;
            int originId = ship.Id;
            var v = g.Traffic.SendByCruise(ship, sol, ac, VoyagePurpose.Expedition, jd);
            Assert.That(v, Is.Not.Null);
            Assert.That(v.ByCruise, Is.True);
            Assert.That(v.Route, Is.Empty, "no gate on the way");
            Assert.That(ship.GateKm.Value.Length, Is.EqualTo(CruiseDrives.WellLimitKm(sol)).Within(1));

            // Out to the edge of the well under its ordinary drive, then out into open space.
            jd = RunUntil(g, jd, 40, () => g.DeepSpace.Cruises.Any(c => c.Leader.Name == name));
            var cruise = g.DeepSpace.Cruises.FirstOrDefault(c => c.Leader.Name == name);
            Assert.That(cruise, Is.Not.Null, "it never engaged its drive");
            // The same hull is out there cruising (U.SPC-T453, never a copy): it left Sol's own fleet list, a gone
            // marker left in its old slot so no other id there moves.
            Assert.That(cruise.Leader, Is.SameAs(ship));
            Assert.That(ship.Destroyed, Is.False, "the hull itself is alive and cruising, not a retired husk");
            Assert.That(sol.World.Fleet.Ships[originId], Is.Not.SameAs(ship));
            Assert.That(sol.World.Fleet.Ships[originId].Destroyed && sol.World.Fleet.Ships[originId].GateDeparted, Is.True,
                "its old slot in Sol's fleet is left a gone marker");
            Assert.That(g.Traffic.InTransit().Any(s => s.Name == name), Is.True);
            Assert.That(cruise.Leader.Voyage, Is.SameAs(v));

            Ship At(StarSystem s) => s.World.Fleet.Ships.FirstOrDefault(x => x.Name == name && !x.Destroyed);
            double engaged = jd;
            jd = RunUntil(g, jd, 30, () => At(ac) != null);
            var there = At(ac);
            Assert.That(there, Is.Not.Null, "never reached Alpha Centauri");
            Assert.That(jd - engaged, Is.GreaterThan(Galaxy.DistanceLy(sol, ac) * 0.9), "it crossed faster than its drive goes");
            Assert.That(ac.World.Events.Any(e => e.Text.Contains(name) && e.Text.Contains("under its own drive")), Is.True);
            // Arrived at the edge of the well, it flies in from there to its business, as any ship in the system would.
            double startKm = there.Orbit.Center.Length;
            Assert.That(startKm, Is.EqualTo(CruiseDrives.WellLimitKm(ac)).Within(1e3));
            int root = ac.Catalog.Root.Index;
            RunUntil(g, jd, 10, () => there.Destroyed || there.Phase != ShipPhase.Parked || there.Orbit.Body != root);
            Assert.That(there.Phase != ShipPhase.Parked || there.Orbit.Body != root, Is.True, "it sat where it dropped out");
            Assert.That(there.DestinationBody, Is.Not.EqualTo(root), "it never set off for anywhere in the system");
        }

        [Test]
        public void ASurveyHullGoesUnderItsOwnDriveToAStarNobodySimulatesAndComesHome()
        {
            var g = Galaxy.CreateDefault(StartJd);
            var sol = g.Find("Sol System");
            var star = g.NearbyStars.OrderBy(n => n.Origin.DistanceLy(sol.Origin)).First();
            var ship = sol.World.Fleet.Ships.First(s => s.Role == ShipRole.Free && s.Spec.Class == ShipClass.Explorer && !s.Destroyed
                && s.Battle == null && sol.World.War.Danger(s.Orbit.Body) == 0);
            string name = ship.Name;
            double jd = RunUntil(g, StartJd, 20, () => InterstellarTraffic.Idle(sol.World, ship));
            Assume.That(InterstellarTraffic.Idle(sol.World, ship), Is.True);
            var v = g.Traffic.SendToStar(ship, sol, star, VoyagePurpose.Survey, jd);
            Assert.That(v, Is.Not.Null);

            bool Reached() => g.DeepSpace.Events.Any(e => e.Text.StartsWith(name) && e.Text.Contains("out at " + star.Name));
            jd = RunUntil(g, jd, 60, Reached);
            Assert.That(Reached(), Is.True, "never reached " + star.Name);
            var cruise = g.DeepSpace.Cruises.Single(c => c.Leader.Name == name);
            // Holding at the edge of that star's well, out in open space, a while before it sets out for home.
            Assert.That(cruise.PositionAt(jd).DistanceKm(star.Origin), Is.EqualTo(CruiseDrives.WellLimitKm(null)).Within(1));
            Assert.That(cruise.Engaged(jd), Is.False);
            Assert.That(cruise.To, Is.SameAs(sol));
            Assert.That(g.Traffic.Arrivals[VoyagePurpose.Survey], Is.GreaterThanOrEqualTo(1));

            Ship At(StarSystem s) => s.World.Fleet.Ships.FirstOrDefault(x => x.Name == name && !x.Destroyed);
            jd = RunUntil(g, jd, 60, () => At(sol) != null);
            var back = At(sol);
            Assert.That(back, Is.Not.Null, "never came home");
            Assert.That(back.Voyage, Is.Null);
            Assert.That(back.Role, Is.EqualTo(ShipRole.Free));
            Assert.That(g.DeepSpace.Cruises.Any(c => c.Leader.Name == name), Is.False);
        }

        [Test]
        public void WithNoWayThroughTheGatesAHullWithADriveGoesThroughOpenSpace()
        {
            var g = Galaxy.CreateDefault(StartJd);
            var sol = g.Find("Sol System");
            var ac = g.Find("Alpha Centauri");
            var ship = sol.World.Fleet.Ships.First(s => s.Role == ShipRole.Free && s.Spec.Class == ShipClass.Explorer && !s.Destroyed
                && s.Battle == null && sol.World.War.Danger(s.Orbit.Body) == 0);
            double jd = RunUntil(g, StartJd, 20, () => InterstellarTraffic.Idle(sol.World, ship));
            Assume.That(InterstellarTraffic.Idle(sol.World, ship), Is.True);
            // Every ring out of Sol out of action.
            foreach (var gate in sol.Gates) gate.Integrity = 0;
            var v = g.Traffic.Send(ship, sol, ac, VoyagePurpose.Survey, jd);
            Assert.That(v, Is.Not.Null, "a hull with a drive of its own is not stuck behind broken gates");
            Assert.That(v.ByCruise, Is.True);
            // One with none still is.
            var freighter = sol.World.Fleet.Ships.First(s => s.Spec.Class == ShipClass.Freighter && InterstellarTraffic.Idle(sol.World, s));
            Assert.That(g.Traffic.Send(freighter, sol, ac, VoyagePurpose.Trade, jd), Is.Null);
        }
    }
}
