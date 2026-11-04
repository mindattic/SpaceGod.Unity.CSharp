using UnityEngine.SceneManagement;

namespace Unity.SpaceGod
{
    /// <summary>
    /// The way between the main menu scene and the game scene: the menu starts a new game or names the save to
    /// resume, and the game goes back to the menu. A run started for the tour, a capture or <c>-continue</c>
    /// skips the menu.
    /// </summary>
    public static class GameLaunch
    {
        public const string MenuScene = "MainMenu";
        public const string GameScene = "UnitySpace";

        static string loadSlot;

        /// <summary>Whether this game was started from the main menu (not straight into the game scene).</summary>
        public static bool FromMenu { get; private set; }

        /// <summary>Flags that go straight into the game, with no menu to click through.</summary>
        public static bool SkipsMenu =>
            Bootstrap.HasCommandLineFlag("-demo") || Bootstrap.HasCommandLineFlag("-capture") || Bootstrap.HasCommandLineFlag("-continue")
            || Coop.CoopPeer.SkipsMenu; // US-T39: a co-op client (and the smoke host) goes straight in

        public static void NewGame() => Start(null);

        public static void Load(string slot) => Start(slot);

        static void Start(string slot)
        {
            FromMenu = true;
            loadSlot = slot;
            SceneManager.LoadScene(GameScene);
        }

        /// <summary>The save the menu asked the game to resume, once (null for a new game).</summary>
        public static string TakeLoadSlot()
        {
            string slot = loadSlot;
            loadSlot = null;
            return slot;
        }

        public static void ToMainMenu() => SceneManager.LoadScene(MenuScene);
    }
}
