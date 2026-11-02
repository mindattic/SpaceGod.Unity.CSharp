using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.SpaceGod.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.SpaceGod.Rendering
{
    /// <summary>
    /// The render-pipeline settings (U.SPC-T430): which pipeline the game runs on (URP, the default and the lighter
    /// one, or HDRP, the photoreal path), applied the next time the game starts, and the photoreal choices a pipeline
    /// that offers them puts into effect at once (a physically based sky, volumetric light, how the eye is metered for
    /// the star's light, and ray tracing). Each is remembered per profile like every other setting.
    /// </summary>
    [SettingsSource]
    public static class RenderPipelineSettings
    {
        /// <summary>The key the pipeline choice is kept under (read at start-up, before the settings are bound).</summary>
        public const string PipelineKey = "render.pipeline";

        static readonly string[] PipelineNames = MenuNames();

        static string[] MenuNames()
        {
            var all = RenderPipelineProfiles.All;
            var names = new string[all.Count];
            for (int i = 0; i < names.Length; i++) names[i] = all[i].MenuName;
            return names;
        }

        static int IndexOf(RenderPipelineProfile profile) => Math.Max(0, Array.IndexOf(PipelineNames, profile.MenuName));

        public static readonly ChoiceSetting Pipeline = new ChoiceSetting(PipelineKey, SettingGroups.RenderPipeline,
            "Render pipeline", PipelineNames, IndexOf(RenderPipelineProfiles.Default),
            "URP is the default and the lighter choice. HDRP is the photoreal path: a physically based sky and volumetric light in a " +
            "planet's atmosphere, exposure metered for the star's light, and optional ray tracing; it needs a GPU with compute shaders. " +
            "Takes effect the next time the game starts (the game restarts into its HDRP build, installed beside it).",
            "hdrp urp photoreal high definition universal restart quality");

        public static readonly ToggleSetting PhysicalSky = new ToggleSetting("render.hdrp.sky", SettingGroups.RenderPipeline,
            "Physically based sky (HDRP)", PhotorealOptions.Default.PhysicalSky,
            "While the ship you fly or follow comes down through a docking ring, sits at its city or climbs back, HDRP draws that " +
            "world's atmosphere as light really scatters in it: blue overhead, red at sunset, the haze below. Everywhere else the " +
            "planets keep their own atmosphere rims. Ignored on URP.", "atmosphere scattering sky rayleigh docking ring descent entry hdrp").Applying(_ => Apply());

        public static readonly ToggleSetting Volumetrics = new ToggleSetting("render.hdrp.volumetrics", SettingGroups.RenderPipeline,
            "Volumetric light (HDRP)", PhotorealOptions.Default.Volumetrics,
            "During a docking-ring descent the air itself catches the star's light, in shafts and haze. Costs GPU time. Ignored on URP.",
            "fog god rays sunbeams haze volumetric hdrp").Applying(_ => Apply());

        public static readonly ChoiceSetting Exposure = new ChoiceSetting("render.hdrp.exposure", SettingGroups.RenderPipeline,
            "Sun exposure (HDRP)", new[] { "Fixed for sunlight", "Adaptive (eye)" }, (int)PhotorealOptions.Default.Exposure,
            "Fixed meters the image once for the star's light (EV 15), so it looks as drawn. Adaptive meters what is in view like an " +
            "eye: the star's glare in frame stops the picture down, and it opens up again in shadow. Ignored on URP.",
            "exposure eye adaptation auto exposure ev metering glare hdrp").Applying(_ => Apply());

        public static readonly RangeSetting ExposureCompensation = new RangeSetting("render.hdrp.exposureCompensation", SettingGroups.RenderPipeline,
            "Exposure compensation (HDRP)", PhotorealOptions.Default.ExposureCompensation, -3, 3, 0.25,
            "Brightens (+) or darkens (-) the whole image by stops on top of the metering. Ignored on URP.",
            v => (v > 0 ? "+" : "") + v.ToString("0.##", CultureInfo.InvariantCulture) + " EV", "brightness exposure ev hdrp").Applying(_ => Apply());

        public static readonly RangeSetting SunBrightness = new RangeSetting("render.hdrp.sunBrightness", SettingGroups.RenderPipeline,
            "Star glare (HDRP)", PhotorealOptions.Default.SunBrightness, 1, 64, 1,
            "How much brighter than a sunlit surface the star's face is drawn, so it blooms and, with adaptive exposure, dazzles. Ignored on URP.",
            v => v.ToString("0", CultureInfo.InvariantCulture) + "x", "sun glare bloom brightness hdrp").Applying(_ => Apply());

        public static readonly ToggleSetting RayTracing = new ToggleSetting("render.hdrp.rayTracing", SettingGroups.RenderPipeline,
            "Ray tracing (HDRP)", PhotorealOptions.Default.RayTracing,
            "Ray-traced shadows and reflections from the star, on a GPU and graphics API (DirectX 12) that support it; " +
            "otherwise it stays off. Costs a lot of GPU time. Ignored on URP.", "raytracing rtx dxr reflections shadows hdrp").Applying(_ => Apply());

        /// <summary>The photoreal choices as they stand.</summary>
        public static PhotorealOptions Options => new PhotorealOptions
        {
            PhysicalSky = PhysicalSky, Volumetrics = Volumetrics, Exposure = (SunExposure)Exposure.Index,
            ExposureCompensation = ExposureCompensation.Value, SunBrightness = SunBrightness.Value, RayTracing = RayTracing,
        };

        /// <summary>The pipeline chosen in the menu (the one the game starts on next time).</summary>
        public static RenderPipelineProfile Chosen => RenderPipelineProfiles.ForMenuName(Pipeline.Selected) ?? RenderPipelineProfiles.Default;

        /// <summary>Whether the pipeline chosen is not the one running, so a restart is waiting to apply it.</summary>
        public static bool RestartPending => Chosen.Id != RenderBackend.Active.Profile.Id;

        // Each declaration applies on construction, before those after it exist: wait for the last.
        static void Apply()
        {
            if (PhysicalSky == null || Volumetrics == null || Exposure == null || ExposureCompensation == null || SunBrightness == null || RayTracing == null)
                return;
            RenderBackend.Active.Photoreal = Options;
        }
    }

    /// <summary>
    /// Puts the chosen render pipeline into effect at start-up (U.SPC-T430), before the first scene loads and so
    /// before any camera has drawn. The choice comes from the active profile (<see cref="RenderPipelineSettings.PipelineKey"/>)
    /// or <c>-pipeline urp|hdrp</c> on the command line, which wins.
    /// <list type="bullet">
    /// <item>A player holds one pipeline only (Unity builds no player with two), so each pipeline is its own player:
    /// URP's at the top of the install, HDRP's in its <see cref="RenderPipelineProfile.PlayerFolder"/>. A player asked
    /// for the other pipeline starts that player with the same arguments (and <c>-pipeline</c>) and quits; when it is
    /// not installed, or the GPU cannot run it, the game carries on where it is.</item>
    /// <item>The editor holds both: each pipeline runs from the quality levels whose pipeline asset is that pipeline's
    /// (HDRP has one of its own; the others fall back to the project's URP asset), so choosing one is choosing such a
    /// level, put back when play mode ends.</item>
    /// </list>
    /// Either way <see cref="RenderBackend"/> then matches its backend afresh.
    /// </summary>
    public static class RenderPipelineSelection
    {
        const string PipelineArg = "-pipeline";

        // Marks a run one player started for another, so it never hands on again (no ping-pong between two installs).
        const string RelaunchArg = "-pipelineRelaunch";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void SelectAtStartup()
        {
            var asked = FromCommandLine();
            var wanted = asked ?? FromProfile() ?? RenderPipelineProfiles.Default;
#if UNITY_EDITOR
            if (!Select(wanted) && wanted != RenderPipelineProfiles.Default) Select(RenderPipelineProfiles.Default);
#else
            if (!Application.isBatchMode) Restart(wanted, handOn: Array.IndexOf(Environment.GetCommandLineArgs(), RelaunchArg) < 0);
#endif
        }

        /// <summary>
        /// In a player: starts the player of <paramref name="wanted"/> and quits, when it is not the pipeline running,
        /// it is installed, and the GPU can run it. False when the game stays where it is.
        /// </summary>
        public static bool Restart(RenderPipelineProfile wanted, bool handOn = true)
        {
            var running = RenderBackend.Active.Profile;
            if (wanted == null || wanted.Id == running.Id || !handOn) return false;
            if (wanted.RequiresComputeShaders && !SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning($"[Unity.SpaceGod] {wanted.DisplayName} needs compute shaders, which this GPU lacks; staying on {running.DisplayName}.");
                return false;
            }
            string exe = RenderPipelineProfiles.PlayerExecutable(Environment.GetCommandLineArgs()[0], running, wanted);
            if (exe == null || !System.IO.File.Exists(exe))
            {
                Debug.LogWarning($"[Unity.SpaceGod] The {wanted.DisplayName} player is not installed ({exe ?? "no path"}); staying on {running.DisplayName}.");
                return false;
            }
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(exe, Arguments(wanted)) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) };
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Unity.SpaceGod] Could not start the {wanted.DisplayName} player: {e.Message}; staying on {running.DisplayName}.");
                return false;
            }
            Debug.Log($"[Unity.SpaceGod] Restarting on {wanted.DisplayName}: {exe}");
            Application.Quit();
            return true;
        }

        /// <summary>This run's arguments for the other player, its own <c>-pipeline</c> replacing any given.</summary>
        static string Arguments(RenderPipelineProfile wanted)
        {
            var args = Environment.GetCommandLineArgs();
            var kept = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], PipelineArg, StringComparison.OrdinalIgnoreCase)) { i++; continue; }
                if (string.Equals(args[i], RelaunchArg, StringComparison.OrdinalIgnoreCase)) continue;
                kept.Add(args[i].IndexOf(' ') >= 0 ? "\"" + args[i] + "\"" : args[i]);
            }
            kept.Add(PipelineArg);
            kept.Add(wanted.Id);
            kept.Add(RelaunchArg);
            return string.Join(" ", kept);
        }

        static RenderPipelineProfile FromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], PipelineArg, StringComparison.OrdinalIgnoreCase))
                    return RenderPipelineProfiles.ForMenuName(args[i + 1]);
            return null;
        }

        static RenderPipelineProfile FromProfile()
        {
            try
            {
                string stored = new ProfileSettingStore(ActiveProfile.Current).ReadSetting(RenderPipelineSettings.PipelineKey);
                return RenderPipelineProfiles.ForMenuName(stored?.Trim());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Unity.SpaceGod] Could not read the render pipeline from the profile; starting on the default. " + e.Message);
                return null;
            }
        }

        /// <summary>The pipeline asset type each quality level runs (null where it falls back to the default asset).</summary>
        static List<string> LevelAssetTypes()
        {
            var types = new List<string>(QualitySettings.count);
            for (int i = 0; i < QualitySettings.count; i++)
            {
                var asset = QualitySettings.GetRenderPipelineAssetAt(i);
                types.Add(asset != null ? asset.GetType().Name : null);
            }
            return types;
        }

        /// <summary>
        /// Switches to a quality level that runs <paramref name="wanted"/> (in the editor, which holds every pipeline);
        /// false (and nothing changed) when there is none or the GPU cannot run it.
        /// </summary>
        public static bool Select(RenderPipelineProfile wanted)
        {
            if (wanted == null) return false;
            if (wanted.RequiresComputeShaders && !SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning($"[Unity.SpaceGod] {wanted.DisplayName} needs compute shaders, which this GPU lacks; staying on {RenderPipelineProfiles.Default.DisplayName}.");
                return false;
            }
            var defaultAsset = GraphicsSettings.defaultRenderPipeline;
            int current = QualitySettings.GetQualityLevel();
            int level = RenderPipelineProfiles.QualityLevelFor(wanted, LevelAssetTypes(), defaultAsset != null ? defaultAsset.GetType().Name : null, current);
            if (level < 0)
            {
                Debug.LogWarning($"[Unity.SpaceGod] This build has no quality level for {wanted.DisplayName}.");
                return false;
            }
            if (level != current)
            {
#if UNITY_EDITOR
                RememberEditorLevel(current);
#endif
                QualitySettings.SetQualityLevel(level, true);
            }
            RenderBackend.Reset();
            return true;
        }

#if UNITY_EDITOR
        static bool restoring;

        // The editor keeps the quality level it was on: a play session on the other pipeline puts it back on leaving play mode.
        static void RememberEditorLevel(int level)
        {
            if (restoring) return;
            restoring = true;
            void Restore(UnityEditor.PlayModeStateChange change)
            {
                if (change != UnityEditor.PlayModeStateChange.EnteredEditMode) return;
                UnityEditor.EditorApplication.playModeStateChanged -= Restore;
                restoring = false;
                QualitySettings.SetQualityLevel(level, true);
                RenderBackend.Reset();
            }
            UnityEditor.EditorApplication.playModeStateChanged += Restore;
        }
#endif
    }
}
