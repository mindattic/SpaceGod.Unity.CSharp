using System;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The hauler flight model (U.SPC-T443, docs/FLIGHT_MODELS.md, Haulers): freighters and miners fly heavy. Laden mass
    /// slows the push, the braking and the turn; the full-retro stop point is where the hull really comes to rest; Ctrl in a
    /// port's approach is docking mode; the strafe is weak and the boost a short emergency burn; assist off is Newtonian.
    /// </summary>
    [TestFixture]
    public class HaulerFlightTests
    {
        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        sealed class Rig
        {
            public PilotState S;
            public readonly ShipCombat Combat;
            public readonly HullDynamics Hull;
            public readonly PilotTuning Tuning;
            public readonly IFlightModel Model;

            public Rig(ShipClass c, IFlightModel model, DVec3 vel = default)
            {
                Combat = new ShipCombat(CombatSpec.Of(c), c);
                Hull = HullDynamics.Of(CombatSpec.Of(c));
                Tuning = PilotTuning.Of(c);
                Model = model;
                S = PilotState.At(DVec3.Zero, vel, DVec3.UnitX);
            }

            public void Fly(PilotInput input, double dt, int steps)
            {
                for (int i = 0; i < steps; i++) Model.Step(ref S, Combat, Hull, Tuning, input, DVec3.Zero, dt);
            }
        }

        static Ship Freighter()
        {
            var c = ShipClass.Freighter;
            return new Ship { Id = 1, Name = "Hauler", Spec = ShipClassSpec.Of(c), Combat = new ShipCombat(CombatSpec.Of(c), c), CapacityTonnes = Economy.CapacityOf(c) };
        }

        [Test]
        public void FreightersAndMinersFlyTheHaulerModel()
        {
            var reg = FlightModelRegistry.CreateDefault();
            foreach (var c in new[] { ShipClass.Freighter, ShipClass.Miner })
            {
                Assert.That(reg.FamilyOf(c), Is.EqualTo(FlightFamily.Hauler), c.ToString());
                Assert.That(reg.For(c), Is.InstanceOf<HaulerFlightModel>(), c.ToString());
            }
            Assert.That(PilotTuning.ControlOf(ShipClass.Freighter), Is.EqualTo(PilotControl.Hauler), "the nose is still steered with a slow mouse (U.SPC-T269)");
        }

        [Test]
        public void TheLadenRatioIsTheHoldAndTanksOverTheReadyHull()
        {
            var ship = Freighter();
            Assert.That(HaulerFlightModel.LadenRatio(ship), Is.EqualTo(1).Within(1e-9), "fresh from its yard: tanks full, hold empty");
            ship.HasCargo = true;
            ship.CargoGood = Good.Water;
            ship.CargoTonnes = 20000;
            double ready = ship.DryMassTonnes + ship.PropellantCapacity;
            Assert.That(HaulerFlightModel.LadenRatio(ship), Is.EqualTo((ready + 20000) / ready).Within(1e-9));
            ship.Propellant = 0;
            Assert.That(HaulerFlightModel.LadenRatio(ship), Is.EqualTo((ship.DryMassTonnes + 20000) / ready).Within(1e-9), "burned propellant is mass gone");

            var model = new HaulerFlightModel();
            Assert.That(model.MassFactor(1), Is.EqualTo(1));
            Assert.That(model.MassFactor(0), Is.EqualTo(1), "an unset ratio is a ready hull");
            Assert.That(model.MassFactor(3), Is.EqualTo(1 + HaulerHandling.Default.MassWeight * 2).Within(1e-12));
            Assert.That(model.MassFactor(0.01), Is.EqualTo(HaulerFlightModel.MinMassFactor), "a light hull is held to a floor");
        }

        [Test]
        public void ALadenHaulerAcceleratesBrakesAndTurnsSlowerThanAnEmptyOne()
        {
            var model = new HaulerFlightModel();
            const double full = 35000.0 / 15000.0;

            // Gathering way from rest.
            var empty = new Rig(ShipClass.Freighter, model);
            var laden = new Rig(ShipClass.Freighter, model);
            empty.Fly(new PilotInput { Throttle = 1, MassRatio = 1 }, 0.05, 60);
            laden.Fly(new PilotInput { Throttle = 1, MassRatio = full }, 0.05, 60);
            double k = model.MassFactor(full);
            Assert.That(laden.S.Vel.Length, Is.EqualTo(empty.S.Vel.Length / k).Within(empty.S.Vel.Length * 0.02), "the push falls with the load");

            // Braking from the same speed.
            double v = empty.Tuning.MaxSpeed;
            empty = new Rig(ShipClass.Freighter, model, DVec3.UnitX * v);
            laden = new Rig(ShipClass.Freighter, model, DVec3.UnitX * v);
            empty.Fly(new PilotInput { MassRatio = 1 }, 0.05, 100);
            laden.Fly(new PilotInput { MassRatio = full }, 0.05, 100);
            Assert.That(laden.S.Vel.Length, Is.GreaterThan(empty.S.Vel.Length + 0.01), "a full hull still has way on");

            // Turning through 90 degrees.
            empty = new Rig(ShipClass.Freighter, model);
            laden = new Rig(ShipClass.Freighter, model);
            empty.Fly(new PilotInput { DesiredForward = DVec3.UnitY, MassRatio = 1 }, 0.02, 30);
            laden.Fly(new PilotInput { DesiredForward = DVec3.UnitY, MassRatio = full }, 0.02, 30);
            Assert.That(AngleDeg(laden.S.Forward, DVec3.UnitX), Is.LessThan(AngleDeg(empty.S.Forward, DVec3.UnitX) * 0.8), "a full hull swings slower");
        }

        [Test]
        public void AReadyHaulerPushesAsItsDesignDoes()
        {
            var model = new HaulerFlightModel();
            var rig = new Rig(ShipClass.Freighter, model);
            rig.Fly(new PilotInput { Throttle = 1, MassRatio = 1 }, 0.05, 20);
            Assert.That(rig.S.Vel.Length, Is.EqualTo(rig.Hull.MainAccel * 1.0).Within(1e-9), "a second at full drive on the design's own push");
        }

        [Test]
        public void TheStopPointIsWhereFullRetroBringsTheHullToRest()
        {
            var model = new HaulerFlightModel();
            foreach (double ratio in new[] { 1.0, 35000.0 / 15000.0 })
            {
                var start = new DVec3(0.15, 0.02, -0.01);
                var rig = new Rig(ShipClass.Freighter, model, start);
                var input = new PilotInput { MassRatio = ratio };
                var stop = model.StopPoint(rig.S, rig.Hull, input);
                Assert.That(stop.Reachable, Is.True);
                double laden = model.Laden(rig.Hull, ratio).RetroAccel;
                Assert.That(stop.Seconds, Is.EqualTo(0.15 / laden).Within(1e-9), "the retros are the slowest to stop the way along the nose");

                // Flight assist with the throttle cut brakes at full retro, and the hull ends up where the HUD said.
                double dt = 0.01;
                int steps = (int)Math.Ceiling(stop.Seconds / dt) + 200;
                rig.Fly(input, dt, steps);
                Assert.That(rig.S.Vel.Length, Is.LessThan(1e-6), "at rest");
                Assert.That((rig.S.Pos - stop.OffsetKm).Length, Is.LessThan(stop.DistanceKm * 0.01 + 0.005), $"stopped {rig.S.Pos.Length:F3} km out against {stop.DistanceKm:F3} km shown");
            }

            var ready = new Rig(ShipClass.Freighter, model, DVec3.UnitX * 0.15);
            double near = model.StopPoint(ready.S, ready.Hull, new PilotInput { MassRatio = 1 }).DistanceKm;
            double far = model.StopPoint(ready.S, ready.Hull, new PilotInput { MassRatio = 2 }).DistanceKm;
            Assert.That(far, Is.GreaterThan(near * 1.5), "a laden hull's stop point is further off");
            var still = PilotState.At(DVec3.Zero, DVec3.Zero, DVec3.UnitX);
            Assert.That(HaulerFlightModel.StopPoint(still, ready.Hull).DistanceKm, Is.EqualTo(0));
        }

        [Test]
        public void CtrlInAPortsApproachIsDockingMode()
        {
            var model = new HaulerFlightModel();
            var h = HaulerHandling.Default;
            var t = PilotTuning.Of(ShipClass.Freighter);

            var docking = new Rig(ShipClass.Freighter, model);
            var outside = new Rig(ShipClass.Freighter, model);
            docking.Fly(new PilotInput { Throttle = 1, Boost = true, Fine = true, DockingApproach = true }, 0.05, 1200);
            outside.Fly(new PilotInput { Throttle = 1, Fine = true }, 0.05, 1200);
            Assert.That(docking.S.Vel.Length, Is.EqualTo(t.MaxSpeed * h.DockingSpeedFraction).Within(1e-6), "the throttle floored asks only docking speed, and boost does not light");
            Assert.That(outside.S.Vel.Length, Is.EqualTo(t.MaxSpeed).Within(1e-6), "Ctrl away from a port is not docking mode");
            Assert.That(docking.Combat.BoostFraction, Is.EqualTo(1), "no boost in docking mode");

            var dockIn = new PilotInput { Fine = true, DockingApproach = true };
            Assert.That(HaulerFlightModel.Docking(dockIn), Is.True);
            Assert.That(model.HelmTuning(t, dockIn).TurnRateDegS, Is.EqualTo(t.TurnRateDegS * h.DockingTurnFraction).Within(1e-9));
            Assert.That(HaulerFlightModel.Docking(new PilotInput { DockingApproach = true }), Is.False, "the approach alone is not docking mode");
            Assert.That(HaulerFlightModel.Docking(new PilotInput { Fine = true, DockingApproach = true, FlightAssistOff = true }), Is.False);

            var gentle = new Rig(ShipClass.Freighter, model);
            var free = new Rig(ShipClass.Freighter, model);
            gentle.Fly(new PilotInput { DesiredForward = DVec3.UnitY, Fine = true, DockingApproach = true }, 0.02, 30);
            free.Fly(new PilotInput { DesiredForward = DVec3.UnitY, DockingApproach = true }, 0.02, 30);
            Assert.That(AngleDeg(gentle.S.Forward, DVec3.UnitX), Is.LessThan(AngleDeg(free.S.Forward, DVec3.UnitX) * 0.6), "the turn is gentled for placement");
        }

        [Test]
        public void TheStrafeIsWeakAndTheBoostAShortEmergencyBurn()
        {
            var model = new HaulerFlightModel();
            var h = HaulerHandling.Default;
            var rig = new Rig(ShipClass.Freighter, model);
            rig.Fly(new PilotInput { StrafeRight = 1 }, 0.05, 1200);
            Assert.That(rig.S.Vel.Length, Is.EqualTo(rig.Tuning.MaxSpeed * h.StrafeSpeedFraction).Within(1e-6));
            Assert.That(h.StrafeSpeedFraction, Is.LessThan(PilotFlight.StrafeSpeedFraction), "weaker than the shared model's");

            var burn = new Rig(ShipClass.Freighter, model);
            double lasts = burn.Tuning.BoostDurationS * h.BoostDurationFraction;
            burn.Fly(new PilotInput { Throttle = 1, Boost = true }, 0.05, (int)Math.Round(lasts / 0.05) + 2);
            Assert.That(burn.Combat.BoostFraction, Is.EqualTo(0), $"the charge is spent in {lasts:F0} s, not {burn.Tuning.BoostDurationS:F0} s");
        }

        [Test]
        public void AssistOffIsTheNewtonianHullOnItsLadenMass()
        {
            var input = new PilotInput { DesiredForward = new DVec3(0.3, 1, 0.2).Normalized, Throttle = 0.7, StrafeUp = -0.3, Roll = 0.4, FlightAssistOff = true, MassRatio = 1 };
            var hauler = new Rig(ShipClass.Freighter, new HaulerFlightModel(), new DVec3(0.05, 0, 0));
            var newton = new Rig(ShipClass.Freighter, NewtonianFlightModel.Instance, new DVec3(0.05, 0, 0));
            hauler.Fly(input, 0.05, 100);
            newton.Fly(input, 0.05, 100);
            Assert.That((hauler.S.Vel - newton.S.Vel).Length, Is.EqualTo(0));
            Assert.That((hauler.S.Pos - newton.S.Pos).Length, Is.EqualTo(0));
            Assert.That((hauler.S.Forward - newton.S.Forward).Length, Is.EqualTo(0));

            input.MassRatio = 2;
            var heavy = new Rig(ShipClass.Freighter, new HaulerFlightModel(), new DVec3(0.05, 0, 0));
            heavy.Fly(input, 0.05, 100);
            Assert.That((heavy.S.Vel - new DVec3(0.05, 0, 0)).Length, Is.LessThan((hauler.S.Vel - new DVec3(0.05, 0, 0)).Length), "the load is physics, assist or not");
        }
    }
}
