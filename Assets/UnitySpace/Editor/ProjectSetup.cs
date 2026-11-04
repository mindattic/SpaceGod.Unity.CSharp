using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.UI;

namespace Unity.SpaceGod.EditorTools
{
    /// <summary>
    /// Builds the main scene and the Windows player. The scene is generated rather than hand-made
    /// so it stays trivially reproducible: it only holds a camera and the UnitySpaceApp object,
    /// and everything else is constructed at runtime from data.
    ///
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod Unity.SpaceGod.EditorTools.ProjectSetup.SetupAndBuild
    ///
    /// Unity builds a player for one render pipeline only, so the batch build makes two (U.SPC-T430): the URP player
    /// (the default) and, in its HDRP folder, the HDRP one the settings menu restarts into (<c>-skipHdrp</c> leaves it out).
    ///
    /// The scene is committed and is only written when it is missing (U.SPC-T262): Unity gives every
    /// object a fresh random file ID, so regenerating it on each build left a no-op diff in git after
    /// every deploy. A change to <see cref="CreateScene"/> therefore reaches the player only once the
    /// scene is rebuilt (Unity.SpaceGod > Rebuild Main Scene, or -executeMethod ...ProjectSetup.CreateScene)
    /// and committed together with the code change.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/Scenes/UnitySpace.unity";
        public const string MenuScenePath = "Assets/Scenes/MainMenu.unity";
        public const string PlayerPath = "Builds/Windows/SpaceGod.Unity.CSharp.exe"; // SpaceGod.Unity.CSharp: its own exe and productName (its own AppData profile and saves)
        public const string HdrpAssetPath = "Assets/UnitySpace/Settings/UnitySpace_HDRP.asset";

        /// <summary>The HDRP player, in the folder of the URP one (<see cref="RenderPipelineProfile.PlayerFolder"/>).</summary>
        public static string HdrpPlayerPath =>
            Path.Combine(Path.GetDirectoryName(PlayerPath), RenderPipelineProfiles.HighDefinition.PlayerFolder, Path.GetFileName(PlayerPath)).Replace('\\', '/');

        static ProjectSetup()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(ScenePath)) CreateScene();
                if (!File.Exists(MenuScenePath)) CreateMainMenuScene();
                var active = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(active.path) && !active.isDirty && !EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorSceneManager.OpenScene(ScenePath);
            };
        }

        [MenuItem("Unity.SpaceGod/Rebuild Main Scene")]
        public static void CreateScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.farClipPlane = 5e8f;
            cameraGo.AddComponent<AudioListener>();

            new GameObject("Unity.SpaceGod").AddComponent<UnitySpaceApp>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            SetBuildScenes();
            Debug.Log("[Unity.SpaceGod] Scene written to " + ScenePath);
        }

        /// <summary>The main menu scene the player opens on: a camera on the sky and the <see cref="MainMenu"/>.</summary>
        [MenuItem("Unity.SpaceGod/Rebuild Main Menu Scene")]
        public static void CreateMainMenuScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Path.GetDirectoryName(MenuScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.farClipPlane = 5e8f;
            cameraGo.AddComponent<AudioListener>();

            new GameObject("Main Menu").AddComponent<MainMenu>();

            EditorSceneManager.SaveScene(scene, MenuScenePath);
            SetBuildScenes();
            Debug.Log("[Unity.SpaceGod] Scene written to " + MenuScenePath);
        }

        /// <summary>The main menu first (the player opens on it), then the game.</summary>
        static void SetBuildScenes()
        {
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MenuScenePath, true), new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Unity.SpaceGod/Build Windows Player")]
        public static void BuildWindows() => Build(PlayerPath);

        static void Build(string path)
        {
            if (!File.Exists(ScenePath)) CreateScene();
            if (!File.Exists(MenuScenePath)) CreateMainMenuScene();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { MenuScenePath, ScenePath },
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
                // US-T72 (docs/CAPTURE.md section 6): capture-enabled, so the published game honours -capture; a launch
                // without -capture does nothing extra (no ffmpeg, no files).
                extraScriptingDefines = new[] { "MINDATTIC_CAPTURE" },
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("[Unity.SpaceGod] Build failed: " + report.summary.result + " (" + report.summary.totalErrors + " errors)");
            Debug.Log("[Unity.SpaceGod] Player built: " + path + " (" + report.summary.totalSize / (1024 * 1024) + " MB)");
        }

        /// <summary>
        /// The HDRP player (U.SPC-T430). The project runs URP by default and keeps HDRP on a quality level of its own,
        /// which Unity will not build into the same player; so for this build alone every quality level and the
        /// default point at the HDRP asset, and all of it is put back afterwards, whether the build succeeds or not.
        /// </summary>
        [MenuItem("Unity.SpaceGod/Build Windows Player (HDRP)")]
        public static void BuildWindowsHdrp()
        {
            var hdrp = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(HdrpAssetPath);
            if (hdrp == null) throw new Exception("[Unity.SpaceGod] Missing the HDRP pipeline asset " + HdrpAssetPath);
            var quality = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
            var levels = quality.FindProperty("m_QualitySettings");
            // Kept as asset paths: a build unloads assets, so references held across it come back destroyed.
            var saved = new string[levels.arraySize];
            string savedDefault = AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < saved.Length; i++)
            {
                var pipeline = levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline");
                saved[i] = AssetDatabase.GetAssetPath(pipeline.objectReferenceValue);
                pipeline.objectReferenceValue = hdrp;
            }
            try
            {
                quality.ApplyModifiedPropertiesWithoutUndo();
                GraphicsSettings.defaultRenderPipeline = hdrp;
                Build(HdrpPlayerPath);
            }
            finally
            {
                RenderPipelineAsset Load(string path) => string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                quality = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
                levels = quality.FindProperty("m_QualitySettings");
                for (int i = 0; i < saved.Length && i < levels.arraySize; i++)
                    levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline").objectReferenceValue = Load(saved[i]);
                quality.ApplyModifiedPropertiesWithoutUndo();
                GraphicsSettings.defaultRenderPipeline = Load(savedDefault);
                AssetDatabase.SaveAssets();
                if (GraphicsSettings.defaultRenderPipeline == null && !string.IsNullOrEmpty(savedDefault))
                    Debug.LogError("[Unity.SpaceGod] Could not put back the default render pipeline " + savedDefault);
            }
        }

        /// <summary>
        /// Batch-mode entry point: build the URP player and then the HDRP one beside it (not with <c>-skipHdrp</c>),
        /// writing the scenes first only if they are missing. Exits non-zero on failure.
        /// </summary>
        public static void SetupAndBuild()
        {
            try
            {
                BuildWindows();
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-skipHdrp") < 0) BuildWindowsHdrp();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
