using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>Universal embodiment (U.SPC-T18): the common take-charge/hand-back seam, a ship's helm behind it, and
    /// a single gun mount on a station or hull laid and fired by the player.</summary>
    [TestFixture]
    public class EmbodimentTests
    {
        const double StartJd = 2461324.0;

        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        static double Health(BattleUnit u) => u.Combat.Hull + u.Combat.Subsystems.Sum(s => s.Hp + s.Shield);

        static Ship Hull(ShipClass c, Faction f, int id) => new Ship
        {
            Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c),
        };

        /// <summary>
        /// A settlement's defence platform at the origin facing +X with only its first heavy laser left to fire, and an
        /// enemy battleship 8 km off along <paramref name="enemyDir"/>.
        /// </summary>
        static (Battle b, BattleUnit station, BattleUnit enemy, MountedTurret turret) StationFight(DVec3 enemyDir, int seed = 3)
        {
            var blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, seed);
            var port = new Settlement { Name = "Vesta Highport", Owner = blue };
            var station = b.AddPlatform(port, new ShipCombat(CombatSpec.Platform), 0, DVec3.Zero);
            // A cruiser: a battleship would back off out of the platform's reach (U.SPC-T490).
            var enemy = b.AddShip(Hull(ShipClass.Cruiser, red, 1), 1, enemyDir.Normalized * 8, -enemyDir.Normalized);
            var laser = station.Combat.Subsystems.First(s => s.Kind == SubsystemKind.HeavyLaser);
            foreach (var s in station.Combat.Subsystems)
                if (s.Def.IsWeapon && s != laser) s.Hp = 0;
            var turret = Embodiables.TurretsOn(b, station).Single();
            Assert.That(turret.Mount, Is.SameAs(laser));
            return (b, station, enemy, turret);
        }

        /// <summary>Runs the fight for <paramref name="seconds"/> with the player laying the turret on <paramref name="aimAt"/>
        /// (null: on the enemy) each frame.</summary>
        static void Fight(Battle b, EmbodimentSession session, BattleUnit enemy, double seconds, bool trigger, DVec3? aimDir = null)
        {
            var t = (MountedTurret)session.Current;
            for (double s = 0; s < seconds; s += 0.1)
            {
                var origin = t.Unit.PointOf(t.Mount.Def);
                var input = new EmbodimentInput { Aim = aimDir ?? (enemy.Pos - origin).Normalized, Primary = trigger };
                session.Step(input, 0.1);
                b.Advance(0.1);
            }
        }

        // ------------------------------------------------------------ a mounted turret, end to end

        [Test]
        public void OnlyDirectFireGunMountsCanBeTaken()
        {
            var b = new Battle(0, 3, "Test", DVec3.Zero, StartJd, 1);
            var port = new Settlement { Name = "Vesta Highport", Owner = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) };
            var station = b.AddPlatform(port, new ShipCombat(CombatSpec.Platform), 0, DVec3.Zero);
            var kinds = Embodiables.TurretsOn(b, station).Select(t => t.Mount.Kind).ToList();
            Assert.That(kinds.Count(k => k == SubsystemKind.HeavyLaser), Is.EqualTo(2));
            Assert.That(kinds.Count(k => k == SubsystemKind.PointDefense), Is.EqualTo(4));
            Assert.That(kinds, Has.No.Member(SubsystemKind.MissileLauncher), "a launcher is not laid down a barrel");
            Assert.That(kinds, Has.No.Member(SubsystemKind.ShieldGenerator));
            Assert.That(kinds, Has.No.Member(SubsystemKind.Reactor));
            var launcher = new MountedTurret(b, station, station.Combat.Subsystems.First(s => s.Kind == SubsystemKind.MissileLauncher));
            Assert.That(launcher.CanEmbody, Is.False);
            Assert.That(launcher.Blocker, Is.EqualTo("not a gun mount"));
            Assert.That(launcher.Kind, Is.EqualTo(EmbodimentKind.Turret));
        }

        [Test]
        public void AStationTurretLaidOnAnEnemyAndFiredHitsIt()
        {
            var (b, _, enemy, turret) = StationFight(DVec3.UnitY);
            var session = new EmbodimentSession();
            Assert.That(session.Switch(turret), Is.True);
            Assert.That(turret.Embodied, Is.True);
            double before = Health(enemy);
            Fight(b, session, enemy, 40, trigger: true);
            Assert.That(Health(enemy), Is.LessThan(before), "the barrel was laid on the enemy and the trigger held");
            Assert.That(AngleDeg(turret.Mount.Aim, enemy.Pos - turret.Unit.PointOf(turret.Mount.Def)), Is.LessThan(2), "the barrel is on the enemy");
        }

        [Test]
        public void ATakenTurretHoldsFireUntilTheTriggerAndItsCrewFiresAgainOnceHandedBack()
        {
            var (b, _, enemy, turret) = StationFight(DVec3.UnitX);
            var session = new EmbodimentSession();
            session.Switch(turret);
            double before = Health(enemy);
            Fight(b, session, enemy, 30, trigger: false);
            Assert.That(Health(enemy), Is.EqualTo(before).Within(1e-6), "the crew stood aside: no shot without the trigger");

            session.Release();
            Assert.That(turret.Embodied, Is.False);
            Assert.That(b.Manned, Is.Empty);
            for (int i = 0; i < 900; i++) b.Advance(0.1);
            Assert.That(Health(enemy), Is.LessThan(before), "handed back, the crew picks its own target and fires");
        }

        [Test]
        public void AShotLaidAwayFromTheEnemyMissesIt()
        {
            var (b, _, enemy, turret) = StationFight(DVec3.UnitX);
            var session = new EmbodimentSession();
            session.Switch(turret);
            double before = Health(enemy);
            Fight(b, session, enemy, 40, trigger: true, aimDir: -DVec3.UnitX);
            Assert.That(Health(enemy), Is.EqualTo(before).Within(1e-6), "every shot is aimed: one laid astern hits nothing ahead");
        }

        [Test]
        public void ATurretTraversesAtItsOwnRateNotInstantly()
        {
            var (b, station, enemy, turret) = StationFight(DVec3.UnitX);
            var session = new EmbodimentSession();
            session.Switch(turret);
            var start = turret.PoseAt(StartJd).Forward;
            var goal = PilotFlight.RotateAbout(start, DVec3.UnitZ, 90 * Astro.Deg2Rad);
            session.Step(new EmbodimentInput { Aim = goal }, 1);
            b.Advance(1);
            double rate = MountedTurret.TraverseDegS(SubsystemKind.HeavyLaser);
            Assert.That(AngleDeg(turret.Mount.Aim, start), Is.EqualTo(rate).Within(0.5), "one second of traverse");
            for (int i = 0; i < 10; i++) b.Advance(1);
            Assert.That(AngleDeg(turret.Mount.Aim, goal), Is.LessThan(1e-6), "it gets there");
            var pose = turret.PoseAt(StartJd);
            Assert.That(pose.FrameBody, Is.EqualTo(b.Body));
            Assert.That(AngleDeg(pose.Forward, goal), Is.LessThan(1e-6), "the seat looks down the barrel");
            Assert.That(Math.Abs(DVec3.Dot(pose.Forward, pose.Up)), Is.LessThan(1e-9));
        }

        [Test]
        public void AMountIsNeverLaidOutsideItsArcNorASpinalGunPastItsGimbal()
        {
            var u = new BattleUnit { Forward = DVec3.UnitX, LengthKm = 0.5 };
            var starboard = new SubsystemDef(SubsystemKind.LaserTurret, 0.2, 0, 0, coneAxis: FireCones.Starboard, coneHalfDeg: 45);
            var bounded = MountedTurret.Bound(u, starboard, DVec3.UnitX);
            Assert.That(u.YawDegOf(u.Pos + bounded), Is.EqualTo(45).Within(1e-6), "bow is outside a starboard battery's arc: held at its forward edge");
            var inside = MountedTurret.Bound(u, starboard, -DVec3.UnitY);
            Assert.That(AngleDeg(inside, -DVec3.UnitY), Is.LessThan(1e-9), "straight out to starboard is inside it");

            var spinal = new SubsystemDef(SubsystemKind.Railgun, 0, 0, 0.4);
            Assert.That(AngleDeg(MountedTurret.Bound(u, spinal, DVec3.UnitY), DVec3.UnitX), Is.EqualTo(Gunnery.RailgunGimbalDeg).Within(1e-6));
        }

        [Test]
        public void AShotAwayTurretIsHandedBackAndTheSessionEnds()
        {
            var (b, _, enemy, turret) = StationFight(DVec3.UnitX);
            var session = new EmbodimentSession();
            session.Switch(turret);
            turret.Mount.Hp = 0;
            Assert.That(turret.CanEmbody, Is.False);
            Assert.That(turret.Blocker, Is.EqualTo("shot away"));
            Assert.That(session.Step(new EmbodimentInput { Primary = true }, 0.1), Is.False);
            Assert.That(session.Active, Is.False);
            Assert.That(b.Manned, Is.Empty);
        }

        [Test]
        public void SwitchingSeatsHandsTheOldOneBack()
        {
            var b = new Battle(0, 3, "Test", DVec3.Zero, StartJd, 1);
            var port = new Settlement { Name = "Vesta Highport", Owner = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) };
            var station = b.AddPlatform(port, new ShipCombat(CombatSpec.Platform), 0, DVec3.Zero);
            var mounts = Embodiables.TurretsOn(b, station);
            var session = new EmbodimentSession();
            session.Switch(mounts[0]);
            session.Switch(mounts[1]);
            Assert.That(mounts[0].Embodied, Is.False);
            Assert.That(mounts[1].Embodied, Is.True);
            Assert.That(b.Manned.Keys, Is.EquivalentTo(new[] { mounts[1].Mount }));
            // A platform's guns are mounted on the station itself (U.SPC-T447).
            Assert.That(session.MoveToHost(), Is.True);
            Assert.That(session.Current, Is.EqualTo(Embodiables.StationOf(null, b, station)));
            Assert.That(mounts[1].Embodied, Is.False, "the gun is the station's now, laid with all the rest");
        }

        // ------------------------------------------------------------ a ship's helm behind the same seam

        static (World w, Ship ship) WorldWithParkedWarship()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            var ship = w.Fleet.Ships.First(s => !s.Destroyed && !s.IsEscort && !s.Derelict && s.Phase == ShipPhase.Parked && s.Battle == null &&
                                                s.Combat.Subsystems.Any(m => MountedTurret.IsMannable(m.Def)));
            return (w, ship);
        }

        [Test]
        public void AShipsHelmIsTakenAndHandedBackThroughTheSeam()
        {
            var (w, ship) = WorldWithParkedWarship();
            var helm = Embodiables.HelmOf(w, ship);
            Assert.That(helm.Kind, Is.EqualTo(EmbodimentKind.Ship));
            Assert.That(helm.CanEmbody, Is.True);
            var pose = helm.PoseAt(StartJd);
            Assert.That(pose.FrameBody, Is.EqualTo(ship.Orbit.Body), "parked, it is in its orbit's frame");
            Assert.That((pose.OffsetKm - ship.Orbit.OffsetAt(StartJd)).Length, Is.LessThan(1e-6));

            var session = new EmbodimentSession();
            Assert.That(session.Switch(helm), Is.True);
            Assert.That(w.PilotedShip, Is.SameAs(ship));
            Assert.That(helm.Embodied, Is.True);
            session.Release();
            Assert.That(w.PilotedShip, Is.Null);
        }

        [Test]
        public void OnlyAWreckADerelictOrATakenPrizeIsOutOfReach()
        {
            var (w, ship) = WorldWithParkedWarship();
            Assert.That(ShipHelm.BlockerOf(ship), Is.Null);
            ship.Derelict = true;
            Assert.That(ShipHelm.BlockerOf(ship), Is.EqualTo("derelict"));
            ship.Derelict = false;
            ship.Destroyed = true;
            Assert.That(Embodiables.HelmOf(w, ship).CanEmbody, Is.False);
            ship.Destroyed = false;
            var b = new Battle(0, 3, "Test", DVec3.Zero, StartJd, 1);
            b.AddShip(ship, 0, DVec3.Zero, DVec3.UnitX).Captured = true;
            Assert.That(ShipHelm.BlockerOf(ship), Is.EqualTo("taken as a prize"));
        }

        [Test]
        public void FromAShipsTurretTheSeatMovesUpToItsHelmAndTheCrewHasTheGunBack()
        {
            var (w, ship) = WorldWithParkedWarship();
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", DVec3.Zero, StartJd, 1);
            var unit = b.AddShip(ship, 0, DVec3.Zero, DVec3.UnitX);
            b.AddShip(Hull(ShipClass.Destroyer, red, 999), 1, new DVec3(10, 0, 0), -DVec3.UnitX);

            var turret = Embodiables.TurretsOn(w, ship).First();
            Assert.That(turret.Host, Is.EqualTo(Embodiables.HelmOf(w, ship)));
            var session = new EmbodimentSession();
            session.Switch(turret);
            Assert.That(unit.PlayerControlled, Is.False, "manning one gun leaves the hull to its crew");
            Assert.That(w.PilotedShip, Is.Null);

            Assert.That(session.MoveToHost(), Is.True);
            Assert.That(b.Manned, Is.Empty, "the gun goes back to its crew");
            Assert.That(w.PilotedShip, Is.SameAs(ship));
            Assert.That(unit.PlayerControlled, Is.True, "the fighting hull's own battle unit is the player's");

            session.Step(new EmbodimentInput { Aim = DVec3.UnitY, Primary = true }, 0.1);
            Assert.That(unit.PlayerFireHeld, Is.True);
            Assert.That(unit.PlayerAim.Equals(DVec3.UnitY), Is.True);

            session.Release();
            Assert.That(unit.PlayerControlled, Is.False, "handed back, its crew fights it");
            Assert.That(unit.PlayerFireHeld, Is.False);
            Assert.That(w.PilotedShip, Is.Null);
        }
    }
}
