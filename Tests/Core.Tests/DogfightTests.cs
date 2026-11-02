using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Light fighters flown by their crews in a dogfight (U.SPC-T274): on the player's own airframe, they lay their guns
    /// on the lead point, extend through the merge and come round, break hard with a hostile on their tail, and go for
    /// the player first.
    /// </summary>
    [TestFixture]
    public class DogfightTests
    {
        const double StartJd = 2461324.0;

        static Ship Hull(ShipClass c, Faction f, int id) => new Ship
        {
            Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c),
        };

        static Battle NewBattle(int seed = 1) => new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, seed);

        static readonly Faction Blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
        static readonly Faction Red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };

        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        [Test]
        public void AnEnemyFighterRunsDownAPlayerFlyingStraightAndHitsIt()
        {
            int hitIn = 0;
            for (int seed = 1; seed <= 4; seed++)
            {
                var b = NewBattle(seed);
                var me = b.AddShip(Hull(ShipClass.Fighter, Blue, 0), 0, DVec3.Zero, DVec3.UnitX);
                var them = b.AddShip(Hull(ShipClass.Fighter, Red, 1), 1, new DVec3(-3, 2, 0), DVec3.UnitY);
                me.PlayerControlled = true;
                var vel = DVec3.UnitX * 0.25;
                double before = me.Combat.Hull + me.Combat.Subsystems.Sum(s => s.Hp + s.Shield);
                for (int i = 0; i < 600 && !b.Over; i++)
                {
                    me.Vel = vel;
                    me.Pos += vel * 0.1;
                    me.Forward = DVec3.UnitX;
                    b.Advance(0.1);
                }
                if (me.Combat.Hull + me.Combat.Subsystems.Sum(s => s.Hp + s.Shield) < before) hitIn++;
            }
            Assert.That(hitIn, Is.GreaterThanOrEqualTo(3), "an enemy fighter could not get its guns onto a player flying straight and level");
        }

        [Test]
        public void AFighterWithAHostileOnItsTailBreaksHard()
        {
            var b = NewBattle();
            var ai = b.AddShip(Hull(ShipClass.Fighter, Red, 1), 1, DVec3.Zero, DVec3.UnitX);
            var chaser = b.AddShip(Hull(ShipClass.Fighter, Blue, 0), 0, new DVec3(-1, 0, 0), DVec3.UnitX);
            chaser.PlayerControlled = true;
            ai.Vel = DVec3.UnitX * 0.3;
            var start = ai.Forward;
            for (int i = 0; i < 10; i++)
            {
                chaser.Pos = ai.Pos - ai.Forward * 1.0;
                chaser.Forward = ai.Forward;
                chaser.Vel = ai.Vel;
                b.Advance(0.1);
            }
            Assert.That(ai.Maneuver, Is.EqualTo(DogfightManeuver.Break), "a fighter with a gun on its tail did not break");
            Assert.That(AngleDeg(ai.Forward, start), Is.GreaterThan(20), "the break was no hard turn");
        }

        [Test]
        public void ACrewFliesItsClasssOwnFlightModelBoostAndAll()
        {
            // U.SPC-T440: the crew's break goes through the light fighter's model, so it draws on the hull's own boost
            // charge and never runs past the boost speed the player's would reach.
            var b = NewBattle();
            var ai = b.AddShip(Hull(ShipClass.Fighter, Red, 1), 1, DVec3.Zero, DVec3.UnitX);
            var chaser = b.AddShip(Hull(ShipClass.Fighter, Blue, 0), 0, new DVec3(-1, 0, 0), DVec3.UnitX);
            chaser.PlayerControlled = true;
            ai.Vel = DVec3.UnitX * 0.3;
            var tune = PilotTuning.Of(ShipClass.Fighter);
            double fastest = 0;
            for (int i = 0; i < 10; i++)
            {
                chaser.Pos = ai.Pos - ai.Forward * 1.0;
                chaser.Forward = ai.Forward;
                chaser.Vel = ai.Vel;
                b.Advance(0.1);
                fastest = Math.Max(fastest, ai.Vel.Length);
            }
            Assert.That(ai.Maneuver, Is.EqualTo(DogfightManeuver.Break));
            Assert.That(ai.Combat.BoostFraction, Is.LessThan(1), "the break under boost drew nothing from the hull's charge");
            Assert.That(fastest, Is.LessThanOrEqualTo(tune.MaxSpeed * tune.BoostSpeedMultiplier * 1.001), "faster than its boost could take it");
            Assert.That(FlightModelRegistry.Default.For(ShipClass.Fighter), Is.InstanceOf<LightFighterFlightModel>());
        }

        [Test]
        public void AFighterThroughTheMergeExtendsAndComesBackRound()
        {
            var b = NewBattle();
            var ai = b.AddShip(Hull(ShipClass.Fighter, Red, 1), 1, DVec3.Zero, DVec3.UnitX);
            var target = b.AddShip(Hull(ShipClass.Cruiser, Blue, 0), 0, new DVec3(0.2, 0, 0), -DVec3.UnitX);
            ai.Target = target;
            ai.Vel = DVec3.UnitX * 0.3;
            bool extended = false;
            double furthest = 0;
            for (int i = 0; i < 400; i++)
            {
                target.Pos = new DVec3(0.2, 0, 0);
                target.Vel = DVec3.Zero;
                target.Stunned = 1;
                b.Advance(0.1);
                extended |= ai.Maneuver == DogfightManeuver.Extend;
                furthest = Math.Max(furthest, (ai.Pos - target.Pos).Length);
            }
            Assert.That(extended, Is.True, "inside the merge the fighter never extended");
            Assert.That(furthest, Is.GreaterThan(Battle.MergeKm * 2), "the fighter never opened the range to come round");
            Assert.That((ai.Pos - target.Pos).Length, Is.LessThan(Battle.ExtendKm * 2.5), "the fighter extended and never came back");
        }

        [Test]
        public void EnemyFightersGoForThePlayerFirst()
        {
            var b = NewBattle();
            var player = b.AddShip(Hull(ShipClass.Fighter, Blue, 0), 0, new DVec3(4, 0, 0), DVec3.UnitX);
            var wingman = b.AddShip(Hull(ShipClass.Fighter, Blue, 1), 0, new DVec3(1, 0, 0), DVec3.UnitX);
            var ai = b.AddShip(Hull(ShipClass.Fighter, Red, 2), 1, DVec3.Zero, DVec3.UnitX);
            player.PlayerControlled = true;
            ai.Target = wingman;
            b.Advance(0.1);
            Assert.That(ai.Target, Is.SameAs(player));
        }

        [Test]
        public void ADogfightBetweenCrewsComesToAnEnd()
        {
            var b = NewBattle(3);
            for (int i = 0; i < 3; i++) b.AddShip(Hull(ShipClass.Fighter, Blue, i), 0, new DVec3(4, i, 0), -DVec3.UnitX);
            for (int i = 0; i < 3; i++) b.AddShip(Hull(ShipClass.Courier, Red, 10 + i), 1, new DVec3(-4, i, 0), DVec3.UnitX);
            for (int i = 0; i < 20000 && !b.Over; i++) b.Advance(1);
            Assert.That(b.Over, Is.True, "six fighters dogfighting never settled it");
            Assert.That(b.Units.All(u => u.Pos.Length < 1e4 && !double.IsNaN(u.Pos.X)), Is.True);
        }
    }
}
