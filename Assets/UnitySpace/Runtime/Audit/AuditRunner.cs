// US-T70: the Unreal copies' scripted smoke and faithfulness audit (-SpaceGodSmoke / -SpaceGodAudit, SpaceGameMode.RunSmoke
// and Runtime/Audit/FeatureAudit.cs in SpaceGod.Unreal.CSharp, Smoke/FeatureAudit.cpp in SpaceGod.Unreal.Cpp), run in the
// Unity copy so all three log the same AUDIT ids and can be compared row for row (docs/PARITY.md "Audit"). New file; the
// game itself does not use it.
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Unity.Shared.Core;
using Unity.SpaceGod.Input;
using UnityEngine;

namespace Unity.SpaceGod.Audit
{
    /// <summary>
    /// Runs the smoke and the audit against the game as Unity runs it. Under Unreal both are a straight-line script that
    /// steps the player loop itself (<c>Frames(n)</c>); Unity owns its loop, so the script runs on a worker thread and
    /// hands over to Unity for each frame it asks for: the worker and the main thread take turns, never both at once, so
    /// the script still reads and writes the game between frames exactly as the Unreal one does. What must run on Unity's
    /// main thread (the engine's API: Time.timeScale, the audio listener, settings that apply to the engine, saving and
    /// loading) goes through <see cref="OnMain{T}"/>. Each frame is a real Unity frame on a fixed 1/60 s
    /// (Time.captureDeltaTime and the app's own dt), the Unreal smoke's step; the scripted device's edges are cleared
    /// after it, as SpaceGameMode.Step does.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class AuditRunner : MonoBehaviour
    {
        public const float Frame = 1f / 60f;
        const string Log = "LogSpaceGod";

        /// <summary>-SpaceGodSmoke or -SpaceGodAudit is on the command line (the audit runs after the smoke).</summary>
        public static readonly bool Requested = Bootstrap.HasCommandLineFlag("-SpaceGodSmoke") || Bootstrap.HasCommandLineFlag("-SpaceGodAudit");

        /// <summary>The app's frame seconds while the script runs (null otherwise): the Unreal smoke's fixed step.</summary>
        public static float? Dt => instance != null ? Frame : (float?)null;

        static AuditRunner instance;
        static readonly object logLock = new object();
        static StreamWriter logFile;

        readonly SemaphoreSlim toWorker = new SemaphoreSlim(0), toMain = new SemaphoreSlim(0);
        ScriptedInputDevice script;
        UnitySpaceApp app;
        Thread worker;
        int framesLeft;
        Action mainAction;
        Exception mainError;
        volatile bool done;
        bool started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void FromCommandLine()
        {
            if (!Requested) return;
            // The smoke run's profile (and its saves) in a scratch folder, never the player's own (SpaceGameMode's
            // Saved/SmokeProfiles); fresh each run, so every run starts from the same nothing.
            var root = Path.Combine(Application.persistentDataPath, "SmokeProfiles", "Audit");
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            Directory.CreateDirectory(root);
            Profiles.UseRoot(root);
            string path = Bootstrap.CommandLineValue("-SpaceGodAuditLog");
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                logFile = new StreamWriter(path, false) { AutoFlush = true };
            }
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            var go = new GameObject("SpaceGodAudit");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<AuditRunner>();
            // 60 frames a second of real time too, so what the original times by Time.unscaledTime (a number key's double
            // tap within 0.45 s, the picker's and the galaxy map's 0.25 s caches) sees the Unreal smoke's 1/60 s a frame.
            Time.captureDeltaTime = Frame;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }

        /// <summary>One line of the run's log: Unity's player log and -SpaceGodAuditLog=&lt;file&gt;, in the Unreal log's
        /// words (<c>LogSpaceGod: ...</c>), which tools/audit.ps1 reads.</summary>
        public static void Line(string message)
        {
            Debug.Log(Log + ": " + message);
            lock (logLock) logFile?.WriteLine(Log + ": " + message);
        }

        // ---------------------------------------------------------------- the main thread

        void Update()
        {
            if (started) return;
            app = Bootstrap.App;
            if (app == null) return;
            // Before the app's first scripted Update: the keys and the mouse are the script's from here on.
            script = new ScriptedInputDevice();
            app.Input.Device = script;
        }

        void LateUpdate()
        {
            if (app == null || done) return;
            if (!started)
            {
                started = true;
                // Again now: the profile's frame-rate cap and VSync were put into effect at start-up after FromCommandLine.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
                Line($"UnitySpaceApp up: {app.Galaxy.Systems.Count} systems, {app.Catalog.Count} bodies in {app.ActiveSystem.Name}, " +
                    $"{app.Fleet.Ships.Count} ships (Unity {Application.unityVersion}, frame dt {Time.unscaledDeltaTime.ToString("0.#####", CultureInfo.InvariantCulture)} s)");
                worker = new Thread(Run) { IsBackground = true, Name = "SpaceGodAudit" };
                worker.Start();
                Serve();
                return;
            }
            // The frame the script asked for has run: its edges go, as after SpaceGameMode.Step.
            script.EndFrame();
            if (--framesLeft > 0) return;
            Serve();
        }

        /// <summary>Hands the turn to the worker and serves what it asks of the main thread, until it asks for frames or
        /// is done.</summary>
        void Serve()
        {
            while (true)
            {
                toWorker.Release();
                toMain.Wait();
                if (mainAction != null)
                {
                    var a = mainAction;
                    mainAction = null;
                    try { a(); mainError = null; }
                    catch (Exception e) { mainError = e; }
                    continue;
                }
                if (done)
                {
                    Line("SpaceGodSmoke: done, quitting");
                    lock (logLock) { logFile?.Dispose(); logFile = null; }
                    Application.Quit();
                }
                return;
            }
        }

        // ---------------------------------------------------------------- the worker's side

        /// <summary>Runs <paramref name="n"/> frames of the game (SpaceGameMode.Frames).</summary>
        void Frames(int n)
        {
            if (n <= 0) return;
            framesLeft = n;
            toMain.Release();
            toWorker.Wait();
        }

        /// <summary>Runs <paramref name="f"/> on Unity's main thread between frames and returns what it gave.</summary>
        public static T OnMain<T>(Func<T> f)
        {
            var r = instance;
            if (r == null || Thread.CurrentThread != r.worker) return f();
            T value = default;
            r.mainAction = () => value = f();
            r.toMain.Release();
            r.toWorker.Wait();
            if (r.mainError != null) throw new InvalidOperationException(r.mainError.Message, r.mainError);
            return value;
        }

        public static void OnMain(Action a) => OnMain(() => { a(); return 0; });

        void Run()
        {
            toWorker.Wait();
            try
            {
                RunSmoke();
                if (Bootstrap.HasCommandLineFlag("-SpaceGodAudit")) FeatureAudit.Run(app, script, Frames);
            }
            catch (Exception e)
            {
                Line("SpaceGodSmoke: failed: " + e);
            }
            done = true;
            toMain.Release();
        }

        // ---------------------------------------------------------------- the smoke (SpaceGameMode.RunSmoke)

        void RunSmoke()
        {
            // (The Unreal smoke runs the UI's own smoke first, the main menu's pages and New Game; here the menu is skipped
            // straight into a new game, as a co-op or -continue run does.)
            var inv = CultureInfo.InvariantCulture;
            var t = System.Diagnostics.Stopwatch.StartNew();
            Frames(10);
            Report($"10 frames in {t.ElapsedMilliseconds} ms");

            // Time warp: '.' one step faster.
            string warpBefore = app.Clock.Warp.Label;
            script.Tap(Key.Period);
            Frames(1);
            Line($"SpaceGodSmoke: '.' warp {warpBefore} -> {app.Clock.Warp.Label}");

            // Free swim: W held for 30 frames, then a right-drag look.
            var from = app.SpaceCamera.ScenePosition;
            script.Hold(Key.W);
            Frames(30);
            script.Release(Key.W);
            double moved = (app.SpaceCamera.ScenePosition - from).Length;
            var rotBefore = app.SpaceCamera.Rotation;
            script.HoldButton(MouseButton.Right);
            for (int i = 0; i < 15; i++)
            {
                script.MouseDelta = new Vector2(12f, 0f);
                Frames(1);
            }
            script.ReleaseButton(MouseButton.Right);
            Frames(1);
            float turned = Quaternion.Angle(rotBefore, app.SpaceCamera.Rotation);
            Line(string.Format(inv, "SpaceGodSmoke: camera moved {0:G4} units (W, {1}), turned {2:F1} deg (right-drag)",
                moved, app.SpaceCamera.Mode, turned));

            // Number key 3 twice: select Earth and focus on it.
            script.Tap(Key.Digit3);
            Frames(1);
            script.Tap(Key.Digit3);
            Frames(90);
            Line($"SpaceGodSmoke: '3' x2 selected {Describe(app.Selected)}, camera {app.SpaceCamera.Mode} on {Describe(app.SpaceCamera.FocusTarget)}");

            // Save, run on, load: the clock comes back to the saved moment.
            double savedJd = app.Clock.Jd;
            int savedShips = app.Fleet.Ships.Count;
            bool saved = OnMain(() => app.SaveTo("smoke", "US-T26 smoke"));
            Frames(60);
            double ranOn = app.Clock.Jd;
            bool loaded = OnMain(() => app.LoadFrom("smoke"));
            Frames(2);
            Line(string.Format(inv,
                "SpaceGodSmoke: save {0}, load {1}: JD saved {2:F6}, ran on to {3:F6}, back to {4:F6} (|d| = {5:G3} s); ships {6} -> {7}",
                saved ? "ok" : "FAILED", loaded ? "ok" : "FAILED", savedJd, ranOn, app.Clock.Jd, Math.Abs(app.Clock.Jd - savedJd) * 86400.0,
                savedShips, app.Fleet.Ships.Count));
            Report($"smoke done in {t.ElapsedMilliseconds} ms");
        }

        string Describe(Target target) =>
            target.IsBody ? "body " + app.Catalog[target.Body].Name : target.IsShip ? "ship " + target.Ship.Name : target.Kind.ToString();

        void Report(string label)
        {
            var inv = CultureInfo.InvariantCulture;
            var cam = app.SpaceCamera.ScenePosition;
            var (frame, fov, near) = OnMain(() => (Time.frameCount, app.UnityCamera.fieldOfView, app.UnityCamera.nearClipPlane));
            Line(string.Format(inv,
                "[{0}] {1}: JD {2:F6} = {3:yyyy-MM-dd HH:mm:ss} UTC, warp {4}; camera scene ({5:G6}, {6:G6}, {7:G6}) {8}, fov {9:F1}, near {10:G3}; toast '{11}'",
                frame, label, app.Clock.Jd, app.Clock.Utc, app.Clock.Warp.Label, cam.X, cam.Y, cam.Z, app.SpaceCamera.Mode,
                fov, near, app.Toast));
        }
    }
}
