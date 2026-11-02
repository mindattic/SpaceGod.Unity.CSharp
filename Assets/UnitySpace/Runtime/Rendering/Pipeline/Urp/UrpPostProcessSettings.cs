using System;
using System.Globalization;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod.Rendering.Urp
{
    /// <summary>
    /// URP's post-processing in Settings > Render pipeline (U.SPC-T451): the Volume stack the game's own camera sees
    /// through on URP (exposure metering, the physical camera, bloom, the tone curve and colour grading), each remembered
    /// per profile and put into effect at once. HDRP meters and grades by its own settings and ignores these.
    /// </summary>
    [SettingsSource]
    public static class UrpPostProcessSettings
    {
        const string Group = SettingGroups.RenderPipeline;

        static readonly string[] MeteringNames = { "Fixed", "Automatic (eye)", "Histogram (eye)", "Physical camera" };
        static readonly string[] CurveNames = { "None (as drawn)", "Neutral", "ACES (filmic)" };
        static readonly double[] IsoStops = { 50, 100, 200, 400, 800, 1600, 3200, 6400 };
        static readonly double[] ShutterStops = { 30, 60, 125, 250, 500, 1000, 2000, 4000 };
        static readonly double[] ApertureStops = { 1.4, 2, 2.8, 4, 5.6, 8, 11, 16, 22 };

        static string[] Names(double[] stops, Func<double, string> name)
        {
            var names = new string[stops.Length];
            for (int i = 0; i < names.Length; i++) names[i] = name(stops[i]);
            return names;
        }

        static string Number(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        static int IndexOf(double[] stops, double value)
        {
            int best = 0;
            for (int i = 1; i < stops.Length; i++)
                if (Math.Abs(stops[i] - value) < Math.Abs(stops[best] - value)) best = i;
            return best;
        }

        static string Signed(double v, string unit) => (v > 0 ? "+" : "") + v.ToString("0.##", CultureInfo.InvariantCulture) + unit;

        public static readonly ToggleSetting Enabled = new ToggleSetting("render.urp.post", Group,
            "Post-processing (URP)", CameraLook.Default.Enabled,
            "URP's own Volume stack for the game's view: HDR, so the star's face glares above white, with exposure, bloom, a tone " +
            "curve and colour grading below. Off draws the image exactly as the shaders do, with no post-processing. Ignored on HDRP.",
            "post processing volume hdr urp photoreal").Applying(_ => Apply());

        public static readonly ChoiceSetting Metering = new ChoiceSetting("render.urp.exposure", Group,
            "Exposure (URP)", MeteringNames, (int)CameraLook.Default.Metering,
            "Fixed shows the image as drawn, moved only by the compensation below. Automatic meters the average of everything in " +
            "view and adapts like an eye; Histogram does the same but throws out black space and the star's face first, so it " +
            "meters what is lit. Physical camera exposes by the ISO, shutter and aperture below, against sunny 16. Ignored on HDRP.",
            "exposure eye adaptation auto exposure histogram metering physical camera ev urp").Applying(_ => Apply());

        public static readonly RangeSetting Compensation = new RangeSetting("render.urp.exposureCompensation", Group,
            "Exposure compensation (URP)", CameraLook.Default.Compensation, -3, 3, 0.25,
            "Brightens (+) or darkens (-) the image by stops on top of the exposure. Ignored on HDRP.",
            v => Signed(v, " EV"), "brightness exposure ev compensation urp").Applying(_ => Apply());

        public static readonly ChoiceSetting Iso = new ChoiceSetting("render.urp.camera.iso", Group,
            "Physical camera ISO (URP)", Names(IsoStops, v => "ISO " + Number(v)), IndexOf(IsoStops, CameraLook.Default.Iso),
            "The sensor's sensitivity when Exposure is Physical camera: each doubling brightens the image a stop. Ignored on HDRP.",
            "iso sensitivity physical camera urp").Applying(_ => Apply());

        public static readonly ChoiceSetting Shutter = new ChoiceSetting("render.urp.camera.shutter", Group,
            "Physical camera shutter (URP)", Names(ShutterStops, v => "1/" + Number(v) + " s"), IndexOf(ShutterStops, 1 / CameraLook.Default.ShutterSeconds),
            "The shutter time when Exposure is Physical camera: each halving darkens the image a stop. Ignored on HDRP.",
            "shutter speed physical camera urp").Applying(_ => Apply());

        public static readonly ChoiceSetting Aperture = new ChoiceSetting("render.urp.camera.aperture", Group,
            "Physical camera aperture (URP)", Names(ApertureStops, v => "f/" + Number(v)), IndexOf(ApertureStops, CameraLook.Default.Aperture),
            "The lens's f-number when Exposure is Physical camera: each full stop wider (f/16 to f/11) brightens the image a stop; " +
            "f/16 at 1/125 s and ISO 100 is sunny 16, the image as drawn. Ignored on HDRP.",
            "aperture f-stop f number lens physical camera urp").Applying(_ => Apply());

        public static readonly RangeSetting Bloom = new RangeSetting("render.urp.bloom", Group,
            "Bloom (URP)", CameraLook.Default.Bloom, 0, 2, 0.05,
            "How strongly what is brighter than the threshold below (the star's face, engine plumes at full burn) bleeds light " +
            "around itself. 0 turns bloom off. Ignored on HDRP.", null, "bloom glow glare urp").Applying(_ => Apply());

        public static readonly RangeSetting BloomThreshold = new RangeSetting("render.urp.bloomThreshold", Group,
            "Bloom threshold (URP)", CameraLook.Default.BloomThreshold, 0.5, 4, 0.1,
            "The brightness bloom starts at, where 1 is the display's white: lower lets more of the image glow. Ignored on HDRP.",
            null, "bloom threshold glow urp").Applying(_ => Apply());

        public static readonly RangeSetting SunBrightness = new RangeSetting("render.urp.sunBrightness", Group,
            "Star glare (URP)", CameraLook.Default.SunBrightness, 1, 32, 1,
            "How much brighter than a sunlit surface the star's face is drawn, so it blooms and stops an adapting exposure down. " +
            "Ignored on HDRP (it has its own) and with post-processing off.",
            v => Number(v) + "x", "sun star glare bloom brightness urp").Applying(_ => Apply());

        public static readonly ChoiceSetting Tonemapping = new ChoiceSetting("render.urp.tonemapping", Group,
            "Tone curve (URP)", CurveNames, (int)CameraLook.Default.Tonemapping,
            "How the image's range is fitted to the display. None clips what is above white, as the game drew it; Neutral rolls the " +
            "highlights off gently; ACES is film-like, with more contrast. Ignored on HDRP (always ACES).",
            "tonemapping tone mapping aces neutral filmic hdr urp").Applying(_ => Apply());

        public static readonly RangeSetting Contrast = new RangeSetting("render.urp.contrast", Group,
            "Contrast (URP)", CameraLook.Default.Contrast, -50, 50, 1,
            "Colour grading: spreads (+) or squeezes (-) the image's tones. Ignored on HDRP.", v => Signed(v, ""),
            "contrast colour color grading urp").Applying(_ => Apply());

        public static readonly RangeSetting Saturation = new RangeSetting("render.urp.saturation", Group,
            "Saturation (URP)", CameraLook.Default.Saturation, -50, 50, 1,
            "Colour grading: richer (+) or more muted (-) colour. Ignored on HDRP.", v => Signed(v, ""),
            "saturation colour color grading urp").Applying(_ => Apply());

        public static readonly RangeSetting Temperature = new RangeSetting("render.urp.temperature", Group,
            "White balance (URP)", CameraLook.Default.Temperature, -50, 50, 1,
            "Colour grading: warmer (+) or cooler (-) light. Ignored on HDRP.", v => Signed(v, ""),
            "white balance temperature warm cool colour color grading urp").Applying(_ => Apply());

        /// <summary>Whether every declaration above has run (each applies on construction, before those after it exist).</summary>
        static bool Declared => Enabled != null && Metering != null && Compensation != null && Iso != null && Shutter != null && Aperture != null
            && Bloom != null && BloomThreshold != null && SunBrightness != null && Tonemapping != null && Contrast != null && Saturation != null
            && Temperature != null;

        /// <summary>The choices as they stand (the defaults while the settings are still being declared).</summary>
        public static CameraLook Current
        {
            get
            {
                if (!Declared) return CameraLook.Default;
                return new CameraLook
                {
                    Enabled = Enabled, Metering = (CameraMetering)Metering.Index, Compensation = Compensation.Value,
                    Iso = IsoStops[Iso.Index], ShutterSeconds = 1 / ShutterStops[Shutter.Index], Aperture = ApertureStops[Aperture.Index],
                    Bloom = Bloom.Value, BloomThreshold = BloomThreshold.Value, Tonemapping = (ToneCurve)Tonemapping.Index,
                    Contrast = Contrast.Value, Saturation = Saturation.Value, Temperature = Temperature.Value, SunBrightness = SunBrightness.Value,
                };
            }
        }

        static void Apply()
        {
            if (!Declared) return;
            UrpRenderBackend.Existing?.SetLook(Current);
        }
    }
}
