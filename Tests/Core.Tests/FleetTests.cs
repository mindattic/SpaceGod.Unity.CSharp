using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    [TestFixture]
    public class FleetTests
    {
        const double StartJd = 2461324.0;

        static (Fleet fleet, Ephemeris eph) Create()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            return (Fleet.CreateDefault(eph, StartJd), eph);
        }

        [Test]
        public void DefaultFleetHasEveryClassAndSomeShipsUnderway()
        {
            var (fleet, _) = Create();
            // The classic fleet predates breachers, fighters, explorers, gunships, lane interdictors, command cruisers, frigates, corvettes and lane tenders (they sail with the
            // pirates/carriers/free pilots of the full World.BuildMilitary roster, and the tenders with the World's lane upkeep,
            // U.SPC-T481, not this standalone Core.Tests fixture) -- adding
            // either here would also perturb this fixture's own chaotic N-body simulation used by other
            // tests, the same way it does World.CreateDefault's (see AvoidanceTests). Ore tugs are real hulls here too
            // (U.SPC-T498: no ship exists only as a number). The interceptor and bomber (U.SPC-T484) are strike craft
            // that fly from carriers' hangars as squadrons, and the recon fighter (U.SPC-T485) in a carrier's recon flight. Capture
            // tugs (U.SPC-T522) are real hulls of a World's belt works (World.RaiseCaptureTugs), not of this fixture.
            foreach (ShipClass c in Enum.GetValues(typeof(ShipClass)))
                if (c != ShipClass.Fighter && c != ShipClass.Explorer && c != ShipClass.Gunship
                    && c != ShipClass.Interceptor && c != ShipClass.Bomber && c != ShipClass.LaneTender && c != ShipClass.GatePicket
                    && c != ShipClass.CommandCruiser && c != ShipClass.Frigate && c != ShipClass.Corvette && c != ShipClass.Recon && c != ShipClass.AssaultShuttle
                    && c != ShipClass.GateBreaker && c != ShipClass.CaptureTug) Assert.That((fleet.Ships).Any(s => s.Spec.Class == c), Is.True);
            Assert.That(fleet.Ships.Count >= 40, Is.True);
            Assert.That((fleet.Ships).Any(s => !s.IsEscort && s.Phase == ShipPhase.Transit), Is.True);
            Assert.That((fleet.Ships).Any(s => !s.IsEscort && s.Phase == ShipPhase.Parked), Is.True);
        }

        [Test]
        public void PatrolsGoRoundTheirRoutes()
        {
            var (fleet, eph) = Create();
            var patrols = fleet.Ships.Where(s => s.Role == ShipRole.Patrol && !s.IsEscort).ToList();
            Assert.That(patrols, Is.Not.Empty);
            Assert.That(patrols.All(p => p.Route != null && p.Route.Length > 1), Is.True);
            var visited = patrols.ToDictionary(p => p, p => new System.Collections.Generic.HashSet<int>());
            for (double jd = StartJd; jd < StartJd + 120; jd += 0.5)
            {
                eph.Update(jd);
                fleet.Update(jd);
                foreach (var p in patrols) if (p.Phase == ShipPhase.Parked) visited[p].Add(p.Orbit.Body);
            }
            Assert.That(patrols.Count(p => visited[p].Count > 1), Is.GreaterThan(0), "no patrol ever left its first waypoint");
        }

        [Test]
        public void EveryEscortInAGroupHasItsOwnFormationSlot()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var world = World.CreateDefault(eph, StartJd);
            var groups = world.Fleet.Ships.Where(s => s.IsEscort).GroupBy(s => s.Leader).ToList();
            Assert.That(groups.Any(g => g.Count() > 5), Is.True, "the default roster has a group bigger than the five base slots");
            foreach (var g in groups)
                Assert.That(g.Select(s => s.FormationSlot).Distinct().Count(), Is.EqualTo(g.Count()), g.Key.Name + " has escorts stacked on one slot");
        }

        [Test]
        public void ShipsStayOutsideBodiesAndFiniteForTwoYears()
        {
            var (fleet, eph) = Create();
            var catalog = eph.Catalog;
            for (double jd = StartJd; jd < StartJd + 730; jd += 0.37)
            {
                eph.Update(jd);
                fleet.Update(jd);
                foreach (var ship in fleet.Ships)
                {
                    var st = fleet.StateOf(ship, jd);
                    Assert.That(double.IsNaN(st.HelioKm.X) || double.IsInfinity(st.HelioKm.X), Is.False, ship.Name);
                    if (ship.Controller.Phase == ShipPhase.Parked)
                    {
                        var body = catalog[ship.Controller.Orbit.Body];
                        Assert.That((st.HelioKm - eph.Helio(body.Index)).Length > body.RadiusKm * 2, Is.True, ship.Name);
                    }
                    Assert.That(st.HelioKm.Length > Fleet.SunKeepOutKm * 0.5, Is.True, $"{ship.Name} flew into the Sun");
                }
            }
        }

        [Test]
        public void PositionIsContinuousAcrossDepartureAndArrival()
        {
            var (fleet, eph) = Create();
            var ship = fleet.Ships.First(s => s.Role == ShipRole.Trader);
            for (int legs = 0; legs < 6; legs++)
            {
                double boundary = ship.PhaseEndJd;
                const double eps = 1e-7;
                eph.Update(boundary - eps);
                fleet.Update(boundary - eps);
                var before = fleet.StateOf(ship, boundary - eps).HelioKm;
                eph.Update(boundary + eps);
                fleet.Update(boundary + eps);
                var after = fleet.StateOf(ship, boundary + eps).HelioKm;
                Assert.That((after - before).Length < 50, Is.True, $"leg {legs}: jump of {(after - before).Length:F0} km");
            }
        }

        [Test]
        public void TransitTimeMatchesBrachistochrone()
        {
            var (fleet, eph) = Create();
            var ship = fleet.Ships.First(s => !s.IsEscort && s.Phase == ShipPhase.Transit);
            double t = ship.PhaseEndJd - ship.PhaseStartJd;
            double expected = 2 * Math.Sqrt(ship.PathLength / ship.AccelKmPerDay2);
            Assert.That(t, Is.EqualTo(expected).Within(1e-6));

            double mid = ship.PhaseStartJd + t / 2;
            eph.Update(mid);
            var st = fleet.StateOf(ship, mid);
            Assert.That(st.Progress, Is.InRange(0.499, 0.501));

            // Reported speed is relative to the transit frame, and matches the motion.
            const double h = 1e-5;
            DVec3 Rel(double jd) => fleet.StateOf(ship, jd).HelioKm - eph.HelioAt(ship.FrameBody, jd);
            double numeric = (Rel(mid + h) - Rel(mid - h)).Length / (2 * h) / Astro.SecondsPerDay;
            Assert.That(Math.Abs(numeric - st.SpeedKmS) < 1e-4 * st.SpeedKmS, Is.True, $"reported {st.SpeedKmS:0.000} km/s, moving at {numeric:0.000}");
        }

        [Test]
        public void TransitsLeaveAndArriveAtTheirParkingOrbitsVelocity()
        {
            var (fleet, eph) = Create();
            int matched = 0;
            foreach (var ship in fleet.Ships.Where(s => !s.IsEscort && s.Phase == ShipPhase.Transit))
            {
                if (ship.DepartureVelocity.LengthSquared == 0) continue;
                matched++;
                const double h = 1e-7;
                var origin = ship.OriginBody;
                var destination = ship.Orbit.Body;
                double t0 = ship.PhaseStartJd, t1 = ship.PhaseEndJd;
                DVec3 Rel(double jd, int body) => fleet.StateOf(ship, jd).HelioKm - eph.HelioAt(body, jd);
                var leave = (Rel(t0 + h, origin) - Rel(t0, origin)) / h;
                var parked = ship.DepartureOrbit.VelocityAt(t0);
                Assert.That((leave - parked).Length < 0.02 * parked.Length + 50, Is.True, $"{ship.Name} leaves at {(leave - parked).Length:0} km/day off");
                var arrive = (Rel(t1, destination) - Rel(t1 - h, destination)) / h;
                var orbit = ship.Orbit.VelocityAt(t1);
                Assert.That((arrive - orbit).Length < 0.02 * orbit.Length + 50, Is.True, $"{ship.Name} arrives at {(arrive - orbit).Length:0} km/day off");
            }
            Assert.That(matched > 5, Is.True);
        }

        [Test]
        public void EscortsShareTheirLeadersPosition()
        {
            var (fleet, eph) = Create();
            var escort = fleet.Ships.First(s => s.IsEscort);
            var a = fleet.StateOf(escort, StartJd).HelioKm;
            var b = fleet.StateOf(escort.Leader, StartJd).HelioKm;
            Assert.That(b, Is.EqualTo(a));
        }

        [Test]
        public void ArrivalsMeetTheMovingTarget()
        {
            var (fleet, eph) = Create();
            var ship = fleet.Ships.First(s => !s.IsEscort && s.Phase == ShipPhase.Transit);
            double arrive = ship.PhaseEndJd;
            var end = eph.HelioAt(ship.FrameBody, arrive) + Fleet.TransitOffset(ship, 1);
            var target = eph.HelioAt(ship.DestinationBody, arrive) + ship.Orbit.OffsetAt(arrive);
            Assert.That((end - target).Length < 1.0, Is.True);
        }

        [Test]
        public void SpawnAtParksAFightableShipExactlyAtTheGivenOffsetAndFacing()
        {
            var (fleet, eph) = Create();
            int earth = eph.Catalog["Earth"].Index;
            var faction = fleet.Factions[0];
            var offset = new DVec3(5000, 0, 2000);
            var forward = new DVec3(0, 1, 0);

            var ship = fleet.SpawnAt("Test Hull", ShipClass.Destroyer, faction, earth, offset, forward, StartJd);

            Assert.That(ship.Phase, Is.EqualTo(ShipPhase.Parked));
            Assert.That(ship.Orbit.Body, Is.EqualTo(earth));
            Assert.That(ship.Orbit.OffsetAt(StartJd), Is.EqualTo(offset));
            Assert.That(ship.Orbit.TangentAt(StartJd), Is.EqualTo(forward.Normalized));
            Assert.That(ship.Faction, Is.EqualTo(faction));
            Assert.That(ship.Spec.Class, Is.EqualTo(ShipClass.Destroyer));
            Assert.That(ship.Combat, Is.Not.Null);
            Assert.That(ship.Combat.Spec.Hull > 0, Is.True);
            Assert.That(fleet.Ships, Does.Contain(ship));
        }

        [Test]
        public void SpawnAtGivesAFreeShipEveryVisibleBodyAsAPortSoItNeverCrashesIfReleased()
        {
            var (fleet, eph) = Create();
            int earth = eph.Catalog["Earth"].Index;
            var faction = fleet.Factions[0];
            var ship = fleet.SpawnAt("Test Hull", ShipClass.Courier, faction, earth, DVec3.Zero, DVec3.UnitX, StartJd);

            Assert.That(ship.Role, Is.EqualTo(ShipRole.Free));
            Assert.That(ship.Ports, Is.Not.Null);
            Assert.That(ship.Ports, Does.Contain(earth));
            fleet.ScheduleNext(ship, StartJd);
        }

        [Test]
        public void ASpawnedShipLeftToItselfPicksItsOwnNextTrip()
        {
            var (fleet, eph) = Create();
            int earth = eph.Catalog["Earth"].Index;
            var ship = fleet.SpawnAt("Test Hull", ShipClass.Courier, fleet.Factions[0], earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd);
            // Nobody pilots it and no battle holds it: the fleet's own schedule takes it from here.
            for (double jd = StartJd; jd < StartJd + 20; jd += 0.5)
            {
                eph.Update(jd);
                Assert.DoesNotThrow(() => fleet.Update(jd));
            }
            Assert.That(ship.DestinationBody >= 0, Is.True);
        }
    }
}
