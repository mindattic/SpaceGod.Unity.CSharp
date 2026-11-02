using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The pipeline-neutral half of URP's Volume stack (U.SPC-T451): the physical camera's exposure against sunny 16, the
    /// automatic and histogram meters, how far and how fast an adapting exposure moves, and the defaults that leave the
    /// image as drawn.
    /// </summary>
    [TestFixture]
    public class CameraExposureTests
    {
        [Test]
        public void SunnySixteenIsTheImageAsDrawn()
        {
            Assert.That(CameraExposure.Ev100(16, 1.0 / 125, 100), Is.EqualTo(CameraExposure.ReferenceEv100).Within(0.05));
            Assert.That(CameraExposure.PhysicalStops(16, 1.0 / 125, 100), Is.EqualTo(0).Within(0.05));
            // Each stop of the camera is a stop of the image: ISO doubled, shutter doubled, aperture a full stop wider.
            double baseline = CameraExposure.PhysicalStops(16, 1.0 / 125, 100);
            Assert.That(CameraExposure.PhysicalStops(16, 1.0 / 125, 200) - baseline, Is.EqualTo(1).Within(1e-9));
            Assert.That(CameraExposure.PhysicalStops(16, 1.0 / 62.5, 100) - baseline, Is.EqualTo(1).Within(1e-9));
            Assert.That(CameraExposure.PhysicalStops(16 / System.Math.Sqrt(2), 1.0 / 125, 100) - baseline, Is.EqualTo(1).Within(1e-9));
            Assert.That(CameraExposure.Ev100(0, 1, 100), Is.EqualTo(CameraExposure.ReferenceEv100), "a camera with no lens is exposed as drawn");
        }

        [Test]
        public void AFreshProfileDrawsAsBeforeButLetsTheStarGlare()
        {
            var look = CameraLook.Default;
            Assert.That(look.Enabled, Is.True);
            Assert.That(look.Metering, Is.EqualTo(CameraMetering.Fixed));
            Assert.That(look.Tonemapping, Is.EqualTo(ToneCurve.None), "no tone curve: the image's tones as drawn");
            Assert.That(look.Contrast == 0 && look.Saturation == 0 && look.Temperature == 0, Is.True, "no grading");
            Assert.That(CameraExposure.FixedStops(look), Is.EqualTo(0));
            Assert.That(look.Bloom, Is.GreaterThan(0));
            Assert.That(look.BloomThreshold, Is.GreaterThanOrEqualTo(1), "only what is above white blooms");
            Assert.That(look.SunBrightness, Is.GreaterThan(1));
            Assert.That(look.Adapts, Is.False);
            look.Metering = CameraMetering.PhysicalCamera;
            Assert.That(CameraExposure.FixedStops(look), Is.EqualTo(0).Within(0.05), "the physical camera starts at sunny 16");
            look.Enabled = false;
            Assert.That(CameraExposure.FixedStops(look), Is.EqualTo(0));
        }

        [Test]
        public void CompensationAddsOnTopOfEveryMode()
        {
            var look = CameraLook.Default;
            look.Compensation = 1.5;
            Assert.That(CameraExposure.FixedStops(look), Is.EqualTo(1.5));
            look.Metering = CameraMetering.PhysicalCamera;
            look.Iso = 400;
            Assert.That(CameraExposure.FixedStops(look), Is.EqualTo(3.5).Within(0.05));
            Assert.That(CameraExposure.TargetStops(CameraExposure.MiddleGrey, 1.5), Is.EqualTo(1.5).Within(1e-9));
        }

        [Test]
        public void TheAutomaticMeterBringsTheViewToMiddleGrey()
        {
            var grey = Enumerable.Repeat(CameraExposure.MiddleGrey, 100).ToList();
            Assert.That(CameraExposure.LogAverage(grey), Is.EqualTo(CameraExposure.MiddleGrey).Within(1e-9));
            Assert.That(CameraExposure.TargetStops(CameraExposure.LogAverage(grey), 0), Is.EqualTo(0).Within(1e-9));
            var bright = Enumerable.Repeat(CameraExposure.MiddleGrey * 4, 100).ToList();
            Assert.That(CameraExposure.TargetStops(CameraExposure.LogAverage(bright), 0), Is.EqualTo(-2).Within(1e-9), "four times brighter, two stops down");
            Assert.That(CameraExposure.LogAverage(new List<double>()), Is.EqualTo(CameraExposure.MiddleGrey), "nothing read yet meters as drawn");
        }

        [Test]
        public void TheMeterNeverTurnsSpaceGreyOrBlacksOutOnTheStar()
        {
            var space = Enumerable.Repeat(0.0, 100).ToList();
            Assert.That(CameraExposure.TargetStops(CameraExposure.LogAverage(space), 0), Is.EqualTo(CameraExposure.MaxStops));
            var star = Enumerable.Repeat(500.0, 100).ToList();
            Assert.That(CameraExposure.TargetStops(CameraExposure.LogAverage(star), 0), Is.EqualTo(CameraExposure.MinStops));
            Assert.That(CameraExposure.LogAverage(new[] { double.NaN, CameraExposure.MiddleGrey }), Is.GreaterThan(0), "a bad pixel is not a crash");
        }

        [Test]
        public void TheHistogramMetersWhatIsLitNotTheBlackOrTheStar()
        {
            // A lit planet filling over half the frame, black space behind it and the star's face in a corner.
            var view = new List<double>();
            view.AddRange(Enumerable.Repeat(0.0, 40));
            view.AddRange(Enumerable.Repeat(0.4, 58));
            view.AddRange(Enumerable.Repeat(200.0, 2));
            double histogram = CameraExposure.HistogramAverage(view);
            double average = CameraExposure.LogAverage(view);
            Assert.That(histogram, Is.EqualTo(0.4).Within(0.4 * 0.15), "the histogram meters the planet");
            Assert.That(average, Is.LessThan(histogram * 0.25), "the plain average is dragged down by black space");
            // Everything thrown out still meters something.
            Assert.That(CameraExposure.HistogramAverage(view, 0.5, 0.5), Is.GreaterThan(0));
            Assert.That(CameraExposure.HistogramAverage(new List<double>()), Is.EqualTo(CameraExposure.MiddleGrey));
        }

        [Test]
        public void TheEyeAdaptsQuicklyToLightAndSlowlyToDark()
        {
            double toBright = CameraExposure.Adapt(0, -2, 0.25);
            double toDark = CameraExposure.Adapt(0, 2, 0.25);
            Assert.That(toBright, Is.LessThan(0).And.GreaterThan(-2));
            Assert.That(toDark, Is.GreaterThan(0).And.LessThan(2));
            Assert.That(-toBright, Is.GreaterThan(toDark), "a bright view is met faster than a dark one");
            Assert.That(CameraExposure.Adapt(0, -2, 100), Is.EqualTo(-2).Within(1e-6), "it settles on the target");
            Assert.That(CameraExposure.Adapt(0.5, -2, 0), Is.EqualTo(0.5), "no time, no change");
            Assert.That(CameraExposure.Adapt(double.NaN, 1, 0.1), Is.EqualTo(1), "the first reading is taken as it is");
            var look = CameraLook.Default;
            look.Metering = CameraMetering.Histogram;
            Assert.That(look.Adapts, Is.True);
            look.Metering = CameraMetering.Automatic;
            Assert.That(look.Adapts, Is.True);
        }
    }
}
