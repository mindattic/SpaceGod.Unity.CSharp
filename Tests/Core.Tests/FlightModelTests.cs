using System;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Per-class flight models (U.SPC-T17, docs/FLIGHT_MODELS.md): every class flies as one family, every
    /// family has a model, new models plug in without touching the others, the families without a model of
    /// their own still fly the Newtonian one exactly as before, and the light fighter flies by its own rules.
    /// </summary>
    [TestFixture]
    public class FlightModelTests
    {
        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        sealed class Rig
        {
            public PilotState S;
            public readonly ShipCombat Combat;
            public readonly HullDynamics Hull;
            public readonly PilotTuning Tuning;
            readonly IFlightModel model;

            public Rig(ShipClass c, IFlightModel model, DVec3 vel = default)
            {
                Combat = new ShipCombat(CombatSpec.Of(c), c);
                Hull = HullDynamics.Of(CombatSpec.Of(c));
                Tuning = PilotTuning.Of(c);
                this.model = model;
                S = PilotState.At(DVec3.Zero, vel, DVec3.UnitX);
            }

            public void Fly(PilotInput input, double dt, int steps)
            {
                for (int i = 0; i < steps; i++) model.Step(ref S, Combat, Hull, Tuning, input, DVec3.Zero, dt);
            }
        }

        // ------------------------------------------------------------ the extension point

        [Test]
        public void EveryClassFliesAsAFamilyAndTheFighterLineupSplitsLightAndHeavy()
        {
            var reg = FlightModelRegistry.CreateDefault();
            foreach (ShipClass c in Enum.GetValues(typeof(ShipClass)))
                Assert.That(reg.For(c), Is.Not.Null, c.ToString());
            Assert.That(reg.FamilyOf(ShipClass.Fighter), Is.EqualTo(FlightFamily.LightFighter));
            Assert.That(reg.FamilyOf(ShipClass.Courier), Is.EqualTo(FlightFamily.Runner));
            Assert.That(reg.FamilyOf(ShipClass.Gunship), Is.EqualTo(FlightFamily.HeavyFighter));
            Assert.That(reg.FamilyOf(ShipClass.Freighter), Is.EqualTo(FlightFamily.Hauler));
            Assert.That(reg.FamilyOf(ShipClass.Miner), Is.EqualTo(FlightFamily.Hauler));
            foreach (var c in new[] { ShipClass.Carrier, ShipClass.Battleship, ShipClass.Cruiser, ShipClass.Destroyer })
                Assert.That(reg.FamilyOf(c), Is.EqualTo(FlightFamily.Capital), c.ToString());

            Assert.That(reg.For(ShipClass.Fighter), Is.InstanceOf<LightFighterFlightModel>());
            Assert.That(reg.For(ShipClass.Gunship), Is.InstanceOf<HeavyFighterFlightModel>(), "heavy fighters fly their own model (U.SPC-T441)");
            Assert.That(reg.For(ShipClass.Freighter), Is.SameAs(NewtonianFlightModel.Instance));
            Assert.That(reg.For(ShipClass.Carrier), Is.InstanceOf<CapitalFlightModel>(), "capitals fly their own model (U.SPC-T499)");
        }

        sealed class StubModel : IFlightModel
        {
            public int Steps;
            public string Name => "Stub";
            public void Step(ref PilotState s, ShipCombat combat, in HullDynamics hull, in PilotTuning tuning, in PilotInput input, DVec3 gravityKmS2, double dt) => Steps++;
        }

        [Test]
        public void ANewModelPlugsInForItsFamilyAloneAndANewClassJoinsAFamily()
        {
            var reg = FlightModelRegistry.CreateDefault();
            var stub = new StubModel();
            reg.Register(FlightFamily.Hauler, stub);
            Assert.That(reg.For(ShipClass.Freighter), Is.SameAs(stub));
            Assert.That(reg.For(ShipClass.Gunship), Is.InstanceOf<HeavyFighterFlightModel>(), "another family was touched");
            Assert.That(reg.For(ShipClass.Fighter), Is.InstanceOf<LightFighterFlightModel>(), "another family was touched");
            Assert.That(reg.For(ShipClass.Battleship), Is.InstanceOf<CapitalFlightModel>(), "another family was touched");

            reg.Assign(ShipClass.Courier, FlightFamily.Hauler);
            Assert.That(reg.For(ShipClass.Courier), Is.SameAs(stub));
            Assert.That(FlightModelRegistry.Default.For(ShipClass.Freighter), Is.Not.SameAs(stub), "a test's registry leaked into the game's");

            // A class may fly a model of its own within its family, and leaves it behind when it changes family.
            var own = new StubModel();
            reg.Register(ShipClass.Miner, own);
            Assert.That(reg.For(ShipClass.Miner), Is.SameAs(own));
            Assert.That(reg.For(ShipClass.Freighter), Is.SameAs(stub), "a class's own model is its alone");
            reg.Assign(ShipClass.Miner, FlightFamily.Bomber);
            Assert.That(reg.For(ShipClass.Miner), Is.InstanceOf<BomberFlightModel>());
        }

        [Test]
        public void FamiliesWithoutTheirOwnModelFlyExactlyAsBefore()
        {
            var reg = FlightModelRegistry.CreateDefault();
            var input = new PilotInput { DesiredForward = new DVec3(0.3, 1, 0.2).Normalized, Throttle = 1, StrafeRight = 0.5, Roll = 0.4, Boost = true };
            foreach (var c in new[] { ShipClass.Freighter, ShipClass.Miner })
            {
                var a = new Rig(c, reg.For(c), new DVec3(0.01, 0.02, 0));
                var b = new Rig(c, NewtonianFlightModel.Instance, new DVec3(0.01, 0.02, 0));
                var hull = b.Hull;
                var tuning = b.Tuning;
                a.Fly(input, 0.05, 200);
                for (int i = 0; i < 200; i++) PilotFlight.Step(ref b.S, b.Combat, hull, tuning, input, DVec3.Zero, 0.05);
                Assert.That((a.S.Pos - b.S.Pos).Length, Is.EqualTo(0), c + " position");
                Assert.That((a.S.Vel - b.S.Vel).Length, Is.EqualTo(0), c + " velocity");
                Assert.That((a.S.Forward - b.S.Forward).Length, Is.EqualTo(0), c + " attitude");
            }
        }

        // ------------------------------------------------------------ the light fighter (Star Conflict)

        [Test]
        public void ALightFightersTrackCarvesAfterItsNoseWhereTheNewtonianHullSlides()
        {
            var tuning = PilotTuning.Of(ShipClass.Fighter);
            var start = DVec3.UnitX * tuning.MaxSpeed;
            var light = new Rig(ShipClass.Fighter, new LightFighterFlightModel(), start);
            var newton = new Rig(ShipClass.Fighter, NewtonianFlightModel.Instance, start);
            // A hard right-angle break at full throttle: the nose comes round in well under a second.
            var input = new PilotInput { DesiredForward = -DVec3.UnitY, Throttle = 1 };
            light.Fly(input, 0.02, 50);
            newton.Fly(input, 0.02, 50);
            Assert.That(AngleDeg(light.S.Forward, -DVec3.UnitY), Is.LessThan(1), "the nose should be round");
            double lightSlip = AngleDeg(light.S.Vel, light.S.Forward), newtonSlip = AngleDeg(newton.S.Vel, newton.S.Forward);
            Assert.That(lightSlip, Is.LessThan(newtonSlip * 0.6), $"slip {lightSlip:F1} deg vs Newtonian {newtonSlip:F1} deg");
            Assert.That(lightSlip, Is.LessThan(20), "a light fighter carves through a break rather than skidding");
        }

        [Test]
        public void AtItsCornerSpeedALightFightersTrackSticksToTheNoseThroughTheTightestTurn()
        {
            var tuning = PilotTuning.Of(ShipClass.Fighter);
            double corner = tuning.MaxSpeed * LightFighterHandling.Default.CornerSpeedFraction;
            var rig = new Rig(ShipClass.Fighter, new LightFighterFlightModel(), DVec3.UnitX * corner);
            var input = new PilotInput { DesiredForward = -DVec3.UnitY, Throttle = LightFighterHandling.Default.CornerSpeedFraction };
            double worst = 0;
            for (int i = 0; i < 60; i++)
            {
                rig.Fly(input, 0.01, 1);
                worst = Math.Max(worst, AngleDeg(rig.S.Vel, rig.S.Forward));
            }
            Assert.That(AngleDeg(rig.S.Forward, -DVec3.UnitY), Is.LessThan(1), "the nose should be round");
            Assert.That(worst, Is.LessThan(3), "the track should hold to the nose at the corner speed");
            Assert.That(rig.S.Vel.Length, Is.EqualTo(corner).Within(corner * 0.02), "and keep its speed through the turn");
        }

        [Test]
        public void ALightFighterStrafesHarderThanTheSharedModel()
        {
            var light = new Rig(ShipClass.Fighter, new LightFighterFlightModel());
            light.Fly(new PilotInput { DesiredForward = DVec3.UnitX, StrafeRight = 1 }, 0.05, 400);
            double expected = light.Tuning.MaxSpeed * LightFighterHandling.Default.StrafeSpeedFraction;
            Assert.That(light.S.Vel.Length, Is.EqualTo(expected).Within(expected * 0.01));
            Assert.That(LightFighterHandling.Default.StrafeSpeedFraction, Is.GreaterThan(PilotFlight.StrafeSpeedFraction));
        }

        [Test]
        public void ALightFighterTurnsTightestAtItsCornerSpeedAndWideUnderBoost()
        {
            var model = new LightFighterFlightModel();
            var t = PilotTuning.Of(ShipClass.Fighter);
            var h = LightFighterHandling.Default;
            Assert.That(model.TurnRateAt(0, t), Is.EqualTo(t.TurnRateDegS));
            Assert.That(model.TurnRateAt(t.MaxSpeed * h.CornerSpeedFraction, t), Is.EqualTo(t.TurnRateDegS));
            double atMax = model.TurnRateAt(t.MaxSpeed, t), atBoost = model.TurnRateAt(t.MaxSpeed * t.BoostSpeedMultiplier, t);
            Assert.That(atMax, Is.LessThan(t.TurnRateDegS).And.GreaterThan(atBoost));
            Assert.That(atBoost, Is.EqualTo(t.TurnRateDegS * h.OverspeedTurnFactor).Within(1e-9));
            Assert.That(model.TurnRateAt(t.MaxSpeed * 10, t), Is.EqualTo(atBoost).Within(1e-9), "the taper bottoms out at full boost");

            // In flight: the same break from rest and from a boosted run, and the boosted nose lags.
            var slow = new Rig(ShipClass.Fighter, model);
            var fast = new Rig(ShipClass.Fighter, model, DVec3.UnitX * t.MaxSpeed * t.BoostSpeedMultiplier);
            var input = new PilotInput { DesiredForward = -DVec3.UnitY };
            slow.Fly(input, 0.02, 15);
            fast.Fly(input, 0.02, 15);
            Assert.That(AngleDeg(fast.S.Forward, DVec3.UnitX), Is.LessThan(AngleDeg(slow.S.Forward, DVec3.UnitX)));
        }

        [Test]
        public void ALightFighterWithAssistOffIsTheNewtonianHull()
        {
            var input = new PilotInput { DesiredForward = -DVec3.UnitY, Throttle = 0.7, StrafeUp = -0.3, FlightAssistOff = true };
            var light = new Rig(ShipClass.Fighter, new LightFighterFlightModel(), new DVec3(0.2, 0, 0));
            var newton = new Rig(ShipClass.Fighter, NewtonianFlightModel.Instance, new DVec3(0.2, 0, 0));
            light.Fly(input, 0.05, 100);
            newton.Fly(input, 0.05, 100);
            Assert.That((light.S.Vel - newton.S.Vel).Length, Is.EqualTo(0));
            Assert.That((light.S.Pos - newton.S.Pos).Length, Is.EqualTo(0));
        }

        // ------------------------------------------------------------ the runner (the Courier, U.SPC-T444)

        [Test]
        public void TheCourierFliesTheRunnersOwnModelWithTheLightFightersFreeAim()
        {
            var reg = FlightModelRegistry.CreateDefault();
            Assert.That(reg.FamilyOf(ShipClass.Courier), Is.EqualTo(FlightFamily.Runner));
            var model = reg.For(ShipClass.Courier) as LightFighterFlightModel;
            Assert.That(model, Is.Not.Null, "a runner flies the light fighter's rules on its own numbers");
            Assert.That(model.Name, Is.EqualTo("Runner"));
            Assert.That(model.Handling.Grip, Is.EqualTo(LightFighterHandling.Runner.Grip));
            Assert.That(model, Is.Not.SameAs(reg.For(ShipClass.Fighter)), "the Fighter keeps the light fighter's model");
            Assert.That(PilotTuning.ControlOf(ShipClass.Courier), Is.EqualTo(PilotControl.FreeAim));

            var runner = LightFighterHandling.Runner;
            var light = LightFighterHandling.Default;
            Assert.That(runner.Grip, Is.LessThan(light.Grip), "low grip");
            Assert.That(runner.CornerSpeedFraction, Is.LessThan(light.CornerSpeedFraction), "an early corner speed");
            Assert.That(runner.OverspeedTurnFactor, Is.LessThan(light.OverspeedTurnFactor));
            Assert.That(runner.StrafeSpeedFraction, Is.LessThan(light.StrafeSpeedFraction), "a runner is no dodger");
        }

        [Test]
        public void TheCourierHasTheHighestTopSpeedTheLongestBoostAndTheFastestCruiseSpool()
        {
            var courier = PilotTuning.Of(ShipClass.Courier);
            foreach (ShipClass c in Enum.GetValues(typeof(ShipClass)))
            {
                if (c == ShipClass.Courier) continue;
                var t = PilotTuning.Of(c);
                Assert.That(courier.MaxSpeed, Is.GreaterThan(t.MaxSpeed), c + " top speed");
                Assert.That(courier.MaxSpeed * courier.BoostSpeedMultiplier, Is.GreaterThan(t.MaxSpeed * t.BoostSpeedMultiplier), c + " boost speed");
                Assert.That(courier.BoostDurationS, Is.GreaterThan(t.BoostDurationS), c + " boost duration");
                Assert.That(courier.CruiseRampS, Is.LessThan(t.CruiseRampS), c + " cruise spool");
            }

            // A wide turning circle: at top speed it needs more room to come round than any strike craft.
            double CircleKm(PilotTuning t) => t.MaxSpeed / (t.TurnRateDegS * Astro.Deg2Rad);
            foreach (var c in new[] { ShipClass.Fighter, ShipClass.Interceptor, ShipClass.Recon, ShipClass.Gunship, ShipClass.Bomber, ShipClass.AssaultShuttle })
                Assert.That(CircleKm(courier), Is.GreaterThan(CircleKm(PilotTuning.Of(c))), c.ToString());
        }

        [Test]
        public void ARunnerSwingsWideInABreakWhereALightFighterCarves()
        {
            // The same airframe and the same break, flown by each family's rules: only the handling differs.
            var tuning = PilotTuning.Of(ShipClass.Fighter);
            var start = DVec3.UnitX * tuning.MaxSpeed;
            var light = new Rig(ShipClass.Fighter, new LightFighterFlightModel(), start);
            var runner = new Rig(ShipClass.Fighter, new LightFighterFlightModel(LightFighterHandling.Runner, "Runner"), start);
            var input = new PilotInput { DesiredForward = -DVec3.UnitY, Throttle = 1 };
            double lightReach = 0, runnerReach = 0;
            for (int i = 0; i < 100; i++)
            {
                light.Fly(input, 0.01, 1);
                runner.Fly(input, 0.01, 1);
                lightReach = Math.Max(lightReach, light.S.Pos.X);
                runnerReach = Math.Max(runnerReach, runner.S.Pos.X);
            }
            Assert.That(AngleDeg(runner.S.Vel, runner.S.Forward), Is.GreaterThan(AngleDeg(light.S.Vel, light.S.Forward)), "the runner's track lags its nose");
            Assert.That(runnerReach, Is.GreaterThan(lightReach * 1.2), $"overshoot {runnerReach:F3} km vs a light fighter's {lightReach:F3} km");

            // Past its early corner speed the runner's nose stiffens sooner.
            var runnerModel = new LightFighterFlightModel(LightFighterHandling.Runner);
            var lightModel = new LightFighterFlightModel();
            Assert.That(runnerModel.TurnRateAt(tuning.MaxSpeed, tuning), Is.LessThan(lightModel.TurnRateAt(tuning.MaxSpeed, tuning)));
            Assert.That(runnerModel.GripAccel(tuning), Is.LessThan(lightModel.GripAccel(tuning) * 0.5));
        }
    }
}
