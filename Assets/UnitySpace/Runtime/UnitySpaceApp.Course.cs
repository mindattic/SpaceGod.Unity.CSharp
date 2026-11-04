using System.Globalization;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod
{
    /// <summary>
    /// A plotted course over the gates (U.SPC-T516): a trip to a system several gates away is a list of real legs
    /// (<see cref="TravelCourse"/>), each flown across the system the hull is in to that system's gate for the next link
    /// (by the hyperlanes where they run to it) and gone through. At the helm the pilot flies it by hand (the HUD names the
    /// next gate and how far it is) or hands it to the crew (the autopilot, <see cref="EngageCourseAutopilot"/>); the god view's
    /// followed ship is given the course the same way and the view goes with it, system to system. Nothing jumps a hull to the
    /// far system.
    /// </summary>
    public sealed partial class UnitySpaceApp
    {
        /// <summary>The course being flown, or null.</summary>
        public TravelCourse Course { get; private set; }

        /// <summary>The hull the course was plotted for: another hull's helm, or a hull lost, ends it.</summary>
        Ship courseShip;

        /// <summary>Whether the crew flies the course (the autopilot, or a followed ship sent on it) rather than the pilot.</summary>
        public bool CourseByCrew { get; private set; }

        /// <summary>Whether the view goes with the crew's hull from system to system.</summary>
        bool courseFollow;

        /// <summary>Plots the quickest open way over the gates to <paramref name="to"/> for the piloted hull.</summary>
        public void PlotCourse(StarSystem to)
        {
            if (to != null && CoopSends("plot", to.Name)) return; // US-T43: the course is the shared helm's: into the frames
            var ship = Piloted.Ship;
            if (ship == null || to == null) return;
            if (to == ActiveSystem) { Course = null; return; }
            var course = TravelCourse.Plan(Galaxy.Network, ActiveSystem, to, ship.Faction, Clock.Jd);
            if (course == null)
            {
                ShowToast("No open way through the gates to " + to.Name);
                return;
            }
            Course = course;
            courseShip = ship;
            CourseByCrew = false;
            courseFollow = false;
            ShowToast("Course plotted to " + to.Name.ToUpperInvariant() + ": " + course.Legs + (course.Legs == 1 ? " gate" : " gates") +
                      ", first " + course.Next.Name.ToUpperInvariant() + "  (the autopilot button hands it to the crew)");
        }

        /// <summary>
        /// The autopilot: hands the helm to the crew, who fly the plotted course gate by gate (by lane across each system where
        /// one runs to the gate) and take every gate; the view follows the hull. False, with a toast, if the crew cannot take it.
        /// </summary>
        public bool EngageCourseAutopilot()
        {
            if (CoopSends("autopilot")) return true; // US-T43: into the frames (it hands the shared helm to the crew)
            if (Course == null || Course.Done || !IsPiloting || Piloted.Ship != courseShip || CourseByCrew) return false;
            var ship = courseShip;
            var to = Course.Destination;
            ExitPiloting();
            if (!StartCrewCourse(ship, to, true))
            {
                ShowToast("The crew cannot take the course just now");
                Course = null;
                return false;
            }
            return true;
        }

        /// <summary>Sends <paramref name="ship"/> (standing in the active system) on a course to <paramref name="to"/> under its crew.</summary>
        bool StartCrewCourse(Ship ship, StarSystem to, bool follow)
        {
            if (CoopSends("crew", ship.Id + " " + (follow ? 1 : 0) + " " + to.Name)) return true; // US-T43: into the frames (it sends the ship)
            if (follow && !CoopActing) follow = false; // US-T43: another player's crew course: their view follows it, not this one
            var course =TravelCourse.Plan(Galaxy.Network, ActiveSystem, to, ship.Faction, Clock.Jd);
            if (course == null || Galaxy.Traffic == null || Galaxy.Traffic.SendPlotted(ship, ActiveSystem, to, Clock.Jd) == null) return false;
            Course = course;
            courseShip = ship;
            CourseByCrew = true;
            courseFollow = follow;
            if (follow) FollowCourseShip(ship);
            ShowToast("The " + ship.Name + " sets out for " + to.Name.ToUpperInvariant() + " by " + course.Legs + (course.Legs == 1 ? " gate" : " gates") +
                      ", first the " + course.Next.Name.ToUpperInvariant());
            return true;
        }

        /// <summary>The god view's trip to <paramref name="to"/> when a ship is followed: the ship is sent, and the view goes with it.</summary>
        bool TrySendFollowedShip(StarSystem to)
        {
            if (CoopExecuting) return false; // US-T43: the asker's camera chose this (its "crew" entry), not this peer's
            if (SpaceCamera.Mode != Cameras.SpaceCamera.CameraMode.Focus || !SpaceCamera.FocusTarget.IsShip) return false;
            var ship = SpaceCamera.FocusTarget.Ship;
            // A house ship is the player's own money: sent through the gates by a stray click on the map it could be lost
            // and written off (U.SPC-T553), so the click only moves the view; the ship is sent from its helm or its orders.
            if (ship.Faction != null && ship.Faction.IsHouse)
            {
                ShowToast("The " + ship.Name + " is yours: give it orders, or take the helm, to send it. The view goes alone");
                return false;
            }
            return StartCrewCourse(ship, to, true);
        }

        void FollowCourseShip(Ship ship)
        {
            var t = Target.OfShip(ship);
            Select(t);
            SpaceCamera.FocusOn(t, Cameras.SpaceCamera.DefaultFraming(t));
        }

        /// <summary>The system whose fleet holds <paramref name="ship"/> now, or null (inside a gate).</summary>
        StarSystem SystemHolding(Ship ship)
        {
            foreach (var s in Galaxy.Systems)
            {
                var ships = s.World.Fleet.Ships;
                if (ship.Id >= 0 && ship.Id < ships.Count && ships[ship.Id] == ship) return s;
            }
            return null;
        }

        void EndCourse(string toast)
        {
            Course = null;
            courseShip = null;
            CourseByCrew = false;
            courseFollow = false;
            if (toast != null) ShowToast(toast);
        }

        /// <summary>Each frame the crew flies a course: keeps it level with the hull's voyage, takes the view into each system the hull comes out in, and ends it on arrival.</summary>
        void UpdateCourseShip()
        {
            if (!CourseByCrew || courseShip == null) return;
            var ship = courseShip;
            if (ship.Destroyed) { EndCourse("The " + ship.Name + " was lost on the way to " + Course.Destination.Name); return; }
            if (IsPiloting && Piloted.Ship == ship) { EndCourse("Helm taken: the course is yours to fly"); return; }
            var v = ship.Voyage;
            if (v != null && v.Purpose == VoyagePurpose.Plotted) Course.SyncTo(v.Leg);
            var where = SystemHolding(ship);
            // Inside a gate: nothing to see until it comes out.
            if (where == null) return;
            if (where != ActiveSystem && courseFollow && !Travel.Active && !CoopIsClient) // US-T43: the host's view takes the party (its Activate goes out with its frame)
            {
                Activate(where);
                SetSystemVisible(true);
                FollowCourseShip(ship);
                ShowToast("The " + ship.Name + " comes out in " + where.Name.ToUpperInvariant() + (Course.Next != null ? ", on to the " + Course.Next.Name.ToUpperInvariant() : ""));
            }
            else if (where == ActiveSystem && courseFollow && !Travel.Active &&
                     (SpaceCamera.Mode != Cameras.SpaceCamera.CameraMode.Focus || !SpaceCamera.FocusTarget.IsShip || SpaceCamera.FocusTarget.Ship != ship))
                courseFollow = false;
            if (v == null || v.Purpose != VoyagePurpose.Plotted)
                EndCourse(where == Course.Destination ? "The " + ship.Name + " has arrived at " + where.Name.ToUpperInvariant() : "The course was called off");
        }

        /// <summary>The hull has gone through <paramref name="gate"/>: on to the next leg, replotted if it was some other gate, ended on arrival.</summary>
        void AdvanceCourse(JumpGate gate, Ship ship)
        {
            if (Course == null || CourseByCrew) return;
            bool more = Course.Passed(gate, Galaxy.Network, ship.Faction, Clock.Jd);
            if (!more)
            {
                Course = null;
                return;
            }
            ShowToast("Next gate: " + Course.Next.Name.ToUpperInvariant() + ", leg " + (Course.Taken + 1) + " of " + Course.Legs);
        }

        /// <summary>The HUD's line for the course being flown, or null: the way by its systems, the next gate and how far off it is.</summary>
        public string CourseLine()
        {
            if (Course == null) return null;
            if (CourseByCrew) return Course.Done ? null : "AUTOPILOT  " + courseShip.Name.ToUpperInvariant() + "  " + Course.Describe().ToUpperInvariant();
            // Another hull's helm ends the course; a system the hull came to some other way (FTL) replans from there.
            if (!IsPiloting || Piloted.Ship != courseShip) { Course = null; return null; }
            if (Course.Done || Course.Here != ActiveSystem)
            {
                var replanned = Course.Done ? null : TravelCourse.Plan(Galaxy.Network, ActiveSystem, Course.Destination, courseShip.Faction, Clock.Jd);
                Course = replanned;
                if (Course == null) return null;
            }
            var next = Course.Next;
            string line = "COURSE  " + Course.Describe().ToUpperInvariant();
            if (next.Home == ActiveSystem)
            {
                var at = Ephemeris.Helio(pilotFrameBody) + pilotOffsetKm;
                line += "   ·   " + DistanceText((next.PositionKm - at).Length) + " TO GATE";
            }
            return line;
        }

        /// <summary>Whether the HUD offers the autopilot: a course plotted at the helm, not yet handed to the crew.</summary>
        public bool CourseAutopilotOffered => Course != null && !CourseByCrew && !Course.Done && IsPiloting && Piloted.Ship == courseShip;
    }
}
