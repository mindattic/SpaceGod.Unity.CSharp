using System;
using System.Collections.Generic;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Take charge of anything, any time (U.SPC-T422): any seat in a fight (a ship's helm, a launched fighter's cockpit, a gun
    /// mount) taken mid-battle and handed back to its AI with nothing paused, and the galaxy playing out on its own when
    /// the player takes nothing.
    /// </summary>
    [TestFixture]
    public class TakeChargeTests
    {
        const double StartJd = 2461324.0;

        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        static double Health(BattleUnit u) => u.Combat.Hull + u.Combat.Subsystems.Sum(s => s.Hp + s.Shield);

        static Ship Hull(ShipClass c, Faction f, int id) => new Ship
        {
            Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c),
        };

        /// <summary>
        /// A carrier and its squadrons against a battleship 28 km off. With <paramref name="duel"/> every gun but the
        /// fighters' is silenced and every craft but the lead of <paramref name="squadron"/> is put back aboard, so what
        /// the battleship takes is that one craft's doing.
        /// </summary>
        static (Battle b, BattleUnit carrier, BattleUnit enemy, BattleUnit craft) CarrierFight(bool duel, int squadron = 0, int seed = 5)
        {
            var blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, seed) { Launch = FighterAI.Launch, FighterStep = FighterAI.Step };
            var carrier = b.AddShip(Hull(ShipClass.Carrier, blue, 0), 0, new DVec3(14, 0, 0), -DVec3.UnitX);
            var enemy = b.AddShip(Hull(ShipClass.Battleship, red, 1), 1, new DVec3(-14, 0, 0), DVec3.UnitX);
            var craft = b.Units.First(u => u.Kind == UnitKind.Fighter && u.Fighter.Squadron == squadron && u.Fighter.Lead == null);
            if (duel)
            {
                foreach (var u in new[] { carrier, enemy })
                foreach (var s in u.Combat.Subsystems)
                    if (s.Def.IsWeapon) s.Hp = 0;
                foreach (var u in b.Units)
                    if (u.Kind == UnitKind.Fighter && u != craft) u.Docked = true;
            }
            return (b, carrier, enemy, craft);
        }

        /// <summary>Flies the taken craft for <paramref name="seconds"/>, its stick on <paramref name="aimAt"/> each step.</summary>
        static void Fly(Battle b, EmbodimentSession session, Func<DVec3> aimAt, double seconds, bool guns = false, bool torpedo = false, double throttle = 1)
        {
            for (double t = 0; t < seconds && !b.Over; t += 0.1)
            {
                session.Step(new EmbodimentInput { Aim = aimAt(), Throttle = throttle, Primary = guns, Secondary = torpedo }, 0.1);
                b.Advance(0.1);
            }
        }

        // ------------------------------------------------------------ a fighter's cockpit

        [Test]
        public void EveryLaunchedCraftIsASeatBesideTheShipsHelms()
        {
            var (w, ship) = WorldWarship();
            var (b, _, _, _) = CarrierFight(duel: false);
            b.AddShip(ship, 0, new DVec3(14, 3, 0), -DVec3.UnitX);
            var seats = Embodiables.SeatsIn(w, b);
            var aloft = b.Units.Where(u => u.Kind == UnitKind.Fighter && u.Present).ToList();
            Assert.That(aloft.Count, Is.GreaterThanOrEqualTo(18));
            Assert.That(seats.OfType<FighterSeat>().Select(s => s.Unit), Is.EquivalentTo(aloft), "every craft aloft");
            Assert.That(seats.OfType<ShipHelm>().Select(h => h.Ship), Does.Contain(ship));
            Assert.That(seats.OfType<FighterSeat>().All(s => s.Kind == EmbodimentKind.Fighter && s.CanEmbody), Is.True);
            Assert.That(seats.OfType<FighterSeat>().Count(s => s.Name.Contains("bomber")), Is.EqualTo(aloft.Count(u => u.Fighter.Bomber)));
        }

        /// <summary>The picker's list (U.SPC-T446): the hull in hand and its guns first, then the fight, then nearby helms,
        /// each once, all from Embodiables; picking from it hands the seat held back to its AI.</summary>
        [Test]
        public void ThePickerListsTheHullInHandAndItsGunsThenTheFightThenNearbyHelmsEachOnce()
        {
            var (w, ship) = WorldWarship();
            var (b, carrier, _, craft) = CarrierFight(duel: false);
            b.AddShip(ship, 0, new DVec3(14, 3, 0), -DVec3.UnitX);
            var idle = w.Fleet.Ships.First(s => s != ship && !s.Destroyed && !s.Derelict && s.Battle == null);
            var list = Embodiables.Pickable(w, b, onShip: ship, nearby: new[] { idle, ship });
            var guns = Embodiables.TurretsOn(w, ship);
            Assert.That(guns, Is.Not.Empty);
            Assert.That(list[0], Is.EqualTo(Embodiables.HelmOf(w, ship)), "the hull in hand's own helm first");
            Assert.That(list.Skip(1).Take(guns.Count), Is.EqualTo(guns), "then its gun mounts");
            Assert.That(list, Does.Contain(Embodiables.FighterOf(b, craft)), "then every seat in the fight");
            Assert.That(list.Last(), Is.EqualTo(Embodiables.HelmOf(w, idle)), "then the helms near the camera");
            Assert.That(list.Distinct().Count(), Is.EqualTo(list.Count), "each seat once");
            Assert.That(list.All(e => e.CanEmbody), Is.True);

            var session = new EmbodimentSession();
            var gun = guns[0];
            Assert.That(session.Switch(gun), Is.True);
            var fighter = list.OfType<FighterSeat>().First();
            Assert.That(session.Switch(fighter), Is.True);
            Assert.That(gun.Embodied, Is.False, "the seat left goes back to its AI");
            Assert.That(fighter.Embodied, Is.True);
            session.Release();
        }

        [Test]
        public void ATakenFighterTurnsAtItsOwnRateTowardTheStickAndHoldsTheThrottle()
        {
            var (b, _, _, craft) = CarrierFight(duel: true);
            var seat = Embodiables.FighterOf(b, craft);
            var session = new EmbodimentSession();
            Assert.That(session.Switch(seat), Is.True);
            Assert.That(seat.Embodied && craft.PlayerControlled, Is.True);
            var start = craft.Forward;
            var goal = PilotFlight.RotateAbout(start, DVec3.UnitZ, 90 * Astro.Deg2Rad);
            session.Step(new EmbodimentInput { Aim = goal }, 0.1);
            // A light fighter turns at its own rate for the speed it is flying (U.SPC-T17, U.SPC-T424): its full rate at the
            // corner speed, a little less above it.
            var model = (LightFighterFlightModel)FighterCraft.Interceptor.Model;
            double rate = model.TurnRateAt(craft.Vel.Length, FighterCraft.Interceptor.Tuning());
            Assert.That(rate, Is.LessThanOrEqualTo(FighterAI.Interceptor.TurnRateDegS).And.GreaterThan(FighterAI.Interceptor.TurnRateDegS * 0.6));
            b.Advance(0.5);
            double turned = AngleDeg(craft.Forward, start);
            Assert.That(turned, Is.EqualTo(rate * 0.5).Within(1), "half a second at its own turn rate, not a snap");

            Fly(b, session, () => goal, 10);
            Assert.That(AngleDeg(craft.Forward, goal), Is.LessThan(1e-6), "the nose is where the stick is");
            Assert.That(craft.Vel.Length, Is.EqualTo(FighterAI.Interceptor.MaxSpeed).Within(1e-6), "full throttle held");
            Assert.That(AngleDeg(craft.Vel, goal), Is.LessThan(1), "it carves after its nose");
            var pose = seat.PoseAt(StartJd);
            Assert.That(pose.FrameBody, Is.EqualTo(b.Body));
            Assert.That((pose.OffsetKm - (craft.Pos + b.Anchor)).Length, Is.LessThan(1e-9));
        }

        [Test]
        public void TheFightersGunsHitOnlyWhenThePlayerAimsAndFires()
        {
            var (b, _, enemy, craft) = CarrierFight(duel: true);
            var session = new EmbodimentSession();
            session.Switch(Embodiables.FighterOf(b, craft));
            double before = Health(enemy);
            Fly(b, session, () => (enemy.Pos - craft.Pos).Normalized, 40, guns: false, throttle: 0);
            Assert.That(Health(enemy), Is.EqualTo(before).Within(1e-6), "its pilot no longer fires on its own");

            Fly(b, session, () => (enemy.Pos - craft.Pos).Normalized, 90, guns: true);
            Assert.That(b.Over, Is.False);
            Assert.That(Health(enemy), Is.LessThan(before), "the run down the crosshair onto the battleship hits it");
        }

        [Test]
        public void ABomberAtTheHelmLoosesItsTorpedoesAtTheHullOnItsNose()
        {
            var (b, _, enemy, craft) = CarrierFight(duel: true, squadron: 2);
            Assert.That(craft.Fighter.Bomber, Is.True);
            var tube = craft.Combat.Subsystems.First(s => s.Kind == SubsystemKind.Torpedo);
            int ammo = tube.Ammo;
            var session = new EmbodimentSession();
            session.Switch(Embodiables.FighterOf(b, craft));
            Fly(b, session, () => (enemy.Pos - craft.Pos).Normalized, 15, torpedo: false);
            Assert.That(tube.Ammo, Is.EqualTo(ammo), "no torpedo without the trigger");
            bool loosed = false;
            for (int i = 0; i < 1200 && !loosed && !b.Over; i++)
            {
                Fly(b, session, () => (enemy.Pos - craft.Pos).Normalized, 0.1, torpedo: true);
                loosed = tube.Ammo < ammo;
            }
            Assert.That(loosed, Is.True, "in range with the battleship on the nose, the trigger looses the torpedoes");
            Assert.That(b.Missiles.Any(m => m.Torpedo && m.Target == enemy), Is.True);
        }

        [Test]
        public void HandedBackTheFightersPilotFliesARunOfItsOwnFromWhereItWasLeft()
        {
            var (b, _, enemy, craft) = CarrierFight(duel: false);
            var session = new EmbodimentSession();
            var seat = Embodiables.FighterOf(b, craft);
            session.Switch(seat);
            Fly(b, session, () => DVec3.UnitZ, 20);
            var left = craft.Pos;
            session.Release();
            Assert.That(b.Flown, Is.Empty);
            Assert.That(craft.PlayerControlled || craft.PlayerFireHeld, Is.False);
            Assert.That(craft.Fighter.Phase, Is.EqualTo(FighterPhase.Approach));
            b.Advance(0.1);
            Assert.That((craft.Pos - left).Length, Is.LessThan(0.1), "it carries on from where it was left, no snap back");
            Assert.That(craft.Target, Is.Not.Null, "its pilot has picked a target of its own");
            for (int i = 0; i < 300 && !b.Over; i++) b.Advance(0.5);
            Assert.That(AngleDeg(craft.Forward, DVec3.UnitZ), Is.GreaterThan(10), "flown by its pilot again, not held on the player's heading");
        }

        [Test]
        public void AFighterShotDownEndsTheSessionAndSaysWhy()
        {
            var (b, _, _, craft) = CarrierFight(duel: true);
            var session = new EmbodimentSession();
            session.Switch(Embodiables.FighterOf(b, craft));
            craft.Dead = true;
            Assert.That(session.Step(new EmbodimentInput { Primary = true }, 0.1), Is.False);
            Assert.That(session.Active, Is.False);
            Assert.That(session.EndedBecause, Is.EqualTo("shot down"));
            Assert.That(b.Flown, Is.Empty);
        }

        [Test]
        public void AFighterThePlayerFliesDoesNotHoldTheFightOpen()
        {
            var (b, _, enemy, craft) = CarrierFight(duel: false);
            var session = new EmbodimentSession();
            var seat = Embodiables.FighterOf(b, craft);
            session.Switch(seat);
            foreach (var u in b.Units)
                if (u.Side == 1) u.Dead = true;
            Fly(b, session, () => DVec3.UnitZ, 600);
            Assert.That(b.Over, Is.True, "the other fighters land and the fight ends, the player's craft still aloft");
            Assert.That(seat.CanEmbody, Is.False);
            Assert.That(seat.Blocker, Is.EqualTo("the fight is over"));
            Assert.That(session.Step(default, 0.1), Is.False);
        }

        // ------------------------------------------------------------ any seat, one after another

        static (World w, Ship ship) WorldWarship()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            var ship = w.Fleet.Ships.First(s => !s.Destroyed && !s.IsEscort && !s.Derelict && s.Phase == ShipPhase.Parked && s.Battle == null &&
                                                s.Combat.Subsystems.Any(m => MountedTurret.IsMannable(m.Def)));
            return (w, ship);
        }

        [Test]
        public void ARolledMannedFighterKeepsItsRollOnTheUnitSoItIsDrawnAsFlown()
        {
            var (b, _, _, _) = CarrierFight(duel: false);
            var u = b.Units.First(x => x.Kind == UnitKind.Fighter && x.Present);
            var stick = new FighterControl { Throttle = 0.5, Roll = 1 };
            b.Flown[u] = stick;
            u.PlayerControlled = true;
            for (int i = 0; i < 20; i++) b.Advance(0.1);
            Assert.That(u.Up.LengthSquared, Is.GreaterThan(0.5), "FlyManned wrote the roof");
            Assert.That((u.Up - stick.Up).Length, Is.LessThan(1e-6));
            u.Basis(out _, out var up);
            Assert.That(DVec3.Dot(up, stick.Up), Is.GreaterThan(0.999));
        }

        [Test]
        public void TakingAndHandingBackTheHelmKeepsTheHullsRoll()
        {
            var (w, ship) = WorldWarship();
            var (b, _, _, _) = CarrierFight(duel: false);
            var unit = b.AddShip(ship, 0, new DVec3(14, 3, 0), -DVec3.UnitX);
            unit.Roof = new DVec3(0, 1, 1).Normalized;
            unit.Basis(out _, out var before);
            Assert.That(before.Z, Is.LessThan(0.9), "the roll is real");
            var helm = Embodiables.HelmOf(w, ship);
            helm.TakeCharge();
            unit.Basis(out _, out var taken);
            Assert.That(AngleDeg(taken, before), Is.LessThan(1e-6), "taking the helm does not snap the roll");
            helm.HandBack();
            unit.Basis(out _, out var back);
            Assert.That(AngleDeg(back, taken), Is.LessThan(1e-6), "handing back does not snap it either");
        }

        [Test]
        public void SeatsAreTakenOneAfterAnotherMidBattleEachHandedBackAndTheFightNeverStops()
        {
            var (w, ship) = WorldWarship();
            var (b, _, enemy, _) = CarrierFight(duel: false);
            var unit = b.AddShip(ship, 0, new DVec3(14, 3, 0), -DVec3.UnitX);
            var seats = Embodiables.SeatsIn(w, b);
            var helm = seats.OfType<ShipHelm>().First(h => h.Ship == ship);
            var fighter = seats.OfType<FighterSeat>().First();
            var gun = Embodiables.TurretsOn(w, b, unit).First();
            Assert.That(gun.Host, Is.EqualTo(helm));

            var session = new EmbodimentSession();
            double seconds = b.Seconds;
            foreach (var next in new IEmbodiable[] { helm, fighter, gun, helm, fighter })
            {
                Assert.That(session.Switch(next), Is.True, next.Name);
                b.Advance(1);
                Assert.That(new IEmbodiable[] { helm, fighter, gun }.Count(s => s.Embodied), Is.EqualTo(1), "only the seat taken is held: " + next.Name);
            }
            Assert.That(b.Seconds - seconds, Is.EqualTo(5).Within(1e-6), "the fight ran on through every switch");
            Assert.That(b.Manned, Is.Empty);
            Assert.That(w.PilotedShip, Is.Null);
            Assert.That(fighter.Embodied, Is.True);

            session.Release();
            Assert.That(b.Flown, Is.Empty);
            Assert.That(unit.PlayerControlled, Is.False);
            Assert.That(seats.All(s => !s.Embodied), Is.True, "back to command: every seat is its AI's");
        }

        [Test]
        public void CyclingSeatsWrapsRoundBothWays()
        {
            var (b, _, _, _) = CarrierFight(duel: false);
            var seats = Embodiables.SeatsIn(null, b);
            Assert.That(seats.Count, Is.EqualTo(20), "with no world, three squadrons and the recon pair, only the cockpits (a helm needs the world it is flown in)");
            int last = seats.Count - 1;
            Assert.That(Embodiables.Cycle(seats, null, 1), Is.EqualTo(seats[0]));
            Assert.That(Embodiables.Cycle(seats, null, -1), Is.EqualTo(seats[last]));
            Assert.That(Embodiables.Cycle(seats, seats[last], 1), Is.EqualTo(seats[0]));
            Assert.That(Embodiables.Cycle(seats, Embodiables.FighterOf(b, ((FighterSeat)seats[0]).Unit), -1), Is.EqualTo(seats[last]), "found by what it is, not by which object");
            Assert.That(Embodiables.Cycle(new List<IEmbodiable>(), null, 1), Is.Null);
        }

        // ------------------------------------------------------------ let it play out

        [Test]
        public void TheGalaxyPlaysOutOnItsOwnAndASeatTakenMidBattleIsHandedBackWithoutStoppingIt()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            double jd = StartJd;
            eph.Update(jd);
            var w = World.CreateDefault(eph, jd);
            int ended = 0;
            w.War.BattleEnded += (_, _) => ended++;
            Battle fight = null;
            for (int i = 0; i < 24 * 120 && fight == null; i++)
            {
                jd += 1.0 / 24;
                eph.Update(jd);
                w.Update(jd);
                fight = w.War.Battles.FirstOrDefault(x => !x.Over && Embodiables.SeatsIn(w, x).Count > 0);
            }
            Assert.That(fight, Is.Not.Null, "with nobody at any controls, the war still comes to blows");
            Assert.That(w.PilotedShip, Is.Null);

            var seat = Embodiables.SeatsIn(w, fight).OrderByDescending(s => s.Kind == EmbodimentKind.Fighter).First();
            var session = new EmbodimentSession();
            Assert.That(session.Switch(seat), Is.True, seat.Name);
            for (int i = 0; i < 300 && session.Active; i++)
            {
                session.Step(new EmbodimentInput { Aim = seat.PoseAt(jd).Forward, Primary = true }, 0.1);
                jd += 0.1 / 86400;
                eph.Update(jd);
                w.Update(jd);
            }
            session.Release();
            Assert.That(seat.Embodied, Is.False);

            for (int i = 0; i < 24 * 30 && w.War.Battles.Contains(fight); i++)
            {
                jd += 1.0 / 24;
                eph.Update(jd);
                w.Update(jd);
            }
            Assert.That(w.War.Battles.Contains(fight), Is.False, "handed back, the fight is fought out by its crews");
            Assert.That(ended, Is.GreaterThan(0));
        }
    }
}
