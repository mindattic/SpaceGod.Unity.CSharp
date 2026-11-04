// US-T70: SpaceGod.Unreal.CSharp's Runtime/Audit/SettingsAudit.cs for the Unity copy: the same probes, ids and order. Where
// the Unreal one reads the setting back off Unreal (its audio device, the post-process Unreal draws with), this reads it off
// Unity, the engine the original's code drives directly: the audio listener, and the URP volume on the game's camera.
// Settings apply to the engine as they are set, so each step runs on Unity's main thread (AuditRunner.OnMain).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.SpaceGod.Audit
{
    /// <summary>
    /// <see cref="FeatureAudit"/>'s settings probes (US-T50, US-T51): the master volume and the CLI mute reaching the audio
    /// listener, and every Render pipeline / Post-processing setting declared, kept in the profile and put into effect on
    /// the post-process the game's camera draws with.
    /// </summary>
    public static class SettingsAudit
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Action<string, bool, string> result;
        static Action<int> frames;

        static string V(float v) => v.ToString("0.##", Inv);

        /// <summary>How to put a setting back as it is now: its value, or its default (forgotten in the profile) if it stands there.</summary>
        static Action Snapshot(GameSetting s)
        {
            if (s.IsDefault) return s.Reset;
            switch (s)
            {
                case ToggleSetting t: { bool v = t.Value; return () => t.Set(v); }
                case ChoiceSetting c: { int v = c.Index; return () => c.Set(v); }
                case RangeSetting r: { double v = r.Value; return () => r.Set(v); }
                default: return () => { };
            }
        }

        static void SessionAudioOnLoad() =>
            typeof(SessionAudio).GetMethod("OnLoad", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);

        // ---------------------------------------------------------------- audio (US-T50)

        /// <summary>
        /// EngineSettings.MasterVolume sets AudioListener.volume (SessionAudio.ListenerVolume); under the CLI mute (what
        /// -muted / MINDATTIC_CLI_MUTED=1 turn on before the first scene: Profiles.SessionMuted, then SessionAudio.OnLoad)
        /// it is 0 whatever the setting says.
        /// </summary>
        public static void Audio(Action<string, bool, string> report, Action<int> stepFrames)
        {
            result = report;
            frames = stepFrames;
            var master = EngineSettings.MasterVolume;
            var restore = AuditRunner.OnMain(() => Snapshot(master));
            bool m0 = Unity.Shared.Core.Profiles.SessionMuted;
            try
            {
                AuditRunner.OnMain(() => master.Set(50));
                frames(1);
                float listener = AuditRunner.OnMain(() => AudioListener.volume);
                result("audio.volume", listener == SessionAudio.ListenerVolume(50),
                    $"master volume {master.ValueText}: listener {V(listener)}");

                AuditRunner.OnMain(() => { Unity.Shared.Core.Profiles.SessionMuted = true; SessionAudioOnLoad(); });
                frames(1);
                float l1 = AuditRunner.OnMain(() => AudioListener.volume);
                AuditRunner.OnMain(() => master.Set(80));
                frames(1);
                float l2 = AuditRunner.OnMain(() => AudioListener.volume);
                result("audio.mute", l1 == 0 && l2 == 0,
                    $"-muted: listener {V(l1)}; master volume {master.ValueText} under the mute: listener {V(l2)}");
            }
            finally
            {
                AuditRunner.OnMain(() =>
                {
                    Unity.Shared.Core.Profiles.SessionMuted = m0;
                    restore();
                    AudioListener.volume = SessionAudio.ListenerVolume(master.Value);
                });
                frames(1);
            }
        }

        // ---------------------------------------------------------------- render (US-T51)

        /// <summary>
        /// UrpPostProcessSettings.cs's and RenderPipelineSelection.cs's settings, in the order the Render pipeline page lists
        /// them in the original (US-T70): the URP post-processing rows first (their assembly, Unity.SpaceGod.Rendering.Urp,
        /// declares them before the game's own is searched), then the pipeline and HDRP's. Each class's own in declaration order.
        /// </summary>
        static readonly string[] RenderKeys =
        {
            "render.urp.post", "render.urp.exposure", "render.urp.exposureCompensation", "render.urp.camera.iso", "render.urp.camera.shutter",
            "render.urp.camera.aperture", "render.urp.bloom", "render.urp.bloomThreshold", "render.urp.sunBrightness", "render.urp.tonemapping",
            "render.urp.contrast", "render.urp.saturation", "render.urp.temperature",
            "render.pipeline", "render.hdrp.sky", "render.hdrp.volumetrics", "render.hdrp.exposure", "render.hdrp.exposureCompensation",
            "render.hdrp.sunBrightness", "render.hdrp.rayTracing",
        };

        /// <summary>
        /// The post-process the game's camera draws with: the URP volume UrpRenderBackend keeps on it (off, or each
        /// override that is on, with its value). The Unreal copies describe Unreal's post-process settings here.
        /// </summary>
        static string Post()
        {
            var cam = Camera.main;
            if (cam == null) return "no camera";
            var t = cam.transform.Find("PostProcessVolume");
            var volume = t != null ? t.GetComponent<Volume>() : null;
            if (volume == null || !t.gameObject.activeSelf || !volume.enabled || volume.sharedProfile == null) return "no post-process";
            // The star's glare is a shader global the URP backend sets with the look (StarGlare), not a volume override.
            var sb = new StringBuilder("star glare " + Shader.GetGlobalFloat(RenderGlobals.SunBrightness).ToString("0.##", Inv) +
                "; weight " + volume.weight.ToString("0.##", Inv));
            foreach (var c in volume.sharedProfile.components)
            {
                if (c == null || !c.active) continue;
                sb.Append("; ").Append(c.GetType().Name);
                var parts = new List<string>();
                foreach (var p in c.parameters)
                {
                    if (p == null || !p.overrideState) continue;
                    var value = p.GetType().GetProperty("value")?.GetValue(p);
                    parts.Add(value is float f ? f.ToString("0.###", Inv) : Convert.ToString(value, Inv));
                }
                if (parts.Count > 0) sb.Append(' ').Append(string.Join(",", parts));
            }
            return sb.ToString();
        }

        static string Photoreal()
        {
            var p = RenderBackend.Active.Photoreal;
            return $"sky {p.PhysicalSky}, volumetrics {p.Volumetrics}, exposure {(int)p.Exposure}, compensation {p.ExposureCompensation.ToString("0.##", Inv)}, " +
                $"glare {p.SunBrightness.ToString("0.#", Inv)}, ray tracing {p.RayTracing}";
        }

        static string Stored(string key) => new ProfileSettingStore(ActiveProfile.Current).ReadSetting(key) ?? "(none)";

        /// <summary>A setting moved off where it stands: a toggle flipped, a choice to the next, a range to its far end.</summary>
        static void Bump(GameSetting s)
        {
            if (s is ToggleSetting t) t.Set(!t.Value);
            else if (s is ChoiceSetting c) c.Set((c.Index + 1) % c.Options.Count);
            else if (s is RangeSetting r) r.Set(r.Value < r.Max ? r.Max : r.Min);
        }

        public static void Render(Action<string, bool, string> report, Action<int> stepFrames)
        {
            result = report;
            frames = stepFrames;
            // No frame runs inside these probes (as under Unreal): the whole of it on Unity's main thread.
            AuditRunner.OnMain(() =>
            {
                var registry = GameSettingsHost.Registry;
                var inGroup = registry.InGroup(SettingGroups.RenderPipeline).ConvertAll(s => s.Key);
                int found = 0, at = -1;
                bool ordered = true;
                foreach (var key in RenderKeys)
                {
                    int i = inGroup.IndexOf(key);
                    if (i < 0) continue;
                    found++;
                    if (i < at) ordered = false;
                    at = i;
                }
                result("render.settings", found == RenderKeys.Length && ordered,
                    $"{found}/{RenderKeys.Length} in '{SettingGroups.RenderPipeline}', in order {ordered}: {string.Join(", ", inGroup)}");

                foreach (var key in RenderKeys) RenderSetting(registry, key);
            });
        }

        /// <summary>
        /// One render setting moved and put back. It must be kept in the profile; a URP post-processing choice must change
        /// the post-process the camera draws with; HDRP's choices ("Ignored on URP") must reach the backend's photoreal
        /// options and leave the post-process alone; the pipeline choice is kept for the next start (a restart pending).
        /// The physical camera's ISO, shutter and aperture count only under Exposure: Physical camera, which they are
        /// tried under; Exposure itself goes from Fixed to Physical camera at ISO 400.
        /// </summary>
        static void RenderSetting(SettingsRegistry registry, string key)
        {
            var s = registry.Find(key);
            if (s == null) { result(key, false, "not declared"); return; }
            string initial = Post();
            var undo = new List<Action>();
            void Prepare(string k, int index)
            {
                if (registry.Find(k) is ChoiceSetting c)
                {
                    undo.Add(Snapshot(c));
                    c.Set(index);
                }
            }
            if (key.StartsWith("render.urp.camera.", StringComparison.Ordinal)) Prepare("render.urp.exposure", 3);
            if (key == "render.urp.exposure") Prepare("render.urp.camera.iso", 3);
            undo.Add(Snapshot(s));
            try
            {
                string before = Post(), photo0 = Photoreal(), text0 = s.ValueText;
                if (key == "render.urp.exposure") ((ChoiceSetting)s).Set(3);
                else Bump(s);
                string after = Post(), photo1 = Photoreal(), stored = Stored(key);
                bool kept = stored == s.Format(), same = after == before;
                string moved = $"{text0} -> {s.ValueText} (kept '{stored}')";
                if (key == RenderPipelineSettings.PipelineKey)
                    result(key, kept && RenderPipelineSettings.RestartPending && same,
                        $"{moved}: chosen {RenderPipelineSettings.Chosen.DisplayName}, restart pending {RenderPipelineSettings.RestartPending}, post-process unchanged {same}");
                else if (key.StartsWith("render.hdrp.", StringComparison.Ordinal))
                    result(key, kept && photo1 != photo0 && same, $"{moved}: photoreal {photo1}; post-process unchanged {same}");
                else
                    result(key, kept && !same, $"{moved}: {before} -> {after}");
            }
            finally
            {
                for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
                string back = Post();
                if (back != initial) result(key + ".restore", false, $"put back: {back}, was {initial}");
            }
        }
    }
}
