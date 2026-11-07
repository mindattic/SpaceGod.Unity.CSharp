using Unity.Shared.Core;
using UnityEngine;

namespace Unity.Shared
{
    /// <summary>
    /// Applies the package's side of <see cref="Headless"/> mode before the first scene loads: the session mute
    /// (<see cref="Profiles.SessionMuted"/>, never written to settings.json), <c>Application.runInBackground</c>,
    /// vSync off, and one <c>MINDATTIC_HEADLESS_READY &lt;productName&gt;</c> log line for scripts to wait for. Nothing
    /// game-specific; with headless mode off it does nothing. The no-window part is the launcher's
    /// (<c>-batchmode -nographics</c>, <c>tools/Start-Headless.ps1</c>).
    /// </summary>
    public static class HeadlessBootstrap
    {
        static bool applied;
        static bool sceneLoadReached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnSubsystemRegistration()
        {
            // Statics survive entering Play mode with domain reload off: start each run clean.
            applied = false;
            sceneLoadReached = false;
            Headless.Activated -= OnActivated;
            Headless.Activated += OnActivated;
            // Mute as early as -muted does (ProfilesBootstrap), before anything can play a sound.
            if (Headless.IsActive) Profiles.SessionMuted = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnBeforeSceneLoad()
        {
            sceneLoadReached = true;
            if (Headless.IsActive) Apply();
        }

        static void OnActivated()
        {
            Profiles.SessionMuted = true;
            if (sceneLoadReached) Apply();   // set from code after startup: apply the rest now
        }

        /// <summary>Applies headless mode's package-level effects once per run. Called for you; safe to call again.</summary>
        public static void Apply()
        {
            if (applied) return;
            applied = true;
            Profiles.SessionMuted = true;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", Headless.ReadyLine(Application.productName));
        }
    }
}
