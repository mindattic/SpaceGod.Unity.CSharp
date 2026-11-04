// US-T70: SpaceGod.Unreal.CSharp's Runtime/Audit/DisplayAudit.cs for the Unity copy: the same probes and ids. render.msaa
// reads Unity's camera back (URP honours the camera's MSAA flag; the Unreal copies read their console variables there);
// display.modes and display.exclusive-rate are the Unreal probe's own code, Screen being Unity's own here. Unity's API is the
// main thread's, so each step between frames runs there (AuditRunner.OnMain).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.SpaceGod.Audit
{
    /// <summary>
    /// <see cref="FeatureAudit"/>'s display probes: render.msaa as the original's MSAA (US-T55) and the display modes with
    /// their real refresh rates, the exclusive-fullscreen rate asked for as the original's DisplayOptions.Apply does (US-T56).
    /// </summary>
    public static class DisplayAudit
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The pipeline asset's MSAA sample count (URP's msaaSampleCount), or -1.</summary>
        static int AssetMsaa()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            var p = asset != null ? asset.GetType().GetProperty("msaaSampleCount") : null;
            return p != null ? Convert.ToInt32(p.GetValue(asset), Inv) : -1;
        }

        /// <summary>
        /// render.msaa off then on: off is the game's camera drawing without multisampling, on is with it, at the pipeline
        /// asset's 4 samples (the original's URP asset, m_MSAA: 4).
        /// </summary>
        public static void Msaa(Action<string, bool, string> result, Action<int> frames)
        {
            var msaa = EngineSettings.Msaa;
            bool before = msaa.Value;
            bool wasDefault = msaa.IsDefault;
            AuditRunner.OnMain(() => msaa.Set(false));
            frames(1);
            bool offAllowed = AuditRunner.OnMain(() => Camera.main != null && Camera.main.allowMSAA);
            AuditRunner.OnMain(() => msaa.Set(true));
            frames(1);
            bool onAllowed = AuditRunner.OnMain(() => Camera.main != null && Camera.main.allowMSAA);
            int samples = AuditRunner.OnMain(AssetMsaa);
            AuditRunner.OnMain(() => { if (wasDefault) msaa.Reset(); else msaa.Set(before); });
            frames(1);
            result("render.msaa", !offAllowed && onAllowed && samples == 4,
                string.Format(Inv, "off: camera MSAA {0}; on: camera MSAA {1}, pipeline asset {2} samples", offAllowed, onAllowed, samples));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODEW devMode);

        /// <summary>Windows' own list for the primary display (whole Hz), the yardstick the rates are checked against.</summary>
        static Dictionary<(int, int), int> WindowsBestRates()
        {
            var best = new Dictionary<(int, int), int>();
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return best;
            var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            for (int i = 0; EnumDisplaySettingsW(null, i, ref dm); i++)
            {
                var key = ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
                if (!best.TryGetValue(key, out int hz) || dm.dmDisplayFrequency > hz) best[key] = (int)dm.dmDisplayFrequency;
            }
            return best;
        }

        /// <summary>
        /// The Display page's list (DisplayOptions.Resolutions, the original's code) carries each size's best real rate:
        /// it must match what Windows lists for that size (to the whole Hz Windows truncates to), not 60 everywhere. Then
        /// Apply in exclusive fullscreen at the largest size asks Screen.SetResolution for that size's best rate, as the
        /// original's TryFindMode does, and is put back.
        /// </summary>
        public static void Modes(Action<string, bool, string> result, Action<int> frames)
        {
            DisplaySettings was = default;
            DisplayResolution largest = default;
            Resolution exact = default;
            AuditRunner.OnMain(() =>
            {
                var raw = Screen.resolutions;
                var page = DisplayOptions.Resolutions();
                var windows = WindowsBestRates();
                var sizes = new HashSet<(int, int)>(raw.Select(r => (r.width, r.height)));
                var real = page.Where(r => sizes.Contains((r.Width, r.Height))).ToList();
                int matching = real.Count(r => windows.TryGetValue((r.Width, r.Height), out int hz) && Math.Abs(r.RefreshHz - hz) < 1.0);
                var rates = raw.Select(r => Math.Round(r.refreshRateRatio.value)).Distinct().OrderBy(v => v).ToList();
                largest = real.OrderBy(r => (long)r.Width * r.Height).LastOrDefault();
                var l = largest;
                exact = raw.Where(r => r.width == l.Width && r.height == l.Height).OrderBy(r => r.refreshRateRatio.value).LastOrDefault();
                result("display.modes", real.Count > 0 && matching == real.Count,
                    string.Format(Inv, "{0} modes, {1} sizes on the page, {2} of them at Windows' best rate; rates {3} Hz; largest {4} ({5}/{6})",
                        raw.Length, real.Count, matching, string.Join(", ", rates), largest, exact.refreshRateRatio.numerator, exact.refreshRateRatio.denominator));

                was = DisplayOptions.Current;
            });
            // Unity has no read-back of the rate Screen.SetResolution was asked for (the Unreal copies' facade records it), and
            // applying exclusive fullscreen here would switch the player's monitor: so the rate DisplayOptions.Apply would ask
            // for is read from the very method it asks with (TryFindMode, the original's own), and nothing is applied.
            var asked = AuditRunner.OnMain(() =>
            {
                var m = typeof(DisplayOptions).GetMethod("TryFindMode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var args = new object[] { largest.Width, largest.Height, null };
                bool found = m != null && (bool)m.Invoke(null, args);
                return found ? ((Resolution)args[2]).refreshRateRatio : default;
            });
            frames(1);
            result("display.exclusive-rate", largest.Width > 0 && asked.numerator == exact.refreshRateRatio.numerator && asked.denominator == exact.refreshRateRatio.denominator,
                string.Format(Inv, "Exclusive {0} x {1}: DisplayOptions.Apply would ask {2}/{3} Hz ({4:F3}) (TryFindMode; not applied, so the display is left as {5})",
                    largest.Width, largest.Height, asked.numerator, asked.denominator, asked.value, was));
        }
    }
}
