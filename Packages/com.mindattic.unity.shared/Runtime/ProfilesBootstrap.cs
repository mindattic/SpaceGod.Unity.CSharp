using Unity.Shared.Core;
using UnityEngine;

namespace Unity.Shared
{
    /// <summary>
    /// Points <see cref="Profiles"/> at <c>Application.persistentDataPath/Profiles</c> before anything else runs,
    /// in a player build, in Play mode and (in the Editor) on every script reload, so a game can read
    /// <c>Profiles.Current.Settings.Common.DisplayName</c> from any Awake. A store a test or tool set first is
    /// left alone, and the <c>MINDATTIC_PROFILES_ROOT</c> / <c>-profiles-root</c> override still wins.
    /// </summary>
    public static class ProfilesBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnLoad() => Initialize();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void OnEditorLoad() => Initialize();
#endif

        /// <summary>
        /// Sets the data path unless a store is already configured, and applies the <c>-muted</c> /
        /// <c>MINDATTIC_CLI_MUTED</c> session mute (in memory only). Safe to call more than once.
        /// </summary>
        public static void Initialize()
        {
            Profiles.ApplyMutedOverride();
            if (!Profiles.IsConfigured) Profiles.UseDataPath(Application.persistentDataPath);
        }
    }

    /// <summary>Unity colour conversions for the engine-free <see cref="RgbColor"/> and the player colour setting.</summary>
    public static class UnityColorExtensions
    {
        /// <summary>The colour as a Unity <see cref="Color"/> (alpha 1).</summary>
        public static Color ToUnityColor(this RgbColor color) => new Color(color.RedFloat, color.GreenFloat, color.BlueFloat, 1f);

        /// <summary>A Unity <see cref="Color"/> as an <see cref="RgbColor"/> (alpha dropped).</summary>
        public static RgbColor ToRgbColor(this Color color) => RgbColor.FromFloats(color.r, color.g, color.b);

        /// <summary>The player's colour as a Unity <see cref="Color"/>: <c>Profiles.Current.Settings.Common.GetUnityColor()</c>.</summary>
        public static Color GetUnityColor(this CommonSettings common) => common.Color.ToUnityColor();

        /// <summary>Sets the player's colour from a Unity <see cref="Color"/> (call <c>SaveSettings()</c> to keep it).</summary>
        public static void SetUnityColor(this CommonSettings common, Color color) => common.Color = color.ToRgbColor();
    }
}
