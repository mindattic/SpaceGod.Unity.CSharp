using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.SpaceGod
{
    /// <summary>
    /// Makes any scene playable: if no <see cref="UnitySpaceApp"/> exists after load, one is
    /// created. With no flags that is all that happens. <c>-demo</c> runs the scripted tour once and
    /// quits (<see cref="CaptureRunner"/>) — the same flag name used for it in every MindAttic Unity
    /// project. Pair it with <c>-capture</c> (parsed separately by MindAttic.Capture) to record the
    /// session.
    /// </summary>
    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureApp()
        {
            // The main menu is a scene of its own, and the app is not made in it: the menu loads the game scene
            // when the player starts. A run for the tour, a capture or -continue goes straight there.
            if (UnityEngine.Object.FindAnyObjectByType<UI.MainMenu>() != null)
            {
                if (!GameLaunch.SkipsMenu) return;
                SceneManager.sceneLoaded += AfterSkippingMenu;
                SceneManager.LoadScene(GameLaunch.GameScene);
                return;
            }
            Equip();
        }

        static void AfterSkippingMenu(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= AfterSkippingMenu;
            Equip();
        }

        static void Equip()
        {
            var app = UnityEngine.Object.FindAnyObjectByType<UnitySpaceApp>();
            if (app == null) app = new GameObject("Unity.SpaceGod").AddComponent<UnitySpaceApp>();

            if (HasCommandLineFlag("-demo") && app.GetComponent<CaptureRunner>() == null)
            {
                // -capture's own timer (if requested) keeps running unmodified: -demo only adds
                // the tour, it does not change what -capture does.
                app.gameObject.AddComponent<CaptureRunner>();
            }
        }

        public static bool HasCommandLineFlag(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static UnitySpaceApp app;

        /// <summary>US-T39 (co-op): the app of the game scene, or null (the main menu, or between scenes).</summary>
        public static UnitySpaceApp App
        {
            get
            {
                if (app == null) app = UnityEngine.Object.FindAnyObjectByType<UnitySpaceApp>();
                return app != null ? app : null;
            }
        }

        /// <summary>US-T39 (co-op): the value of a <c>-Name=value</c> argument, or null.</summary>
        public static string CommandLineValue(string name)
        {
            string prefix = name + "=";
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length).Trim('"');
            return null;
        }
    }
}
