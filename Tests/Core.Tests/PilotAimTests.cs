using System;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    [TestFixture]
    public class PilotAimTests
    {
        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        static readonly DVec3 Nose = DVec3.UnitX, Roof = DVec3.UnitZ;

        /// <summary>Starboard for a hull nosed along +X with its roof on +Z.</summary>
        static readonly DVec3 Starboard = -DVec3.UnitY;

        [Test]
        public void EachKindOfHullIsFlownItsOwnWay()
        {
            Assert.That(PilotTuning.ControlOf(ShipClass.Fighter), Is.EqualTo(PilotControl.FreeAim));
            Assert.That(PilotTuning.ControlOf(ShipClass.Courier), Is.EqualTo(PilotControl.FreeAim));
            Assert.That(PilotTuning.ControlOf(ShipClass.Gunship), Is.EqualTo(PilotControl.DirectNose));
            Assert.That(PilotTuning.ControlOf(ShipClass.Freighter), Is.EqualTo(PilotControl.Hauler));
            Assert.That(PilotTuning.ControlOf(ShipClass.Miner), Is.EqualTo(PilotControl.Hauler));
            Assert.That(PilotTuning.ControlOf(ShipClass.Explorer), Is.EqualTo(PilotControl.Hauler));
            foreach (var c in new[] { ShipClass.Carrier, ShipClass.Battleship, ShipClass.Cruiser, ShipClass.Destroyer })
                Assert.That(PilotTuning.ControlOf(c), Is.EqualTo(PilotControl.Helm), c.ToString());
        }

        [Test]
        public void FreeAimRunsFarAheadOfTheNoseButNeverBehindIt()
        {
            var aim = Nose;
            var up = Roof;
            PilotAim.Look(ref aim, ref up, 60 * Astro.Deg2Rad, 0, Nose, Roof, 0);
            Assert.That(AngleDeg(aim, Nose), Is.EqualTo(60).Within(1e-9), "a free-aim crosshair leads the nose as far as it is swung");
            Assert.That(DVec3.Dot(aim, Starboard) > 0, Is.True, "mouse right swings the crosshair to starboard");

            PilotAim.Look(ref aim, ref up, 90 * Astro.Deg2Rad, 0, Nose, Roof, 0);
            Assert.That(AngleDeg(aim, Nose), Is.EqualTo(PilotAim.FreeAimMaxLeadDeg).Within(1e-9));
        }

        [Test]
        public void FreeAimStaysScreenRelativeWhileLookingUp()
        {
            var aim = Nose;
            var up = Roof;
            PilotAim.Look(ref aim, ref up, 0, 70 * Astro.Deg2Rad, Nose, Roof, 0);
            Assert.That(AngleDeg(aim, Roof), Is.EqualTo(20).Within(1e-9), "mouse up pitches the crosshair up");
            Assert.That(Math.Abs(DVec3.Dot(up, aim)), Is.LessThan(1e-9), "the view's roof stays square to the line of sight");

            // Looking up, mouse right still turns the view to its own right, not about the hull's roof.
            var right = DVec3.Cross(aim, up).Normalized;
            var before = aim;
            PilotAim.Look(ref aim, ref up, 5 * Astro.Deg2Rad, 0, Nose, Roof, 0);
            Assert.That(DVec3.Dot(aim - before, right) > 0, Is.True);
            Assert.That(Math.Abs(DVec3.Dot(aim - before, up)), Is.LessThan(1e-3));
        }

        [Test]
        public void FreeAimViewRollsWithTheHull()
        {
            var aim = Nose;
            var up = Roof;
            var rolledRoof = PilotFlight.RotateAbout(Roof, Nose, 30 * Astro.Deg2Rad);
            for (int i = 0; i < 300; i++) PilotAim.Look(ref aim, ref up, 0, 0, Nose, rolledRoof, 1 / 60.0);
            Assert.That(AngleDeg(up, rolledRoof), Is.LessThan(0.01));
        }

        [Test]
        public void NoseSteeringLeadsOnlyAsFarAsItsLimit()
        {
            double lead = 8 * Astro.Deg2Rad;
            var aim = PilotAim.Nose(Nose, Nose, Roof, 3 * Astro.Deg2Rad, 0, lead);
            Assert.That(AngleDeg(aim, Nose), Is.EqualTo(3).Within(1e-9));
            Assert.That(DVec3.Dot(aim, Starboard) > 0, Is.True);
            aim = PilotAim.Nose(aim, Nose, Roof, 40 * Astro.Deg2Rad, 0, lead);
            Assert.That(AngleDeg(aim, Nose), Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void TheHelmTurnsWhileHeldAndHoldsTheNoseWhenLetGo()
        {
            double lead = 4 * Astro.Deg2Rad;
            Assert.That(PilotAim.Helm(Nose, Roof, 0, 0, lead).Equals(Nose), Is.True, "no helm input holds the heading");

            var starboard = PilotAim.Helm(Nose, Roof, 1, 0, lead);
            Assert.That(AngleDeg(starboard, Nose), Is.EqualTo(4).Within(1e-9));
            Assert.That(DVec3.Dot(starboard, Starboard) > 0, Is.True);

            var up = PilotAim.Helm(Nose, Roof, 0, 1, lead);
            Assert.That(DVec3.Dot(up, Roof) > 0, Is.True);

            var half = PilotAim.Helm(Nose, Roof, 0.5, 0, lead);
            Assert.That(AngleDeg(half, Nose), Is.EqualTo(2).Within(1e-9), "a half deflection asks for half the lead");
        }

        [Test]
        public void ACapitalShipAtTheHelmTurnsAtItsFullRateAndStopsWhenReleased()
        {
            var c = ShipClass.Battleship;
            var hull = HullDynamics.Of(CombatSpec.Of(c));
            var tuning = PilotTuning.Of(c);
            var combat = new ShipCombat(CombatSpec.Of(c), c);
            var s = PilotState.At(DVec3.Zero, DVec3.Zero, Nose);
            double lead = PilotFlight.AimLeadRad(hull, tuning);
            const double dt = 1 / 60.0;
            for (int i = 0; i < 60 * 10; i++)
                PilotFlight.Step(ref s, combat, hull, tuning, new PilotInput { DesiredForward = PilotAim.Helm(s.Forward, s.Up, 1, 0, lead) }, DVec3.Zero, dt);
            Assert.That(s.TurnRateDegS, Is.EqualTo(tuning.TurnRateDegS).Within(0.05 * tuning.TurnRateDegS), "held, the helm turns at the hull's full rate");

            for (int i = 0; i < 60 * 10; i++)
                PilotFlight.Step(ref s, combat, hull, tuning, new PilotInput { DesiredForward = PilotAim.Helm(s.Forward, s.Up, 0, 0, lead) }, DVec3.Zero, dt);
            Assert.That(s.TurnRateDegS, Is.LessThan(1e-6), "let go, the swing stops");
        }
    }
}
