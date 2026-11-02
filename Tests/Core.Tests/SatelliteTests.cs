using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Satellites as embodiables (U.SPC-T448): a constellation satellite can be taken out of its slot, turned, pointed and
    /// pushed about on its own state, and handed back its AI flies it home to its slot and rejoins the cloud.
    /// </summary>
    [TestFixture]
    public class SatelliteTests
    {
        const double StartJd = 2461324.0;

        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        static (Constellation c, BodyDefinition earth) Starlink()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var infra = new Infrastructure(eph.Catalog);
            infra.AddSolSpacecraft();
            return (infra.Constellations.First(c => c.Name == "Starlink"), eph.Catalog["Earth"]);
        }

        /// <summary>Steps the constellation from <paramref name="jd"/> for <paramref name="seconds"/> with the seat held on <paramref name="input"/>.</summary>
        static double Fly(Constellation c, BodyDefinition earth, SatelliteSeat seat, EmbodimentInput input, double jd, double seconds, double dt = 0.5)
        {
            for (double t = 0; t < seconds; t += dt)
            {
                if (seat.Embodied) seat.Control(input, dt);
                jd += dt / Astro.SecondsPerDay;
                c.Step(earth, jd);
            }
            return jd;
        }

        [Test]
        public void SlotsMatchTheCloudAndMoveAtOrbitalSpeed()
        {
            var (c, earth) = Starlink();
            var points = new DVec3[c.Count];
            c.Offsets(earth, StartJd, points);
            foreach (int i in new[] { 0, 1, 500, c.Count - 1 })
            {
                Assert.That(c.SlotAt(earth, i, StartJd, out var at, out var v), Is.True);
                Assert.That((at - points[i]).Length, Is.LessThan(1e-6));
                double circular = Math.Sqrt(earth.Gm / at.Length);
                Assert.That(v.Length, Is.EqualTo(circular).Within(circular * 1e-6));
                Assert.That(Math.Abs(DVec3.Dot(v.Normalized, at.Normalized)), Is.LessThan(1e-9));
                // The velocity really is the slot's rate of change.
                c.SlotAt(earth, i, StartJd + 1.0 / Astro.SecondsPerDay, out var next, out _);
                Assert.That((next - at - v).Length, Is.LessThan(0.01));
            }
            Assert.That(c.SlotAt(earth, c.Count, StartJd, out _, out _), Is.False);
        }

        [Test]
        public void ATakenSatelliteTurnsAndThrustsOnlyAsThePlayerSays()
        {
            var (c, earth) = Starlink();
            double jd = StartJd;
            var seat = new SatelliteSeat(c, earth, 42, () => jd);
            var session = new EmbodimentSession();
            Assert.That(seat.CanEmbody, Is.True);
            var before = seat.PoseAt(jd);
            Assert.That(session.Switch(seat), Is.True);
            Assert.That(seat.Embodied, Is.True);
            Assert.That(seat.Kind, Is.EqualTo(EmbodimentKind.Satellite));
            // Taken where it was, pointing where it was.
            var taken = seat.PoseAt(jd);
            Assert.That((taken.OffsetKm - before.OffsetKm).Length, Is.LessThan(1e-6));
            Assert.That(AngleDeg(taken.Forward, before.Forward), Is.LessThan(1e-6));

            // Swing the boresight 90° off the nadir: it turns at its slew rate, not at once.
            var aim = taken.Up;
            jd = Fly(c, earth, seat, new EmbodimentInput { Aim = aim }, jd, 1);
            double after1 = AngleDeg(seat.PoseAt(jd).Forward, aim);
            Assert.That(after1, Is.InRange(90 - Satellite.SlewDegS - 2, 90 - Satellite.SlewDegS + 2));
            jd = Fly(c, earth, seat, new EmbodimentInput { Aim = aim }, jd, 6);
            Assert.That(AngleDeg(seat.PoseAt(jd).Forward, aim), Is.LessThan(0.5));

            // Coasting, it stays in its slot (the same orbit); thrusting, it leaves it.
            Assert.That(seat.Satellite.SlotErrorKm, Is.LessThan(0.05));
            jd = Fly(c, earth, seat, new EmbodimentInput { Throttle = 1 }, jd, 60);
            Assert.That(seat.Satellite.SlotErrorKm, Is.GreaterThan(1));
            Assert.That(seat.Satellite.LastThrustKmS2, Is.EqualTo(Satellite.ThrustKmS2).Within(1e-12));

            // Where the cloud draws it is where it is.
            var points = new DVec3[c.Count];
            c.Offsets(earth, jd, points);
            Assert.That((points[42] - seat.PoseAt(jd).OffsetKm).Length, Is.LessThan(1e-9));
        }

        [Test]
        public void HandedBackItsAIHoldsStation()
        {
            var (c, earth) = Starlink();
            double jd = StartJd;
            var seat = new SatelliteSeat(c, earth, 7, () => jd);
            var session = new EmbodimentSession();
            session.Switch(seat);
            // Pushed well off station and pointed away from the ground.
            jd = Fly(c, earth, seat, new EmbodimentInput { Throttle = 1, StrafeRight = 0.5, Aim = seat.PoseAt(jd).OffsetKm.Normalized }, jd, 120);
            double off = seat.Satellite.SlotErrorKm;
            Assert.That(off, Is.GreaterThan(5));
            var left = seat.PoseAt(jd);

            session.Release();
            Assert.That(seat.Embodied, Is.False);
            Assert.That(seat.Satellite, Is.Not.Null, "its AI flies it home from where it was left");
            // Nothing snaps: the AI's first moment starts from the pose the player left.
            jd = Fly(c, earth, seat, default, jd, 0.5);
            Assert.That((seat.PoseAt(jd).OffsetKm - left.OffsetKm).Length, Is.LessThan(10));

            double slotJd = jd;
            for (int i = 0; i < 7200 && seat.Satellite != null; i++)
                slotJd = Fly(c, earth, seat, default, slotJd, 1, 1);
            Assert.That(seat.Satellite, Is.Null, "back on station, it rejoins the cloud");
            Assert.That(c.Flying.Count, Is.EqualTo(0));
            var pose = seat.PoseAt(slotJd);
            c.SlotAt(earth, 7, slotJd, out var slot, out var v);
            Assert.That((pose.OffsetKm - slot).Length, Is.LessThan(1e-6));
            Assert.That(AngleDeg(pose.Forward, -slot), Is.LessThan(1e-6), "boresight back on the ground below");
        }

        [Test]
        public void ItsBeamCoversWhatItPointsAtAndNotThroughTheBody()
        {
            var (c, earth) = Starlink();
            var sat = c.Detach(earth, 3, StartJd);
            var below = sat.OffsetKm.Normalized * earth.RadiusKm;
            Assert.That(sat.Covers(below), Is.True, "the ground straight below");
            Assert.That(sat.Covers(-below), Is.False, "the far side of the world");
            Assert.That(sat.Covers(sat.OffsetKm * 3), Is.False, "straight overhead, behind it");
            sat.Forward = sat.OffsetKm.Normalized;
            Assert.That(sat.Covers(sat.OffsetKm * 3), Is.True, "turned round, overhead is in its beam");
        }

        [Test]
        public void ASatelliteOutOfRangeCannotBeTaken()
        {
            var (c, earth) = Starlink();
            var seat = new SatelliteSeat(c, earth, c.Count, () => StartJd);
            Assert.That(seat.CanEmbody, Is.False);
            Assert.That(seat.Blocker, Is.EqualTo("nothing there"));
            Assert.That(new EmbodimentSession().Switch(seat), Is.False);
        }
    }
}
