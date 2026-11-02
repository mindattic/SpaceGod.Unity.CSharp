using System;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The heavy fighter's flight model, Everspace 2 style (U.SPC-T441, docs/FLIGHT_MODELS.md, Heavy fighters): stiff
    /// speed-independent turning capped below a light fighter's with a crisp spin-up, a short eased settle on the throttle,
    /// strafe and vertical at about 0.45 of forward, and boost speed bled off over a couple of seconds once it is let go.
    /// </summary>
    [TestFixture]
    public class HeavyFighterFlightTests
    {
        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        sealed class Rig
        {
            public PilotState S;
            public readonly ShipCombat Combat;
            public readonly HullDynamics Hull;
            public readonly PilotTuning Tuning;
            public readonly IFlightModel Model;

            public Rig(IFlightModel model, DVec3 vel = default, ShipClass c = ShipClass.Gunship)
            {
                Combat = new ShipCombat(CombatSpec.Of(c), c);
                Hull = HullDynamics.Of(CombatSpec.Of(c));
                Tuning = PilotTuning.Of(c);
                Model = model;
                S = PilotState.At(DVec3.Zero, vel, DVec3.UnitX);
            }

            public void Fly(PilotInput input, double dt, int steps, DVec3 gravity = default)
            {
                for (int i = 0; i < steps; i++) Model.Step(ref S, Combat, Hull, Tuning, input, gravity, dt);
            }
        }

        static HeavyFighterFlightModel Model => new HeavyFighterFlightModel();

        [Test]
        public void TheGunshipAndTheGatePicketFlyTheHeavyFightersOwnModel()
        {
            var reg = FlightModelRegistry.CreateDefault();
            Assert.That(reg.ModelOf(FlightFamily.HeavyFighter), Is.InstanceOf<HeavyFighterFlightModel>());
            Assert.That(reg.For(ShipClass.Gunship), Is.SameAs(reg.ModelOf(FlightFamily.HeavyFighter)));
            Assert.That(reg.For(ShipClass.GatePicket), Is.SameAs(reg.ModelOf(FlightFamily.HeavyFighter)), "a class in the family flies its model");
            Assert.That(PilotTuning.ControlOf(ShipClass.Gunship), Is.EqualTo(PilotControl.DirectNose), "the nose is the crosshair");

            var h = HeavyFighterHandling.Default;
            Assert.That(h.TurnRateCapFraction, Is.EqualTo(0.7));
            Assert.That(h.StrafeSpeedFraction, Is.EqualTo(0.45));
            Assert.That(h.SettleS, Is.InRange(0.4, 0.6));
            Assert.That(h.BoostBleedS, Is.InRange(1.5, 3));
        }

        [Test]
        public void TheNoseTurnsAtOneRateAtAnySpeedCappedBelowALightFighters()
        {
            var model = Model;
            var t = PilotTuning.Of(ShipClass.Gunship);
            double cap = HeavyFighterHandling.Default.TurnRateCapFraction * PilotTuning.Of(ShipClass.Fighter).TurnRateDegS;
            Assert.That(model.TurnRateDegS(t), Is.LessThanOrEqualTo(cap + 1e-9));
            foreach (var light in new[] { ShipClass.Fighter, ShipClass.Interceptor })
                Assert.That(model.TurnRateDegS(t), Is.LessThan(PilotTuning.Of(light).TurnRateDegS), light.ToString());
            // A faster-tuned class would still be held to the cap.
            var quick = new PilotTuning(0.4, 400, 2, 20, 9, 2.2);
            Assert.That(model.TurnRateDegS(quick), Is.EqualTo(cap).Within(1e-9));

            // The same break from rest and from a boosted run comes round equally fast: no corner speed.
            var slow = new Rig(model);
            var fast = new Rig(model, DVec3.UnitX * t.MaxSpeed * t.BoostSpeedMultiplier);
            var input = new PilotInput { DesiredForward = -DVec3.UnitY };
            slow.Fly(input, 0.01, 30);
            fast.Fly(input, 0.01, 30);
            Assert.That(AngleDeg(fast.S.Forward, DVec3.UnitX), Is.EqualTo(AngleDeg(slow.S.Forward, DVec3.UnitX)).Within(1e-6));
            Assert.That(slow.S.TurnRateDegS, Is.EqualTo(model.TurnRateDegS(t)).Within(0.5), "mid-turn the nose swings at its top rate");
        }

        [Test]
        public void TheNoseSpinsUpAndStopsCrisply()
        {
            var model = Model;
            var rig = new Rig(model);
            double top = model.TurnRateDegS(rig.Tuning);
            Assert.That(model.TurnAlpha(rig.Hull, rig.Tuning), Is.GreaterThanOrEqualTo(rig.Hull.PitchYawAngularAccel));

            // From still, the nose is at its top swing within the spin-up time.
            var input = new PilotInput { DesiredForward = -DVec3.UnitY };
            int steps = (int)Math.Ceiling(HeavyFighterHandling.Default.SpinUpS / 0.005) + 1;
            rig.Fly(input, 0.005, steps);
            Assert.That(rig.S.TurnRateDegS, Is.EqualTo(top).Within(top * 0.02));

            // And it comes to rest on the heading without overshooting it.
            double worstPast = 0;
            for (int i = 0; i < 300; i++)
            {
                rig.Fly(input, 0.005, 1);
                if (DVec3.Dot(rig.S.Forward, DVec3.UnitX) < 0) worstPast = Math.Max(worstPast, AngleDeg(rig.S.Forward, -DVec3.UnitY));
            }
            Assert.That(AngleDeg(rig.S.Forward, -DVec3.UnitY), Is.LessThan(0.1));
            Assert.That(rig.S.TurnRateDegS, Is.LessThan(0.5), "stopped on the heading");
            Assert.That(worstPast, Is.LessThan(1), "no overshoot past the heading");

            // The aim leads the nose only as far as this model's own swing brakes in.
            double rate = top * Astro.Deg2Rad;
            Assert.That(model.AimLeadRad(rig.Hull, rig.Tuning), Is.LessThanOrEqualTo(PilotFlight.AimLeadRad(rig.Hull, rig.Tuning) + 1e-12));
            Assert.That(model.AimLeadRad(rig.Hull, rig.Tuning),
                Is.EqualTo(Math.Max(PilotFlight.MinAimLeadDeg * Astro.Deg2Rad, rate * rate / (2 * model.TurnAlpha(rig.Hull, rig.Tuning)))).Within(1e-12));
        }

        [Test]
        public void TheThrottleSettlesThroughAShortEase()
        {
            var model = Model;
            var rig = new Rig(model);
            double top = rig.Tuning.MaxSpeed, settle = HeavyFighterHandling.Default.SettleS;
            var input = new PilotInput { DesiredForward = DVec3.UnitX, Throttle = 1 };

            rig.Fly(input, 0.01, (int)Math.Round(settle * 0.3 / 0.01));
            Assert.That(rig.S.Vel.X, Is.LessThan(top * 0.8), "the hull has heft: it does not jump to speed");
            Assert.That(rig.S.Vel.X, Is.GreaterThan(top * 0.4), "but it is not slow");
            rig.Fly(input, 0.01, (int)Math.Round(settle * 0.7 / 0.01));
            Assert.That(rig.S.Vel.X, Is.EqualTo(top).Within(top * 0.05), "settled on the set point");
            rig.Fly(input, 0.01, 100);
            Assert.That(rig.S.Vel.X, Is.EqualTo(top).Within(top * 1e-3));
            Assert.That(rig.S.Vel.X, Is.LessThanOrEqualTo(top + 1e-12), "the ease does not overshoot");

            // Back to a stop, in the same time.
            var stop = new PilotInput { DesiredForward = DVec3.UnitX };
            rig.Fly(stop, 0.01, (int)Math.Round(settle / 0.01));
            Assert.That(rig.S.Vel.Length, Is.LessThan(top * 0.05));
        }

        [Test]
        public void StrafeAndVerticalReachAboutHalfOfForwardAsFastAsForward()
        {
            var model = Model;
            double settle = HeavyFighterHandling.Default.SettleS;
            var right = new Rig(model);
            var up = new Rig(model);
            right.Fly(new PilotInput { DesiredForward = DVec3.UnitX, StrafeRight = 1 }, 0.01, (int)Math.Round(settle / 0.01));
            up.Fly(new PilotInput { DesiredForward = DVec3.UnitX, StrafeUp = 1 }, 0.01, (int)Math.Round(settle / 0.01));
            double expected = right.Tuning.MaxSpeed * HeavyFighterHandling.Default.StrafeSpeedFraction;
            Assert.That(DVec3.Dot(right.S.Vel, right.S.Right), Is.EqualTo(expected).Within(expected * 0.05), "strafe settles as fast as forward");
            Assert.That(DVec3.Dot(up.S.Vel, up.S.Up), Is.EqualTo(expected).Within(expected * 0.05), "and so does vertical");

            right.Fly(new PilotInput { DesiredForward = DVec3.UnitX, StrafeRight = 1 }, 0.01, 200);
            Assert.That(right.S.Vel.Length, Is.EqualTo(expected).Within(expected * 0.01));
            Assert.That(HeavyFighterHandling.Default.StrafeSpeedFraction, Is.GreaterThan(PilotFlight.StrafeSpeedFraction));
        }

        [Test]
        public void ABoostLetGoBleedsOffOverACoupleOfSeconds()
        {
            var model = Model;
            var rig = new Rig(model);
            var t = rig.Tuning;
            double top = t.MaxSpeed, boosted = top * t.BoostSpeedMultiplier, extra = boosted - top;
            rig.Fly(new PilotInput { DesiredForward = DVec3.UnitX, Throttle = 1, Boost = true }, 0.01, 200);
            Assert.That(rig.S.Vel.X, Is.EqualTo(boosted).Within(boosted * 0.01), "boost raises the set point");

            var cruise = new PilotInput { DesiredForward = DVec3.UnitX, Throttle = 1 };
            rig.Fly(cruise, 0.01, 50);
            Assert.That(rig.S.Vel.X, Is.GreaterThan(top + extra * 0.6), "half a second on, most of the boost is still carried");
            rig.Fly(cruise, 0.01, 50);
            Assert.That(rig.S.Vel.X, Is.LessThan(top + extra * 0.7).And.GreaterThan(top + extra * 0.3), "a second on, it is bleeding off");
            rig.Fly(cruise, 0.01, 110);
            Assert.That(rig.S.Vel.X, Is.EqualTo(top).Within(top * 0.01), "a couple of seconds on, it is back to top speed");

            // The Newtonian hull's assist would brake only on its retros; the bleed is quicker than that, and slower than the ease.
            Assert.That(model.BleedAccel(rig.Hull, t), Is.GreaterThanOrEqualTo(rig.Hull.RetroAccel));
            Assert.That(model.BleedAccel(rig.Hull, t), Is.EqualTo(extra / HeavyFighterHandling.Default.BoostBleedS).Within(1e-12));
        }

        [Test]
        public void AssistHoldsTheHullAgainstGravity()
        {
            var rig = new Rig(Model);
            var g = new DVec3(0, 0, -0.01);
            rig.Fly(new PilotInput { DesiredForward = DVec3.UnitX }, 0.02, 250, g);
            Assert.That(rig.S.Vel.Length, Is.LessThan(1e-6));
        }

        [Test]
        public void WithAssistOffAHeavyFighterIsTheNewtonianHull()
        {
            var input = new PilotInput { DesiredForward = -DVec3.UnitY, Throttle = 0.7, StrafeUp = -0.3, Boost = true, FlightAssistOff = true };
            var heavy = new Rig(Model, new DVec3(0.2, 0, 0));
            var newton = new Rig(NewtonianFlightModel.Instance, new DVec3(0.2, 0, 0));
            heavy.Fly(input, 0.05, 100);
            newton.Fly(input, 0.05, 100);
            Assert.That((heavy.S.Vel - newton.S.Vel).Length, Is.EqualTo(0));
            Assert.That((heavy.S.Pos - newton.S.Pos).Length, Is.EqualTo(0));
            Assert.That((heavy.S.Forward - newton.S.Forward).Length, Is.EqualTo(0));
        }
    }
}
