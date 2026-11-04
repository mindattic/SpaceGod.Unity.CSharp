using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod
{
    /// <summary>
    /// The helm's own FTL drive, I-War 2 style (U.SPC-T417, the rules in <see cref="HelmFtl"/>). At the helm of a capital
    /// ship or a survey hull, flown out beyond the system's outer rim (canon, U.SPC-T455: no jump is possible inside a
    /// solar system's gravity wells), the nose is put on another star (the HUD names it), Enter spools the drive, and once
    /// charged the hull itself goes out into open space with the rest of the traffic between the stars
    /// (<see cref="DeepSpace"/>), the view goes over to the stars sliding by, and it drops out at the edge of that star's
    /// well, the helm back in hand. The clock runs fast enough that the charge and the crossing each take seconds to a
    /// minute or so, and slows again after. Small craft carry no drive.
    /// </summary>
    public sealed partial class UnitySpaceApp
    {
        /// <summary>The helm's FTL drive and its charge.</summary>
        public HelmFtl Ftl { get; } = new HelmFtl();

        /// <summary>The star the nose is on, that the drive could be spooled for (null: none, or a hull with no drive).</summary>
        public FtlTarget FtlPointed { get; private set; }

        /// <summary>Whether the hull at the helm is beyond its system's outer rim, clear of the gravity wells to jump.</summary>
        public bool FtlBeyondRim { get; private set; }

        /// <summary>Beyond the rim with the nose on a star, what still keeps the drive from spooling (another well, a
        /// gravity projector, the drive cooling, too little reaction mass: U.SPC-T418), or null.</summary>
        public string FtlBlocked { get; private set; }

        /// <summary>The player's hull out between the stars under its own drive, or null.</summary>
        public Cruise FtlCruise { get; private set; }

        /// <summary>Whether the player's hull is out between the stars under its own drive (the helm is back at the far end).</summary>
        public bool InFtlCruise => FtlCruise != null;

        /// <summary>The hull flown out between the stars (the same object all the way, see <see cref="Fleet.LetGo"/>).</summary>
        Ship ftlShip;

        /// <summary>The warp the clock ran at before the drive was spooled (back to it after), or -1.</summary>
        int ftlWarpBefore = -1;

        /// <summary>Whether the system bound for is already built (hidden), part way across.</summary>
        bool ftlDestinationBuilt;

        /// <summary>What there is to point at here, gathered now and then rather than every frame.</summary>
        List<FtlTarget> ftlTargets;
        StarSystem ftlTargetsFor;
        int ftlTargetsChart = -1;
        float ftlTargetsAge;

        /// <summary>The charge runs at the slowest warp that gets it over in about this many real seconds; the run out and
        /// the crossing take the settings menu's long trip compression (<see cref="FtlNavigation.TripRealSeconds"/>, U.SPC-T420).</summary>
        const double FtlSpoolRealSeconds = 8;

        /// <summary>The course plotted for the helm's FTL drive (U.SPC-T420): to a star or a beacon, picked with the nose, Page
        /// Up/Down or the galaxy map, with its ETA and fuel. Null: none.</summary>
        public FtlCourse PlottedCourse { get; private set; }

        /// <summary>The FTL autopilot that flies the plotted course: out beyond the rim, onto the star, spooled and engaged (U.SPC-T420).</summary>
        public FtlAutopilot FtlAutopilot { get; } = new FtlAutopilot();

        /// <summary>Whether the hull at the helm carries its own FTL drive, so a course can be plotted and flown for it.</summary>
        public bool CanPlotFtl => IsPiloting && !RidingLane && Piloted.Ship != null && HelmFtl.Refusal(Piloted.Ship) == null;

        /// <summary>Real seconds until the plotted course is worked out again from where the hull has got to.</summary>
        float ftlCourseAge;

        /// <summary>The leg of the crossing the clock was last set for (a new one: pulled out by a well, or spooled again past it).</summary>
        double ftlCruiseLegJd = double.NaN;

        /// <summary>Real seconds left of the charge or of the crossing at the clock's present rate.</summary>
        public double FtlSecondsLeft
        {
            get
            {
                double jd = Clock.Jd, end;
                if (InFtlCruise) end = FtlCruise.ArriveJd;
                else if (Ftl.Stage == FtlStage.Spooling) end = Ftl.ChargedJd;
                else if (FtlAutopilot.Stage == FtlAutopilotStage.Cooling) end = FtlRules.CoolUntil(Piloted.Ship);
                else if (FtlAutopilot.Stage == FtlAutopilotStage.OutRun) end = FtlAutopilot.RunEndJd;
                else return 0;
                if (double.IsInfinity(end) || double.IsNaN(end)) return 0;
                return Math.Max(0, end - jd) * Astro.SecondsPerDay / Math.Max(Clock.Warp.SimSecondsPerSecond, 1e-9);
            }
        }

        /// <summary>Where the hull is, km from the system's root.</summary>
        DVec3 PilotRootKm => Ephemeris.Helio(pilotFrameBody) + pilotOffsetKm;

        /// <summary>The system's outer rim, AU from its star: beyond it the drive can spool.</summary>
        public double FtlRimAu => HelmFtl.RimKm(ActiveSystem) / Astro.AuKm;

        /// <summary>How far the hull at the helm is from its system's star, AU.</summary>
        public double PilotFromStarAu => IsPiloting ? PilotRootKm.Length / Astro.AuKm : 0;

        // ---------------------------------------------------------------- at the helm

        /// <summary>
        /// A frame of the drive at the helm, after the hull has flown: whether it is beyond the rim, what star the nose is
        /// on, Enter to spool (again to call it off), the charge held or lost, and once charged the jump. True when the hull
        /// has left the system this frame.
        /// </summary>
        bool UpdateHelmFtl(Ship ship)
        {
            double jd = Clock.Jd;
            bool canJump = HelmFtl.Refusal(ship) == null;
            FtlBeyondRim = HelmFtl.BeyondRim(ActiveSystem, PilotRootKm);
            FtlPointed = canJump && Ftl.Stage == FtlStage.Idle ? PointedTarget(PilotRootKm, pilot.Forward) : null;
            FtlBlocked = null;
            if (FtlPointed != null && FtlBeyondRim)
            {
                HelmFtl.Bearing(FtlPointed, ActiveSystem, PilotRootKm, out double km);
                FtlBlocked = HelmFtl.WellRefusal(ActiveSystem, PilotRootKm, Galaxy) ?? HelmFtl.DriveRefusal(ship, km, jd);
            }

            // Enter is the manual charge; while the autopilot is turning onto the star it is the autopilot's to spool.
            if (Input.EngageFtl && !(FtlAutopilot.Active && Ftl.Stage != FtlStage.Spooling))
            {
                if (Ftl.Stage == FtlStage.Spooling)
                {
                    Ftl.Abort("Charge called off");
                    EndFtlWarp();
                    ShowToast("FTL charge called off");
                    return false;
                }
                if (!Ftl.Spool(ship, ActiveSystem, FtlPointed, PilotRootKm, pilot.Forward, ship.Battle != null, jd, Galaxy))
                {
                    ShowToast(Ftl.AbortReason);
                    return false;
                }
                if (ftlWarpBefore < 0) ftlWarpBefore = Clock.WarpIndex;
                Clock.SetWarp(SimClock.WarpToCover((Ftl.ChargedJd - jd) * Astro.SecondsPerDay, FtlSpoolRealSeconds, ftlWarpBefore));
                var t = Ftl.Target;
                ShowToast("FTL DRIVE SPOOLING for " + t.Name.ToUpperInvariant() + ", " + Distance(t) + "  (hold the nose on it; Enter calls it off)");
                return false;
            }

            if (Ftl.Stage != FtlStage.Spooling) return false;
            switch (Ftl.Step(ActiveSystem, PilotRootKm, pilot.Forward, ship.Battle != null, jd, Galaxy))
            {
                case FtlEvent.Aborted:
                    EndFtlWarp();
                    ShowToast(Ftl.AbortReason);
                    return false;
                case FtlEvent.Charged:
                    return EngageCruise(ship, jd);
                default:
                    return false;
            }
        }

        /// <summary>The star dead ahead of the nose, from a list of what is charted brought up to date every second or so.</summary>
        FtlTarget PointedTarget(DVec3 rootKm, DVec3 forward)
        {
            ftlTargetsAge -= Time.unscaledDeltaTime;
            if (ftlTargets == null || ftlTargetsFor != ActiveSystem || ftlTargetsChart != Galaxy.ChartVersion || ftlTargetsAge <= 0)
            {
                ftlTargets = FtlTargets.All(Galaxy, ActiveSystem);
                ftlTargetsFor = ActiveSystem;
                ftlTargetsChart = Galaxy.ChartVersion;
                ftlTargetsAge = 1f;
            }
            return HelmFtl.Pointed(ftlTargets, ActiveSystem, rootKm, forward);
        }

        /// <summary>How far <paramref name="t"/> is from the hull, light-years.</summary>
        public string Distance(FtlTarget t)
        {
            if (t == null || !IsPiloting) return "";
            HelmFtl.Bearing(t, ActiveSystem, PilotRootKm, out double km);
            return DistanceText(km);
        }

        static string DistanceText(double km)
        {
            var inv = CultureInfo.InvariantCulture;
            if (km >= 0.01 * Astro.LightYearKm) return (km / Astro.LightYearKm).ToString("0.00", inv) + " ly";
            if (km >= 0.01 * Astro.AuKm) return (km / Astro.AuKm).ToString("0.00", inv) + " AU";
            return km.ToString("N0", inv) + " km";
        }

        // ---------------------------------------------------------------- between the stars

        /// <summary>
        /// Charged, for another star: the hull itself goes out into open space under its own drive, the helm let go of
        /// for the crossing (nothing to steer between the stars yet), and the view goes over to the stars. True if it went.
        /// </summary>
        bool EngageCruise(Ship ship, double jd)
        {
            var from = ActiveSystem;
            var rootKm = PilotRootKm;
            // FTL carries momentum (U.SPC-T418): the velocity it goes in with, against the system's root, it comes out with.
            var c = Ftl.EngageInterstellar(Galaxy, from, ship, rootKm, jd, pilotVelKmS + FrameVelocityKmS(pilotFrameBody, jd) - FrameVelocityKmS(0, jd), out int wing);
            if (c == null)
            {
                EndFtlWarp();
                ShowToast(Ftl.AbortReason);
                return false;
            }
            EndSalvage(null);
            SalvageApproach = null;
            // The system left no longer sees the pilot, nor holds a patrol's hail or a chase on it.
            World.PilotedShip = null;
            World.Sensors?.UpdatePilot(null, default, false, jd);
            World.Law?.UpdatePilot(null, 0, default, default, jd);
            World.Pursuit?.ReleaseAll(jd);
            Piloted = Target.None;
            pilotLockTarget = Target.None;
            pilotLockUnit = null;
            pilotBoosting = false;
            pilotCruise = default;
            TankerDueS = -1;
            SpaceCamera.PilotLockTarget = Target.None;
            SpaceCamera.PilotView = null;
            if (CoopHelmMine) SpaceCamera.ExitFocus(); // US-T43: the holder's view only (the cruise view below is everyone's)
            Select(Target.None);

            FtlCruise = c;
            ftlShip = ship;
            ftlDestinationBuilt = false;
            // The course is flown: the crossing has it from here (U.SPC-T420).
            FtlAutopilot.Reset();
            PlottedCourse = null;
            // A long crossing compressed on the sim clock, the rest of the galaxy moving on with it (U.SPC-T420).
            ftlCruiseLegJd = c.LegJd;
            Clock.SetWarp(FtlNavigation.WarpFor(c.ArriveJd - jd, Math.Max(0, ftlWarpBefore)));
            // What the far system is drawn with starts loading now, in the background (U.SPC-T414).
            Rendering.AssetStreamer.WarmSystem(c.To);
            SetSystemVisible(false);
            Interstellar.Visible = true;
            GateFlash = 1;
            fovKick = GateFovKick;
            // Craft in the hangars, and escorts clamped to the hull (U.SPC-T473).
            int clamped = FtlCarriage.CountClamped(c.Aboard.Keys);
            int docked = c.Aboard.Count - clamped;
            string carried = docked > 0 && clamped > 0 ? " (" + docked + " docked aboard, " + clamped + " clamped to the hull)"
                : docked > 0 ? " (" + docked + " docked aboard)" : clamped > 0 ? " (" + clamped + " clamped to the hull)" : "";
            ShowToast("FTL ENGAGED for " + c.ToName.ToUpperInvariant() + ", " + c.Profile.Ly.ToString("0.00", CultureInfo.InvariantCulture) +
                      " ly through open space" + (wing == 0 ? "" : ", " + wing + " of your wing with you" + carried));
            return true;
        }

        /// <summary>
        /// A frame of the crossing: the charts follow the hull, the far system is built (hidden) half way, and once the hull
        /// has dropped out into it the helm is taken again there; lost out there, the session ends.
        /// </summary>
        void UpdateFtlCruise()
        {
            var c = FtlCruise;
            double jd = Clock.Jd;
            if (Galaxy.DeepSpace.Cruises.Contains(c))
            {
                // The galaxy is charted and built about the hull as it goes (U.SPC-T413).
                Galaxy.Viewpoint = c.PositionAt(jd);
                // A new leg (pulled out by a well or a gravity projector, or spooled again past it, U.SPC-T418): the clock
                // is compressed again for what is left of it (U.SPC-T420).
                if (c.LegJd != ftlCruiseLegJd && !c.Holding)
                {
                    ftlCruiseLegJd = c.LegJd;
                    Clock.SetWarp(FtlNavigation.WarpFor(c.ArriveJd - jd));
                    ShowToast(c.Crossing
                        ? "PULLED OUT OF FTL by " + (c.InterdictedBy ?? "a gravity well") + ": crossing it under the ordinary drive, " + FtlNavigation.Describe(c.ArriveJd - jd)
                        : "Clear of " + (c.InterdictedBy ?? "the well") + ": the drive spools again for " + c.ToName);
                }
                double total = c.ArriveJd - c.LegJd;
                double share = total > 0 && !double.IsInfinity(total) ? (jd - c.LegJd) / total : 0;
                if (!ftlDestinationBuilt && share >= 0.5 && c.To != null)
                {
                    Activate(c.To);
                    SetSystemVisible(false);
                    Interstellar.Visible = true;
                    ftlDestinationBuilt = true;
                }
                return;
            }

            var ship = ftlShip;
            var to = c.To;
            EndFtlCruise();
            if (ship == null || ship.Destroyed || to == null || !to.World.Fleet.Ships.Contains(ship))
            {
                // Destroyed out there (a fight between the stars): nothing comes out the far end.
                SetSystemVisible(true);
                FocusSystemOverview();
                ShowToast("The " + (ship?.Name ?? "ship") + " was lost between the stars");
                return;
            }
            if (ActiveSystem != to) Activate(to);
            SetSystemVisible(true);
            Interstellar.Visible = false;
            EnterPiloting(Target.OfShip(ship));
            // Out moving as it went in (U.SPC-T418), in the frame the helm now flies in.
            pilotVelKmS = c.CarriedVelocityKmS - FrameVelocityKmS(pilotFrameBody, Clock.Jd) + FrameVelocityKmS(0, Clock.Jd);
            EndFtlWarp();
            // The far side streams in behind the drop-out's own flash and fold (U.SPC-T414).
            BeginHelmGateCover();
            ShowToast("Dropped out of FTL at the edge of " + to.Name.ToUpperInvariant() + ", " +
                      DistanceText(ship.Orbit.Center.Length) + " from its star  (in from " + (c.From?.Name ?? "open space") + ")");
        }

        /// <summary>The view between the stars during the crossing: from where the hull is, looking the way it goes.</summary>
        void UpdateFtlCruiseView(float dt)
        {
            var c = FtlCruise;
            if (c == null) return;
            var heading = c.Direction.SwapYZ;
            var forward = new Vector3((float)heading.X, (float)heading.Y, (float)heading.Z);
            if (forward.sqrMagnitude < 1e-12f) forward = Vector3.forward;
            var want = Quaternion.LookRotation(forward, Mathf.Abs(forward.normalized.y) > 0.98f ? Vector3.forward : Vector3.up);
            var rotation = Quaternion.Slerp(SpaceCamera.Rotation, want, 1f - Mathf.Exp(-2.5f * dt));
            SpaceCamera.SetRotation(rotation);
            Interstellar.Visible = true;
            Interstellar.Update(c.PositionAt(Clock.Jd).Ly, rotation, 1f);
        }

        /// <summary>The crossing is over (arrived, or lost): back to the view of a system.</summary>
        void EndFtlCruise()
        {
            FtlCruise = null;
            ftlCruiseLegJd = double.NaN;
            ftlShip = null;
            ftlDestinationBuilt = false;
            Interstellar.Visible = false;
        }

        /// <summary>The clock back to the pace it had before the drive was spooled.</summary>
        void EndFtlWarp()
        {
            if (ftlWarpBefore >= 0) Clock.SetWarp(ftlWarpBefore);
            ftlWarpBefore = -1;
        }

        /// <summary>The helm let go of mid-charge or mid-run: the drive stops where it is, the autopilot lets go and the
        /// course (the hull's, from this system) is forgotten.</summary>
        void StopFtl()
        {
            bool flying = FtlAutopilot.Active;
            FtlAutopilot.Abandon(Clock.Jd);
            PlottedCourse = null;
            if (Ftl.Stage == FtlStage.Idle && !flying) return;
            Ftl.Reset();
            EndFtlWarp();
        }

        // ---------------------------------------------------------------- navigation (U.SPC-T420)

        /// <summary>
        /// Plots a course for the hull at the helm to <paramref name="target"/>, from where it is now: the run out to the rim,
        /// the charge, the light-years, any well or projector on the way, the ETA and the fuel. Said in a toast unless
        /// <paramref name="quiet"/>. True if it can be flown.
        /// </summary>
        public bool PlotFtlCourse(FtlTarget target, bool quiet = false)
        {
            if (target != null && CoopSends("ftlplot", (quiet ? "1 " : "0 ") + target.Name)) return true; // US-T43: the shared helm's course: into the frames
            if (!IsPiloting || Piloted.Ship == null)
            {
                ShowToast("Take the helm of a capital ship or an explorer (P) to plot an FTL course");
                return false;
            }
            var ship = Piloted.Ship;
            var refusal = RidingLane ? "Not from a lane: come off it first" : HelmFtl.Refusal(ship);
            if (refusal != null)
            {
                ShowToast(refusal);
                return false;
            }
            if (FtlAutopilot.Active) DisengageFtlAutopilot(null);
            PlottedCourse = FtlCourse.Plot(Galaxy, ActiveSystem, ship, PilotRootKm, target, Clock.Jd);
            ftlCourseAge = 1f;
            if (!quiet) ShowToast(CourseSummary(PlottedCourse));
            return PlottedCourse.Flyable;
        }

        /// <summary>A plotted course in a line, or why it cannot be flown.</summary>
        public static string CourseSummary(FtlCourse c)
        {
            if (c == null) return "No course plotted";
            string name = c.Target?.Name.ToUpperInvariant() ?? "nowhere";
            var inv = CultureInfo.InvariantCulture;
            if (!c.Flyable) return "No course for " + name + ": " + c.Refusal;
            return "COURSE PLOTTED for " + name + " (" + c.Target.KindName + "), " + c.Ly.ToString("0.00", inv) + " ly, ETA " +
                   FtlNavigation.Describe(c.TotalDays) + ", " + c.FuelTonnes.ToString("N0", inv) + " t of " + c.TanksTonnes.ToString("N0", inv) +
                   " t reaction mass" + (c.Interdictions.Count > 0 ? ", pulled out " + c.Interdictions.Count + "x on the way" : "") + "  (End engages the autopilot)";
        }

        /// <summary>Clears the plotted course (and lets go of the autopilot flying it).</summary>
        public void ClearFtlCourse()
        {
            if (CoopSends("ftlclear")) return; // US-T43: into the frames (the loop's own Delete key is already in them)
            if (FtlAutopilot.Active) DisengageFtlAutopilot(null);
            PlottedCourse = null;
            ShowToast("FTL course cleared");
        }

        /// <summary>Plots a course to the next star or beacon out from the one plotted (<paramref name="step"/> 1) or the one
        /// before (-1), nearest first.</summary>
        void CycleFtlCourse(int step)
        {
            if (!CanPlotFtl)
            {
                if (IsPiloting && Piloted.Ship != null) ShowToast(HelmFtl.Refusal(Piloted.Ship) ?? "Not from a lane: come off it first");
                return;
            }
            var list = FtlNavigation.Nearest(Galaxy, ActiveSystem, PilotRootKm);
            var next = FtlNavigation.Cycle(list, PlottedCourse?.Target, step);
            if (next == null)
            {
                ShowToast("No other star or beacon on the charts");
                return;
            }
            PlotFtlCourse(next);
        }

        /// <summary>
        /// Engages the FTL autopilot for the plotted course (or, with none, the star the nose is on), plotted afresh from
        /// where the hull is: it flies out beyond the rim on the clock's long trip compression, turns onto the star, spools
        /// and engages. True if it took the helm.
        /// </summary>
        public bool EngageFtlAutopilot()
        {
            if (CoopSends("ftlauto")) return true; // US-T43: into the frames (the loop's own End key is already in them)
            if (!IsPiloting || Piloted.Ship == null)
            {
                ShowToast("Take the helm of a capital ship or an explorer (P) to fly an FTL course");
                return false;
            }
            var ship = Piloted.Ship;
            if (FtlAutopilot.Active) return true;
            if (Ftl.Stage == FtlStage.Spooling)
            {
                ShowToast("The drive is already spooling for " + Ftl.Target.Name + " (Enter calls it off)");
                return false;
            }
            var target = PlottedCourse?.Target ?? FtlPointed;
            if (target == null)
            {
                ShowToast("No course plotted: put the nose on a star, Page Down for the nearest, or pick one on the galaxy map ([)");
                return false;
            }
            if (!PlotFtlCourse(target, quiet: true))
            {
                ShowToast(CourseSummary(PlottedCourse));
                return false;
            }
            double jd = Clock.Jd;
            var from = PilotRootKm;
            if (!FtlAutopilot.Engage(PlottedCourse, from, ship.Battle != null, jd))
            {
                ShowToast(FtlAutopilot.Reason);
                return false;
            }
            // Flown in the system's own frame, from the star, the stick let go of.
            pilotFrameBody = Catalog.Root.Index;
            pilot.Pos = from;
            pilot.Vel = FtlAutopilot.VelocityKmS(jd);
            pilot.AngularVelocity = DVec3.Zero;
            pilotCruise = default;
            pilotBoosting = false;
            if (ftlWarpBefore < 0) ftlWarpBefore = Clock.WarpIndex;
            var c = PlottedCourse;
            var inv = CultureInfo.InvariantCulture;
            if (FtlAutopilot.Stage == FtlAutopilotStage.OutRun)
                Clock.SetWarp(FtlNavigation.WarpFor(FtlAutopilot.RunDaysLeft(jd), ftlWarpBefore));
            ShowToast("FTL AUTOPILOT for " + c.Target.Name.ToUpperInvariant() + ": " +
                      (c.NeedsOutRun ? "out beyond the rim (" + (c.OutRunKm / Astro.AuKm).ToString("0.##", inv) + " AU, " + FtlNavigation.Describe(c.OutRunDays) + "), then " : "") +
                      "onto the star, spool and engage; ETA " + Astro.ToDateTime(c.EtaJd).ToString("yyyy-MM-dd", inv) + "  (End takes the helm back)");
            return true;
        }

        /// <summary>Aligned: waits for a drive still cooling, else starts the charge. False if the autopilot let go.</summary>
        bool BeginFtlAutopilotSpool(Ship ship, FtlAutopilot ap, double jd)
        {
            double coolUntil = FtlRules.CoolUntil(ship);
            if (coolUntil > jd)
            {
                if (ap.Stage != FtlAutopilotStage.Cooling)
                {
                    ap.Cooling();
                    if (ftlWarpBefore < 0) ftlWarpBefore = Clock.WarpIndex;
                    ShowToast("FTL drive still cooling: the autopilot holds the nose on " + ap.Course.Target.Name.ToUpperInvariant() + " and spools when it has cooled");
                }
                Clock.SetWarp(SimClock.WarpToCover((coolUntil - jd) * Astro.SecondsPerDay, FtlSpoolRealSeconds, Math.Max(0, ftlWarpBefore)));
                return true;
            }
            if (!Ftl.Spool(ship, ActiveSystem, ap.Course.Target, PilotRootKm, pilot.Forward, false, jd, Galaxy))
            {
                DisengageFtlAutopilot("FTL autopilot off: " + Ftl.AbortReason);
                return false;
            }
            ap.Spooling();
            if (ftlWarpBefore < 0) ftlWarpBefore = Clock.WarpIndex;
            Clock.SetWarp(SimClock.WarpToCover((Ftl.ChargedJd - jd) * Astro.SecondsPerDay, FtlSpoolRealSeconds, Math.Max(0, ftlWarpBefore)));
            ShowToast("FTL DRIVE SPOOLING for " + ap.Course.Target.Name.ToUpperInvariant() + ", " + Distance(ap.Course.Target) + "  (the autopilot holds the nose on it; End lets go)");
            return true;
        }

        /// <summary>The autopilot lets go of the helm where the hull is, moving as it was (<paramref name="why"/> said in a
        /// toast, unless null), and a charge it had started is called off.</summary>
        void DisengageFtlAutopilot(string why)
        {
            var ap = FtlAutopilot;
            if (!ap.Active) return;
            double jd = Clock.Jd;
            if (ap.Stage == FtlAutopilotStage.OutRun)
            {
                pilot.Pos = ap.PositionKm(jd);
                pilot.Vel = ap.VelocityKmS(jd);
            }
            if (ap.Stage == FtlAutopilotStage.Spooling && Ftl.Stage == FtlStage.Spooling) Ftl.Abort("Charge called off with the autopilot");
            ap.Disengage(why, jd);
            EndFtlWarp();
            // A fight or a chase is flown at the pace it is fought, not the pace the clock had before the trip.
            if (Piloted.Ship?.Battle != null || (World.Pursuit != null && World.Pursuit.Active)) Clock.SetWarp(0);
            ResetPilotAim();
            if (why == null) return;
            double speed = pilot.Vel.Length;
            ShowToast(why + (speed > 1 ? "  (still moving at " + speed.ToString("N0", CultureInfo.InvariantCulture) + " km/s)" : ""));
        }

        /// <summary>The plotted course worked out again, now and then, from where the hull has got to (while nothing flies it).</summary>
        void RefreshFtlCourse(Ship ship)
        {
            var c = PlottedCourse;
            if (c == null || FtlAutopilot.Active) return;
            if (c.Ship != ship || c.From != ActiveSystem)
            {
                PlottedCourse = null;
                return;
            }
            ftlCourseAge -= Time.unscaledDeltaTime;
            if (ftlCourseAge > 0) return;
            ftlCourseAge = 1f;
            PlottedCourse = FtlCourse.Plot(Galaxy, ActiveSystem, ship, PilotRootKm, c.Target, Clock.Jd);
        }

        /// <summary>The way to the plotted course's star or beacon from the hull (unit), and how far, km; false with none.</summary>
        public bool PlottedBearing(out DVec3 direction, out double km)
        {
            direction = default;
            km = 0;
            if (PlottedCourse?.Target == null || !IsPiloting || PlottedCourse.From != ActiveSystem) return false;
            direction = PlottedCourse.Bearing(PilotRootKm, out km);
            return km > 0;
        }

        /// <summary>
        /// A frame with the autopilot at the helm: the run out flown closed-form on the sim clock, the turn onto the star, and
        /// the charge held on it until the drive engages; a fight, hunters closing, the drives knocked out or End let go of
        /// it. True if it flew the hull this frame (the stick is not read), false if it let go.
        /// </summary>
        bool UpdateFtlAutopilot(Ship ship, float dt)
        {
            var ap = FtlAutopilot;
            if (!ap.Active) return false;
            double jd = Clock.Jd;
            if (Input.ToggleFtlAutopilot)
            {
                DisengageFtlAutopilot("FTL autopilot off: the helm is yours");
                return false;
            }
            if (ship.Battle != null)
            {
                DisengageFtlAutopilot("FTL autopilot off: battle stations");
                return false;
            }
            if (World.Pursuit != null && World.Pursuit.Active)
            {
                DisengageFtlAutopilot("FTL autopilot off: hunters closing");
                return false;
            }
            if (PilotStunSeconds > 0)
            {
                DisengageFtlAutopilot("FTL autopilot off: the drives are knocked out");
                return false;
            }

            if (ap.Step(false, jd) == FtlAutopilotEvent.Arrived)
            {
                pilot.Pos = ap.RunToKm;
                pilot.Vel = DVec3.Zero;
                // The turn is flown at the pace the clock had before.
                Clock.SetWarp(Math.Max(0, ftlWarpBefore));
                ShowToast("Beyond " + ActiveSystem.Name + "'s rim: turning onto " + ap.Course.Target.Name.ToUpperInvariant());
            }
            switch (ap.Stage)
            {
                case FtlAutopilotStage.OutRun:
                    pilot.Pos = ap.PositionKm(jd);
                    pilot.Vel = ap.VelocityKmS(jd);
                    SetPilotFacing(ap.RunDirection);
                    break;
                case FtlAutopilotStage.Aligning:
                {
                    pilot.Vel = DVec3.Zero;
                    var forward = pilot.Forward;
                    var turned = ap.Turn(ref forward, PilotRootKm, FtlAutopilot.TurnDegPerSecond * dt);
                    SetPilotFacing(forward);
                    if (turned != FtlAutopilotEvent.Aligned) break;
                    if (!BeginFtlAutopilotSpool(ship, ap, jd)) return false;
                    break;
                }
                case FtlAutopilotStage.Cooling:
                {
                    // Held, nose on the star, until the drive has cooled; then it spools.
                    pilot.Vel = DVec3.Zero;
                    if (jd < FtlRules.CoolUntil(ship)) break;
                    if (!BeginFtlAutopilotSpool(ship, ap, jd)) return false;
                    break;
                }
                default:
                    // Spooling: held still with the nose on the star; the charge engages it (UpdateHelmFtl, below).
                    pilot.Vel = DVec3.Zero;
                    break;
            }
            pilot.AngularVelocity = DVec3.Zero;
            pilotCruise = default;
            ResetPilotAim();
            SpaceCamera.PilotView = PilotViewRotation(PilotControlMode);

            // What everyone else sees of it, and what it sees: its drive is lit on the run out.
            World.Sensors?.UpdatePilot(ship, Ephemeris.HelioAt(pilotFrameBody, jd) + pilotOffsetKm, ap.Stage == FtlAutopilotStage.OutRun, jd);
            World.Law.UpdatePilot(ship, pilotFrameBody, pilotOffsetKm, pilotForward, jd);
            World.Outlaws.UpdatePilot(ship, pilotFrameBody, pilotOffsetKm, pilotForward, jd);
            World.Contracts?.UpdatePilot(ship, pilotFrameBody, pilotOffsetKm, jd);
            UpdatePilotBattle(ship, dt);
            UpdatePursuit(ship, dt);
            if (UpdateHelmFtl(ship)) return true;
            if (ap.Stage == FtlAutopilotStage.Spooling && Ftl.Stage != FtlStage.Spooling)
            {
                // The charge was lost or called off (Enter): the autopilot goes with it.
                string why = Ftl.AbortReason;
                ap.Disengage(why, jd);
                EndFtlWarp();
                ResetPilotAim();
                ShowToast("FTL autopilot off: " + (why ?? "the charge was lost"));
            }
            ShowWarNotices();
            return true;
        }

        /// <summary>The hull's nose put on <paramref name="forward"/> (ecliptic, unit), its roof kept as square to it as it was.</summary>
        void SetPilotFacing(DVec3 forward)
        {
            if (forward.LengthSquared <= 0) return;
            var f = forward.Normalized;
            var up = pilot.Up - f * DVec3.Dot(pilot.Up, f);
            pilot.Forward = f;
            pilot.Up = up.LengthSquared > 1e-6 ? up.Normalized : PilotState.LevelUp(f);
        }
    }
}
