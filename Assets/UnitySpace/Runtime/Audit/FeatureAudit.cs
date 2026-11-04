// US-T70: SpaceGod.Unreal.CSharp's Runtime/Audit/FeatureAudit.cs, unchanged but for two things the Unity copy needs: the log
// line goes through AuditRunner.Line (Unreal's UnrealLogger there), and Time.timeScale through TimeScale (Unity's API is the main
// thread's; AuditRunner runs this script on a worker between frames). Same probes, same order, same ids and detail text.
using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.Input;

namespace Unity.SpaceGod.Audit
{
    /// <summary>
    /// US-T48 faithfulness audit (<c>-SpaceGodAudit</c>, tools/play.ps1 -Mode audit): after the smoke run, presses the
    /// original's bindings (Core/ControlsHelp.cs) on the scripted device, one feature at a time, and checks the app state
    /// the original's code path changes. Logs <c>AUDIT &lt;id&gt; PASS|FAIL: detail</c>; the C++ track's
    /// Smoke/FeatureAudit.cpp runs the same probes in the same order with the same ids, so the two logs line up.
    /// Each probe puts back what it changed, so one failing does not cascade into the next.
    /// </summary>
    public static class FeatureAudit
    {
        const string Log = "LogSpaceGod";

        static UnitySpaceApp app;
        static ScriptedInputDevice kb;
        static Action<int> frames;
        static int pass, fail;

        public static void Run(UnitySpaceApp theApp, ScriptedInputDevice device, Action<int> stepFrames)
        {
            app = theApp;
            kb = device;
            frames = stepFrames;
            pass = fail = 0;
            Probe("audio", () => SettingsAudit.Audio(Result, frames)); // US-T50
            Probe("render", () => SettingsAudit.Render(Result, frames)); // US-T51
            Probe("display", () => { DisplayAudit.Msaa(Result, frames); DisplayAudit.Modes(Result, frames); }); // US-T55, US-T56
            Probe("time", Time);
            Probe("view", View);
            Probe("screens", Screens);
            Probe("nav", Nav);
            Probe("saves", Saves);
            Probe("helm", Helm);
            Probe("practice", Practice);
            Probe("seats", Seats);
            Probe("diplomacy", Diplomacy);
            // US-T58: what the binding probes left code-present: combat at the controls with the mouse, and the player's
            // own transits (a hyperlane ridden, a jump gate gone through, an FTL crossing to another star). The last two
            // leave Sol, so they come last.
            Probe("combat", Combat);
            Probe("lane", LaneRide);
            Probe("gate", GateJump);
            Probe("ftl", FtlCrossing);
            AuditRunner.Line($"AUDIT done: {pass} PASS, {fail} FAIL");
        }

        static void Probe(string group, Action body)
        {
            try { body(); }
            catch (Exception e) { Result(group + ".exception", false, e.GetType().Name + ": " + e.Message); }
        }

        static void Result(string id, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            AuditRunner.Line($"AUDIT {id} {(ok ? "PASS" : "FAIL")}: {detail}");
        }

        /// <summary>Taps <paramref name="k"/> with <paramref name="mods"/> held, runs a frame, lets go, runs another.</summary>
        static void Tap(Key k, params Key[] mods)
        {
            foreach (var m in mods) kb.Hold(m);
            kb.Tap(k);
            frames(1);
            foreach (var m in mods) kb.Release(m);
            frames(1);
        }

        /// <summary>A key that flips a flag: pressed once it must flip, pressed again flip back. The flag is put back after.</summary>
        static void Toggle(string id, Key k, Func<bool> get, Action<bool> set, params Key[] mods)
        {
            bool before = get();
            Tap(k, mods);
            bool on = get();
            Tap(k, mods);
            bool off = get();
            set?.Invoke(before);
            Result(id, on != before && off == before, $"{before} -> {on} -> {off}");
        }

        static string Toast => app.Toast ?? "";

        /// <summary>UnityEngine.Time.timeScale, read and set on Unity's main thread (US-T70).</summary>
        static float TimeScale
        {
            get => AuditRunner.OnMain(() => UnityEngine.Time.timeScale);
            set => AuditRunner.OnMain(() => { UnityEngine.Time.timeScale = value; });
        }

        static void CloseEverything()
        {
            app.ShowHelp = app.ShowMarket = app.ShowCargo = app.ShowOutfit = app.ShowBounties = app.ShowContracts = false;
            app.ShowFleet = app.ShowTrade = app.ShowGalaxyMap = app.ShowNews = app.ShowSpawn = app.ShowSaves = app.ShowPicker = false;
            app.ScreenCleared = false;
            app.ShowHud = true;
            TimeScale = 1;
        }

        // ---------------------------------------------------------------- time

        static void Time()
        {
            bool paused = app.Clock.PauseHeld;
            Tap(Key.Space);
            bool p1 = app.Clock.PauseHeld;
            Tap(Key.Space);
            Result("time.pause", p1 != paused && app.Clock.PauseHeld == paused, $"Space: {paused} -> {p1} -> {app.Clock.PauseHeld}");

            string w0 = app.Clock.Warp.Label;
            Tap(Key.Period);
            string w1 = app.Clock.Warp.Label;
            Tap(Key.Comma);
            string w2 = app.Clock.Warp.Label;
            Result("time.warp", w1 != w0 && w2 == w0, $"'.' {w0} -> {w1}, ',' -> {w2}");
            Tap(Key.Equals);
            string w3 = app.Clock.Warp.Label;
            Tap(Key.Minus);
            Result("time.warp-alt", w3 != w0 && app.Clock.Warp.Label == w0, $"'=' {w0} -> {w3}, '-' -> {app.Clock.Warp.Label}");

            Tap(Key.T);
            double hours = Math.Abs((app.Clock.Utc - DateTime.UtcNow).TotalHours);
            Result("time.reset", hours < 1, $"T: clock {app.Clock.Utc:yyyy-MM-dd HH:mm} UTC, {hours:F2} h from now");
        }

        // ---------------------------------------------------------------- view

        static void View()
        {
            double g0 = app.ScaleGoal;
            Tap(Key.G);
            double g1 = app.ScaleGoal;
            Tap(Key.Backquote);
            Result("view.scale", g1 != g0 && app.ScaleGoal == g0, $"G {g0} -> {g1}, ` -> {app.ScaleGoal}");
            frames(60);

            Toggle("view.orbits", Key.O, () => app.ShowOrbits, v => app.ShowOrbits = v);
            Toggle("view.labels", Key.L, () => app.ShowLabels, v => app.ShowLabels = v);
            Toggle("view.lanes", Key.N, () => app.ShowLanes, v => app.ShowLanes = v);
            Toggle("view.satellites", Key.K, () => app.ShowSatellites, v => app.ShowSatellites = v);
            Toggle("view.reorient", Key.R, () => app.AutoReorient, null);

            string s0 = app.Sky.Current.Name;
            Tap(Key.K, Key.LeftShift);
            string s1 = app.Sky.Current.Name;
            Tap(Key.K, Key.LeftShift, Key.LeftAlt);
            Result("view.sky", s1 != s0 && app.Sky.Current.Name == s0, $"Shift+K '{s0}' -> '{s1}', Shift+Alt+K -> '{app.Sky.Current.Name}'");
        }

        // ---------------------------------------------------------------- screens and panels

        static void Screens()
        {
            CloseEverything();
            Toggle("ui.help", Key.H, () => app.ShowHelp, v => app.ShowHelp = v);
            Toggle("ui.help-f1", Key.F1, () => app.ShowHelp, v => app.ShowHelp = v);
            Toggle("ui.hud", Key.U, () => app.ShowHud, v => app.ShowHud = v);
            Toggle("ui.clear-screen", Key.F11, () => app.ScreenCleared, v => app.ScreenCleared = v);
            Toggle("ui.market", Key.M, () => app.ShowMarket, v => app.ShowMarket = v);
            Toggle("ui.outfit", Key.F2, () => app.ShowOutfit, v => app.ShowOutfit = v);
            Toggle("ui.bounties", Key.F4, () => app.ShowBounties, v => app.ShowBounties = v);
            Toggle("ui.contracts", Key.F7, () => app.ShowContracts, v => app.ShowContracts = v);
            Toggle("ui.fleet", Key.F10, () => app.ShowFleet, v => app.ShowFleet = v);
            Toggle("ui.trade-desk", Key.Backslash, () => app.ShowTrade, v => app.ShowTrade = v);
            Toggle("ui.galaxy-map", Key.LeftBracket, () => app.ShowGalaxyMap, v => app.ShowGalaxyMap = v);
            Toggle("ui.news", Key.X, () => app.ShowNews, v => app.ShowNews = v);
            Toggle("ui.spawn", Key.V, () => app.ShowSpawn, v => app.ShowSpawn = v);
            Toggle("ui.saves", Key.F6, () => app.ShowSaves, v => app.ShowSaves = v);
            Toggle("ui.picker", Key.F9, () => app.ShowPicker, v => app.ShowPicker = v);
            CloseEverything();

            // Esc backs out (panels, focus, selection) and then opens the pause menu, which stops time; Esc again resumes.
            int presses = 0;
            while (TimeScale != 0 && presses < 6) { Tap(Key.Escape); presses++; }
            bool opened = TimeScale == 0;
            Tap(Key.Escape);
            bool resumed = TimeScale != 0;
            Result("ui.pause-menu", opened && resumed, $"Esc x{presses} opened: {opened}, Esc resumed: {resumed}");
            CloseEverything();
        }

        // ---------------------------------------------------------------- navigation

        static string Describe(Target t) =>
            t.IsBody ? "body " + app.Catalog[t.Body].Name : t.IsShip ? "ship " + t.Ship.Name : t.Kind.ToString();

        /// <summary>Runs frames until the camera's flight to a focus ends (at most 300).</summary>
        static void Settle()
        {
            for (int i = 0; i < 300 && app.SpaceCamera.Mode == Cameras.SpaceCamera.CameraMode.Travel; i++) frames(1);
        }

        static void Nav()
        {
            Tap(Key.Home);
            Settle();
            Result("nav.home", app.Selected.IsBody && app.Selected.Body == 0 && app.SpaceCamera.Mode == Cameras.SpaceCamera.CameraMode.Focus,
                $"Home: selected {Describe(app.Selected)}, camera {app.SpaceCamera.Mode}");

            Tap(Key.Digit3);
            string sel = Describe(app.Selected);
            Tap(Key.F);
            Settle();
            Result("nav.number-focus", sel == "body Earth" && app.SpaceCamera.Mode == Cameras.SpaceCamera.CameraMode.Focus && Describe(app.SpaceCamera.FocusTarget) == "body Earth",
                $"3: {sel}; F: camera {app.SpaceCamera.Mode} on {Describe(app.SpaceCamera.FocusTarget)}");

            Tap(Key.Tab);
            string first = Describe(app.Selected);
            Tap(Key.Tab);
            string second = Describe(app.Selected);
            Result("nav.cycle-ships", app.Selected.IsShip && first != second, $"Tab: {first}, Tab: {second}");

            string toast = Toast;
            var mode = app.SpaceCamera.Mode;
            var focus = Describe(app.SpaceCamera.FocusTarget);
            Tap(Key.B);
            frames(10);
            bool handled = Toast != toast || app.SpaceCamera.Mode != mode || Describe(app.SpaceCamera.FocusTarget) != focus;
            Result("nav.watch-battle", handled, $"B: camera {app.SpaceCamera.Mode} on {Describe(app.SpaceCamera.FocusTarget)}; toast '{Toast}'");
            CloseEverything();
        }

        // ---------------------------------------------------------------- saves

        static void Saves()
        {
            string toast = Toast;
            Tap(Key.F5);
            string saved = Toast;
            double jd = app.Clock.Jd;
            frames(30);
            Tap(Key.F8);
            frames(2);
            Result("saves.quicksave", saved != toast && saved.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0, $"F5: toast '{saved}'");
            Result("saves.quickload", Math.Abs(app.Clock.Jd - jd) * 86400 < 3600, $"F8: JD back to {app.Clock.Jd:F6} (saved {jd:F6}); toast '{Toast}'");
            CloseEverything();
        }

        // ---------------------------------------------------------------- helm

        static Ship PilotedShip => app.Piloted.Ship;

        static bool SelectAShip()
        {
            for (int i = 0; i < 12 && !app.Selected.IsShip; i++) Tap(Key.Tab);
            return app.Selected.IsShip;
        }

        static void Helm()
        {
            if (!SelectAShip()) { Result("helm.pilot", false, "Tab found no ship to select"); return; }
            string name = app.Selected.Ship.Name;
            Tap(Key.P);
            frames(5);
            Result("helm.pilot", app.IsPiloting && PilotedShip?.Name == name, $"P on {name}: piloting {app.IsPiloting} ({PilotedShip?.Name}); toast '{Toast}'");
            if (!app.IsPiloting) return;

            var from = app.SpaceCamera.ScenePosition;
            kb.Hold(Key.W);
            frames(60);
            kb.Release(Key.W);
            frames(1);
            double moved = (app.SpaceCamera.ScenePosition - from).Length;
            Result("helm.thrust", moved > 0, $"W held 60 frames: chase camera moved {moved:G4} units");

            string toast = Toast;
            Tap(Key.F);
            string fa = Toast;
            Tap(Key.F);
            Result("helm.flight-assist", fa != toast && fa.IndexOf("assist", StringComparison.OrdinalIgnoreCase) >= 0, $"F: toast '{fa}', F: '{Toast}'");

            bool silent = PilotedShip.SilentRunning;
            Tap(Key.F3);
            bool s1 = PilotedShip.SilentRunning;
            Tap(Key.F3);
            Result("helm.silent-running", s1 != silent && PilotedShip.SilentRunning == silent, $"F3: {silent} -> {s1} -> {PilotedShip.SilentRunning}");

            Toggle("helm.cargo", Key.I, () => app.ShowCargo, v => app.ShowCargo = v);

            Tap(Key.Escape);
            bool menu = TimeScale == 0;
            Tap(Key.Escape);
            Result("helm.esc-pause-menu", menu && TimeScale != 0 && app.IsPiloting, $"Esc at the helm opened the pause menu: {menu}; still piloting: {app.IsPiloting}");
            CloseEverything();

            Tap(Key.P);
            frames(5);
            Result("helm.release", !app.IsPiloting, $"P again: piloting {app.IsPiloting}; toast '{Toast}'");
        }

        static void KeyToast(string id, Key k, params Key[] mods)
        {
            string toast = Toast;
            Tap(k, mods);
            // No new toast is not a failure: the original's handler can be silent for this hull (no lane left, no
            // phasers, nothing to lock); logged as INFO for comparing the two tracks' logs line by line.
            if (Toast != toast) Result(id, true, $"{k}: toast '{Toast}'");
            else AuditRunner.Line($"AUDIT {id} INFO: {k}: no new toast ('{Toast}')");
        }

        static void Practice()
        {
            Tap(Key.V, Key.LeftShift);
            frames(5);
            Result("helm.practice-fighter", app.IsPiloting && PilotedShip != null && PilotedShip.Name.Contains("Practice"),
                $"Shift+V: piloting {app.IsPiloting} ({PilotedShip?.Name} {PilotedShip?.Spec?.Class}); toast '{Toast}'");
            if (app.IsPiloting)
            {
                string toast = Toast;
                Tap(Key.PageDown);
                Result("helm.ftl-plot", Toast != toast || app.PlottedCourse != null, $"PgDn: course {(app.PlottedCourse != null ? "plotted" : "none")}; toast '{Toast}'");
                // Keys the helm answers with a toast (a refusal included): the binding reaches the original's handler.
                KeyToast("helm.ftl-engage", Key.Enter);
                KeyToast("helm.ftl-autopilot", Key.End);
                KeyToast("helm.lane-reenter", Key.Y);
                KeyToast("helm.phasers", Key.Insert);
                KeyToast("helm.run-hot", Key.Insert, Key.LeftShift);
                KeyToast("helm.lock", Key.T);
                Tap(Key.P);
                frames(5);
            }
            CloseEverything();
        }

        // ---------------------------------------------------------------- seats (take charge)

        static void Seats()
        {
            if (!SelectAShip()) { Result("seats.man-gun", false, "Tab found no ship to select"); return; }
            string name = app.Selected.Ship.Name;
            string toast = Toast;
            Tap(Key.Slash);
            frames(5);
            bool seated = app.IsSeated;
            Result("seats.man-gun", seated || Toast != toast, $"/ on {name}: seated {seated}; toast '{Toast}'");
            if (seated)
            {
                Tap(Key.Backspace);
                frames(5);
                Result("seats.up-to-helm", app.IsPiloting && !app.IsSeated, $"Backspace: piloting {app.IsPiloting}, seated {app.IsSeated}; toast '{Toast}'");
                Tap(Key.P);
                frames(5);
            }
            Result("seats.back-to-command", !app.AtControls, $"P: at controls {app.AtControls}");

            toast = Toast;
            Tap(Key.Quote);
            frames(5);
            Result("seats.take-charge", app.AtControls || Toast != toast, $"': at controls {app.AtControls}; toast '{Toast}'");
            if (app.AtControls) { Tap(Key.P); frames(5); }
            CloseEverything();
        }

        // ---------------------------------------------------------------- diplomacy

        static void Diplomacy()
        {
            if (!SelectAShip()) { Result("diplomacy.hail", false, "Tab found no ship to select"); return; }
            // F12 on ships in turn until one belongs to another polity ("Nobody to hail" for one's own).
            bool hailed = false;
            for (int i = 0; i < 30 && !hailed; i++)
            {
                Tap(Key.F12);
                frames(2);
                hailed = app.ShowGalaxyMap || !Toast.StartsWith("Nobody to hail");
                if (!hailed) Tap(Key.Tab);
            }
            Result("diplomacy.hail", hailed, $"F12 on {Describe(app.Selected)}: galaxy map {app.ShowGalaxyMap}; toast '{Toast}'");
            CloseEverything();
            Tap(Key.Escape);
            CloseEverything();
        }

        // ---------------------------------------------------------------- US-T58: staging

        static string F(double v, string f) => v.ToString(f, CultureInfo.InvariantCulture);

        /// <summary>Releases whatever is at the controls (P), so the next probe starts from command.</summary>
        static void ToCommand()
        {
            for (int i = 0; i < 3 && app.AtControls; i++) { Tap(Key.P); frames(3); }
            CloseEverything();
        }

        /// <summary>
        /// Stages a hull for a probe: a home-side <paramref name="cls"/> parked at <paramref name="offsetKm"/> from
        /// <paramref name="body"/>, nose on <paramref name="forward"/> (Fleet.SpawnAt, as Shift+V and the spawn grid make
        /// theirs), then selected and taken with P. The flying from there is the player's keys.
        /// </summary>
        static Ship StageAtHelm(string name, ShipClass cls, int body, DVec3 offsetKm, DVec3 forward)
        {
            ToCommand();
            var ship = app.Fleet.SpawnAt(name, cls, app.World.Sol, body, offsetKm, forward, app.Clock.Jd);
            ship.Ports = app.World.FreeShipPorts();
            app.Select(Target.OfShip(ship));
            Tap(Key.P);
            frames(2);
            return app.IsPiloting && PilotedShip == ship ? ship : null;
        }

        static bool BattleAt(int body)
        {
            foreach (var b in app.World.War.Battles)
                if (!b.Over && b.Body == body) return true;
            return false;
        }

        // ---------------------------------------------------------------- US-T58: combat at the controls

        /// <summary>The helm's crosshair (where the mouse has swung the free aim; what the guns fire down).</summary>
        static DVec3 PilotAim => app.PilotAimDirection;

        /// <summary>A hull's whole fight left in it: hull, shields and every subsystem's hit points.</summary>
        static double Integrity(ShipCombat c)
        {
            double t = c.Hull + c.Shield;
            foreach (var s in c.Subsystems) t += Math.Max(0, s.Hp);
            return t;
        }

        /// <summary>From the piloted hull to <paramref name="t"/>, unit: in the fight's frame once there is one.</summary>
        static DVec3 DirectionTo(Ship me, Ship t)
        {
            var b = me.Battle;
            var mu = b?.UnitOf(me);
            var tu = b?.UnitOf(t);
            if (mu != null && tu != null && tu.Present)
            {
                // Led as a pilot leads a crossing target: where it will be when a round at the guns' muzzle speed gets there.
                double muzzle = 0;
                foreach (var s in mu.Combat.Subsystems)
                    if (s.Def.Weapon != null && !s.Def.Weapon.Missile) muzzle = Math.Max(muzzle, s.Def.Weapon.MuzzleSpeedKmS);
                var to = tu.Pos - mu.Pos;
                if (muzzle > 0) to += (tu.Vel - mu.Vel) * (to.Length / muzzle);
                return to.Normalized;
            }
            return (app.Fleet.StateOf(t, app.Clock.Jd).HelioKm - app.PlayerHelioKm()).Normalized;
        }

        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * 180 / Math.PI;

        /// <summary>
        /// One frame of the mouse swinging the crosshair toward <paramref name="want"/>: the error across the aim's right and
        /// up axes, turned into pixels at the look sensitivity (half of it a frame), the signs those the calibration found.
        /// </summary>
        static void SteerMouse(DVec3 want, int signX, int signY)
        {
            var aim = PilotAim.Normalized;
            var right = DVec3.Cross(aim, app.PilotUp).Normalized;
            var up = DVec3.Cross(right, aim).Normalized;
            double perPx = Cameras.SpaceCamera.LookSensitivity * Math.PI / 180;
            double x = Math.Max(-120, Math.Min(120, 0.5 * DVec3.Dot(want, right) / perPx));
            double y = Math.Max(-120, Math.Min(120, 0.5 * DVec3.Dot(want, up) / perPx));
            kb.MouseDelta = new UnityEngine.Vector2((float)(signX * x), (float)(signY * y));
            frames(1);
        }

        static void Combat()
        {
            ToCommand();
            Tap(Key.V, Key.LeftShift);
            frames(3);
            var me = app.IsPiloting ? PilotedShip : null;
            if (me == null) { Result("combat.mouse-aim", false, "Shift+V: no practice fighter at the helm"); return; }
            // A pirate freighter 1.5 km out, some 18 degrees off the nose (spawned as the spawn grid's call does).
            double jd = app.Clock.Jd;
            var fwd = app.PilotForward;
            var side = DVec3.Cross(fwd, app.PilotUp).Normalized;
            var target = app.Fleet.SpawnAt("Audit Target", ShipClass.Freighter, app.World.Pirates, app.PilotFrameBody, app.PilotOffsetKm + fwd * 1.5 + side * 0.5, fwd, jd);
            target.Ports = app.World.FreeShipPorts();
            frames(2);

            // Which way the mouse swings the crosshair: one frame of each axis.
            var a0 = PilotAim.Normalized;
            var r0 = DVec3.Cross(a0, app.PilotUp).Normalized;
            var u0 = DVec3.Cross(r0, a0).Normalized;
            kb.MouseDelta = new UnityEngine.Vector2(20, 0);
            frames(1);
            int signX = DVec3.Dot(PilotAim - a0, r0) >= 0 ? 1 : -1;
            var a1 = PilotAim.Normalized;
            kb.MouseDelta = new UnityEngine.Vector2(0, 20);
            frames(1);
            int signY = DVec3.Dot(PilotAim - a1, u0) >= 0 ? 1 : -1;

            double before = AngleDeg(PilotAim, DirectionTo(me, target));
            int steps = 0;
            while (steps < 180 && AngleDeg(PilotAim, DirectionTo(me, target)) > 0.5) { SteerMouse(DirectionTo(me, target), signX, signY); steps++; }
            double after = AngleDeg(PilotAim, DirectionTo(me, target));
            Result("combat.mouse-aim", after < 1, $"mouse: crosshair {F(before, "F1")} -> {F(after, "F2")} deg off the {target.Name} in {steps} frames");

            // The target selected (what a left-click on it does in the god view), T locks it; the left button opens fire
            // (weapons free on a lone merchant), and held down the guns fire down the crosshair, the mouse keeping it on the target.
            app.Select(Target.OfShip(target));
            Tap(Key.T);
            string locked = Describe(app.SpaceCamera.PilotLockTarget);
            double whole = Integrity(target.Combat), least = whole, heat = 0, worst = 0, range = 0;
            string fireToast = "";
            int held = 0, frame = 0;
            bool trigger = false;
            // The click opens the fight, which puts the two sides at their stations some km apart (War.TryStartPlayerAttack):
            // the player runs in on W, under cruise (J) while far out, and inside the guns' reach fires in bursts, as a
            // player does: held until the guns run hot, let go until they cool, the mouse keeping the crosshair on the lead.
            kb.Hold(Key.W);
            double nearest = double.MaxValue, closest = double.MaxValue, first = -1;
            bool cruising = false;
            for (; frame < 3600; frame++)
            {
                var unit = me.Battle?.UnitOf(me);
                var tu = me.Battle?.UnitOf(target);
                double to = unit != null && tu != null ? (tu.Pos - unit.Pos).Length : 0;
                bool far = to > 3, inReach = unit != null && to < 1.5;
                if (far != cruising) { if (far) kb.Hold(Key.J); else kb.Release(Key.J); cruising = far; }
                bool hot = unit != null && (unit.Overheated || unit.Heat > 0.8);
                bool cool = unit == null || unit.Heat < 0.3;
                bool want = unit == null ? frame < 2 : inReach && (trigger ? !hot : cool);
                if (trigger && !want) { kb.ReleaseButton(MouseButton.Left); trigger = false; }
                else if (!trigger && want) { kb.HoldButton(MouseButton.Left); trigger = true; }
                SteerMouse(DirectionTo(me, target), signX, signY);
                if (trigger) held++;
                if (fireToast.Length == 0 && me.Battle != null) fireToast = Toast;
                unit = me.Battle?.UnitOf(me);
                tu = me.Battle?.UnitOf(target);
                if (unit != null)
                {
                    heat = Math.Max(heat, unit.Heat);
                    if (trigger && inReach) worst = Math.Max(worst, AngleDeg(unit.PlayerAim, DirectionTo(me, target)));
                    if (tu != null) { range = (tu.Pos - unit.Pos).Length; nearest = Math.Min(nearest, range); if (first < 0) first = range; }
                    // The guns' rounds (this side's, in flight): how close any came to the target's hull.
                    if (tu != null)
                        foreach (var r in me.Battle.Rounds)
                            if (r.Side == unit.Side && !r.Gone) closest = Math.Min(closest, Math.Max(0, (r.Pos - tu.Pos).Length - tu.Radius));
                }
                least = Math.Min(least, Integrity(tu?.Combat ?? target.Combat));
                if (target.Destroyed || (held > 120 && least < whole && closest < 0.1)) break;
                if (me.Destroyed || !app.IsPiloting) break;
            }
            kb.Release(Key.J);            if (trigger) kb.ReleaseButton(MouseButton.Left);
            kb.Release(Key.W);
            frames(1);
            Result("combat.mouse-fire", me.Battle != null && heat > 0 && least < whole && closest < 0.1,
                $"T: locked {locked}; left button held {held} of {Math.Min(frame + 1, 3600)} frames: fight {(me.Battle != null ? "on" : "none")}, gun heat {F(heat, "F2")}, " +
                $"crosshair within {F(worst, "F2")} deg of the lead, {F(nearest == double.MaxValue ? 0 : nearest, "F2")} to {F(range, "F2")} km off, first {F(first, "F2")} km, rounds to {F(closest == double.MaxValue ? -1 : closest, "F3")} km of its hull, the {target.Name} {F(whole, "F0")} -> {F(least, "F0")}{(target.Destroyed ? " (destroyed)" : "")}; toast '{fireToast}'");
            ToCommand();
        }

        // ---------------------------------------------------------------- US-T58: the player's own transits

        /// <summary>
        /// A hyperlane (U.SPC-T41): the practice fighter staged just outside a lane's on-ramp ring, flown in on W, rides it
        /// (Fleet.JoinLane, the clock compressed) to the far ramp and comes off there.
        /// </summary>
        static void LaneRide()
        {
            ToCommand();
            var lanes = app.Fleet.Lanes;
            double jd = app.Clock.Jd;
            Lane lane = null;
            foreach (var l in lanes.Lanes)
                if (lanes.Carries(l) && !l.IsGateSpur && !BattleAt(l.A) && !BattleAt(l.B)) { lane = l; break; }
            if (lane == null) { Result("transit.lane-enter", false, "no lane carrying traffic, clear of a fight"); return; }
            int at = lane.A, to = lane.B;
            var dir = lanes.RampDirection(lane, at, jd);
            var ramp = lanes.RampOffset(lane, at, jd);
            var ship = StageAtHelm("Audit Lane Fighter", ShipClass.Fighter, at, ramp + dir * (Hyperlanes.GateRadiusKm + 0.5), -dir);
            if (ship == null)
            {
                Result("transit.lane-enter", false, $"P on the staged fighter: piloting {app.IsPiloting}");
                return;
            }
            kb.Hold(Key.W);
            int f = 0;
            for (; f < 1800 && !app.RidingLane; f++) frames(1);
            kb.Release(Key.W);
            frames(1);
            bool riding = app.RidingLane;
            Result("transit.lane-enter", riding,
                $"W held {f} frames from {F(Hyperlanes.GateRadiusKm + 0.5, "F1")} km off the {app.Catalog[at].Name} ramp: riding {riding}; toast '{Toast}'");
            if (!riding) { ToCommand(); return; }

            int ride = 0;
            for (; ride < 9000 && app.RidingLane; ride++) frames(1);
            double off = (app.PlayerHelioKm() - lanes.RampHelio(lane, to, app.Clock.Jd)).Length;
            Result("transit.lane-ride", !app.RidingLane && app.IsPiloting && off < Hyperlanes.GateRadiusKm * 2 && Toast.StartsWith("Off the"),
                $"the {lane.Name} lane ridden {ride} frames to {app.Catalog[to].Name}: {F(off, "F0")} km from its ramp, piloting {app.IsPiloting}; toast '{Toast}'");
            ToCommand();
        }

        /// <summary>
        /// A jump gate (U.SPC-T8): the practice fighter staged just outside a gate's activation range, across its spur
        /// (so it is not swept into the spur's ring), flown in on W ("In range of ... Y to activate") and through on Y
        /// (GateTransit.TryPerform, Activate on the far system, the helm taken again on the hull that came out).
        /// </summary>
        static void GateJump()
        {
            ToCommand();
            double jd = app.Clock.Jd;
            var here = app.ActiveSystem;
            JumpGate gate = null;
            foreach (var g in here.Gates)
                if (g.Destination != null && g.Link != null && g.Link.Open(here, app.World.Sol, jd)) { gate = g; break; }
            if (gate == null) { Result("transit.gate", false, $"no gate open to the {app.World.Sol.Name} in {here.Name}"); return; }
            var lanes = app.Fleet.Lanes;
            var along = DVec3.UnitX;
            foreach (var s in lanes.GateSpurs)
                if (s.Gate == gate) along = lanes.RampDirection(s, s.B, jd);
            var across = DVec3.Cross(along, Math.Abs(along.Z) < 0.9 ? DVec3.UnitZ : DVec3.UnitX).Normalized;
            int root = app.Catalog.Root.Index;
            var start = gate.PositionKm - app.Ephemeris.Helio(root) + across * (JumpGate.ActivationRangeKm + 0.5);
            var ship = StageAtHelm("Audit Gate Fighter", ShipClass.Fighter, root, start, -across);
            if (ship == null)
            {
                Result("transit.gate", false, $"P on the staged fighter: piloting {app.IsPiloting}");
                return;
            }
            kb.Hold(Key.W);
            int f = 0;
            for (; f < 1800 && !Toast.StartsWith("In range of"); f++) frames(1);
            kb.Release(Key.W);
            frames(1);
            string range = Toast;
            var destination = gate.Destination;
            // Y; the toast read after the jump's own frame (the next one says the far gate is in range, as the hull comes out beside it).
            kb.Tap(Key.Y);
            frames(1);
            string jumped = Toast;
            frames(5);
            bool there = app.ActiveSystem == destination && app.IsPiloting && destination.World.Fleet.IsPresent(PilotedShip);
            Result("transit.gate", there && jumped.StartsWith("Jumped to"),
                $"W held {f} frames: '{range}'; Y: in {app.ActiveSystem.Name} (bound for {destination.Name}), piloting {app.IsPiloting} " +
                $"({PilotedShip?.Name}, in its fleet {there}); toast '{jumped}', then '{Toast}'");
            // Left at the helm: the FTL probe stages its hull beside where this one came out.
        }

        /// <summary>
        /// The helm's own FTL drive (U.SPC-T417, T420): an explorer at the helm, Page Down to plot the nearest star,
        /// End for the autopilot (out past the rim, onto the star, spooled and engaged), the crossing, and the drop-out at
        /// the far star with the helm back in hand.
        /// </summary>
        static void FtlCrossing()
        {
            // Beside the hull at the helm (the one the gate probe brought out), 50 km off; else 1 AU out from the star.
            var from = app.ActiveSystem;
            int body = app.Catalog.Root.Index;
            var at = new DVec3(0, Astro.AuKm, 0);
            var fwd = DVec3.UnitY;
            if (app.IsPiloting)
            {
                body = app.PilotFrameBody;
                fwd = app.PilotForward;
                at = app.PilotOffsetKm + DVec3.Cross(fwd, app.PilotUp).Normalized * 50;
            }
            var ship = StageAtHelm("Audit Explorer", ShipClass.Explorer, body, at, fwd);
            if (ship == null) { Result("transit.ftl-engage", false, $"P on the staged explorer: piloting {app.IsPiloting}"); return; }
            for (int i = 0; i < 12; i++)
            {
                Tap(Key.PageDown);
                var t = app.PlottedCourse?.Target;
                if (t != null && t.System != null && !t.IsBeacon && app.PlottedCourse.Flyable) break;
            }
            var course = app.PlottedCourse;
            string plotted = Toast;
            var bound = course?.Target?.System;
            Tap(Key.End);
            string engaged = Toast;
            int f = 0;
            for (; f < 12000 && !app.InFtlCruise && app.IsPiloting; f++) frames(1);
            bool cruising = app.InFtlCruise;
            Result("transit.ftl-engage", cruising && bound != null,
                $"PgDn: '{plotted}'; End: '{engaged}'; {f} frames on: crossing {cruising}; toast '{Toast}'");
            if (!cruising) { ToCommand(); return; }

            int c = 0;
            for (; c < 12000 && app.InFtlCruise; c++) frames(1);
            frames(5);
            Result("transit.ftl-arrive", !app.InFtlCruise && app.ActiveSystem == bound && app.IsPiloting && PilotedShip == ship && Toast.StartsWith("Dropped out of FTL"),
                $"{c} frames between the stars from {from.Name}: in {app.ActiveSystem.Name} (bound for {bound?.Name}), piloting {app.IsPiloting} ({PilotedShip?.Name}); toast '{Toast}'");
            ToCommand();
        }
    }
}
