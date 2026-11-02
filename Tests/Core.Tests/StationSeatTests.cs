using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>Stations as embodiables (U.SPC-T447): a settlement's defence platform or a gate fort taken whole, its guns
    /// laid together on the player's aim in a fight or out of one, its craft launched, and its crew back on hand-back.</summary>
    [TestFixture]
    public class StationSeatTests
    {
        const double StartJd = 2461324.0;

        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        static double Health(BattleUnit u) => u.Combat.Hull + u.Combat.Subsystems.Sum(s => s.Hp + s.Shield);

        static Ship Hull(ShipClass c, Faction f, int id) => new Ship
        {
            Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c),
        };

        /// <summary>A settlement's defence platform at the origin and an enemy cruiser 8 km off along <paramref name="enemyDir"/>.</summary>
        static (Battle b, Settlement port, BattleUnit station, BattleUnit enemy) StationFight(DVec3 enemyDir, ShipCombat combat = null)
        {
            var blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, 3);
            var port = new Settlement { Name = "Vesta Highport", Owner = blue };
            var platform = combat ?? new ShipCombat(CombatSpec.Platform);
            port.Platforms.Add(platform);
            var station = b.AddPlatform(port, platform, 0, DVec3.Zero);
            var enemy = b.AddShip(Hull(ShipClass.Cruiser, red, 1), 1, enemyDir.Normalized * 8, -enemyDir.Normalized);
            return (b, port, station, enemy);
        }

        static bool StationsGun(Battle b, Subsystem s) => b.Manned.TryGetValue(s, out var m) && m.ByStation;

        [Test]
        public void AStationInAFightIsATakableStationAndHostsItsGuns()
        {
            var (b, port, station, _) = StationFight(DVec3.UnitY);
            var seat = Embodiables.StationOf(null, b, station);
            Assert.That(seat, Is.Not.Null);
            Assert.That(seat.Kind, Is.EqualTo(EmbodimentKind.Station));
            Assert.That(seat.CanEmbody, Is.True, seat.Blocker);
            Assert.That(seat.Faction, Is.SameAs(port.Owner));
            Assert.That(seat.Host, Is.Null, "a station is mounted on nothing");
            Assert.That(seat.Name, Does.Contain("Vesta Highport"));
            Assert.That(Embodiables.TurretsOn(b, station).All(t => Equals(t.Host, seat)), Is.True, "every gun seat moves up to the station");
            Assert.That(Embodiables.StationOf(null, b, b.Units.First(u => u.Ship != null)), Is.Null, "a ship is not a station");
        }

        [Test]
        public void ATakenStationLaysEveryGunOnTheAimAndFiresThemOnTheTrigger()
        {
            var (b, _, station, enemy) = StationFight(DVec3.UnitY);
            var seat = Embodiables.StationOf(null, b, station);
            var session = new EmbodimentSession();
            Assert.That(session.Switch(seat), Is.True);
            Assert.That(seat.Embodied, Is.True);
            var guns = station.Combat.Subsystems.Where(s => MountedTurret.IsMannable(s.Def)).ToList();
            Assert.That(guns.All(s => StationsGun(b, s)), Is.True, "the crew stands aside from every gun");
            Assert.That(station.Combat.Subsystems.Where(s => s.Kind == SubsystemKind.MissileLauncher).Any(s => b.Manned.ContainsKey(s)), Is.False,
                "its launchers stay its crew's");
            Assert.That(Embodiables.TurretsOn(b, station).Any(t => t.Embodied), Is.False, "no single gun is held on its own");

            double before = Health(enemy);
            for (double t = 0; t < 40; t += 0.1)
            {
                session.Step(new EmbodimentInput { Aim = (enemy.Pos - station.Pos).Normalized, Primary = true }, 0.1);
                b.Advance(0.1);
            }
            Assert.That(Health(enemy), Is.LessThan(before), "its guns were laid on the enemy and fired");
            var laser = station.Combat.Subsystems.First(s => s.Kind == SubsystemKind.HeavyLaser && !s.Destroyed);
            Assert.That(AngleDeg(laser.Aim, enemy.Pos - station.PointOf(laser.Def)), Is.LessThan(5), "the heavy laser is on the enemy");

            session.Release();
            Assert.That(seat.Embodied, Is.False);
            Assert.That(b.Manned, Is.Empty, "handed back, every gun is its crew's again");
        }

        [Test]
        public void ATakenStationHoldsFireUntilTheTrigger()
        {
            var (b, _, station, enemy) = StationFight(DVec3.UnitX);
            foreach (var s in station.Combat.Subsystems)
                if (s.Def.IsWeapon && !MountedTurret.IsMannable(s.Def)) s.Hp = 0;
            var session = new EmbodimentSession();
            session.Switch(Embodiables.StationOf(null, b, station));
            double before = Health(enemy);
            for (double t = 0; t < 30; t += 0.1)
            {
                session.Step(new EmbodimentInput { Aim = (enemy.Pos - station.Pos).Normalized }, 0.1);
                b.Advance(0.1);
            }
            Assert.That(Health(enemy), Is.EqualTo(before).Within(1e-6), "the crew stood aside: no shot without the trigger");
        }

        [Test]
        public void FromOneGunTheSeatMovesUpToTheStationAndBackDownToAGun()
        {
            var (b, _, station, _) = StationFight(DVec3.UnitX);
            var gun = Embodiables.TurretsOn(b, station).First();
            var session = new EmbodimentSession();
            session.Switch(gun);
            Assert.That(session.MoveToHost(), Is.True);
            var seat = (StationSeat)session.Current;
            Assert.That(seat.Embodied && !gun.Embodied, Is.True);
            Assert.That(session.Switch(gun), Is.True);
            Assert.That(seat.Embodied, Is.False, "switching hands the station back first");
            Assert.That(gun.Embodied, Is.True);
            Assert.That(b.Manned.Keys, Is.EquivalentTo(new[] { gun.Mount }));
        }

        [Test]
        public void AStationShotAwayIsDroppedAndItsCrewHasItsGunsBack()
        {
            var (b, _, station, _) = StationFight(DVec3.UnitX);
            var seat = Embodiables.StationOf(null, b, station);
            var session = new EmbodimentSession();
            session.Switch(seat);
            station.Dead = true;
            station.Combat.Hull = 0;
            Assert.That(seat.Blocker, Is.EqualTo("destroyed"));
            Assert.That(session.Step(new EmbodimentInput { Primary = true }, 0.1), Is.False);
            Assert.That(session.EndedBecause, Is.EqualTo("destroyed"));
            Assert.That(station.Combat.StationHeld, Is.False);
            Assert.That(b.Manned, Is.Empty);
        }

        [Test]
        public void ASecondTriggerLaunchesTheCraftInItsHangarsOnce()
        {
            var (b, _, station, _) = StationFight(DVec3.UnitX, new ShipCombat(CombatSpec.Of(ShipClass.Carrier), ShipClass.Carrier));
            var seat = Embodiables.StationOf(null, b, station);
            int aboard = station.Combat.Fighters;
            Assert.That(aboard, Is.GreaterThan(0));
            var session = new EmbodimentSession();
            session.Switch(seat);
            int units = b.Units.Count;
            session.Step(new EmbodimentInput { Secondary = true }, 0.1);
            int launched = b.Units.Count - units;
            Assert.That(launched, Is.GreaterThan(0), "its craft went out");
            Assert.That(station.Combat.Fighters, Is.EqualTo(aboard - launched));
            Assert.That(b.Units.Skip(units).All(u => u.Kind == UnitKind.Fighter && u.Side == station.Side), Is.True);
            Assert.That(seat.LaunchCraft(), Is.EqualTo(0), "nothing left aboard fit to fly");
        }

        [Test]
        public void AStationHasItsOwnPlaceOutsideAFightAndTurnsItsGunsThere()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            var stations = Embodiables.StationsIn(w);
            Assert.That(stations, Is.Not.Empty, "the default world has defended settlements");
            var seat = stations.First(s => s.Settlement != null && w.War.BattleAt(s.Settlement.Body) == null);
            Assert.That(seat.Unit(out _), Is.Null, "no fight round it");
            Assert.That(seat.CanEmbody, Is.True, seat.Blocker);
            Assert.That(Embodiables.StationsAt(w, seat.Settlement.Body), Does.Contain(seat));

            var pose = seat.PoseAt(StartJd);
            Assert.That(pose.FrameBody, Is.EqualTo(seat.Settlement.Body));
            Assert.That(pose.OffsetKm.Length, Is.EqualTo(Battle.AnchorDistanceKm(w.Catalog[seat.Settlement.Body])).Within(1e-6));
            Assert.That(seat.PoseAt(StartJd + 10).OffsetKm.Equals(pose.OffsetKm), Is.True, "it holds station: it does not move");

            var session = new EmbodimentSession();
            Assert.That(session.Switch(seat), Is.True);
            Assert.That(seat.Embodied, Is.True);
            var laser = seat.Combat.Subsystems.First(s => s.Kind == SubsystemKind.HeavyLaser);
            var goal = PilotFlight.RotateAbout(pose.Forward, pose.Up, 60 * Astro.Deg2Rad);
            session.Step(new EmbodimentInput { Aim = goal }, 0.1);
            double first = AngleDeg(laser.Aim, goal);
            for (int i = 0; i < 100; i++) session.Step(new EmbodimentInput { Aim = goal }, 0.1);
            Assert.That(AngleDeg(laser.Aim, goal), Is.LessThan(first), "its guns turn toward the aim with no fight on");
            Assert.That(seat.LaunchCraft(), Is.EqualTo(0), "nothing to launch into with no fight on");

            session.Release();
            Assert.That(seat.Embodied, Is.False);
        }
    }
}
