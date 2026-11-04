using System;
using Unity.SpaceGod.Core;

namespace Bench.CSharp
{
    /// <summary>
    /// The simulation half of Unity.SpaceGod's <c>UnitySpaceApp</c>, engine-free: what its <c>Awake</c> builds and what its
    /// <c>Update</c> steps each frame, with everything that only draws, plays sound or reads input left out. Every call here
    /// is one the game itself makes, in the game's order (see Bench/README.md, "What a tick is").
    /// </summary>
    public sealed class Simulation
    {
        public SimClock Clock { get; private set; }
        public Galaxy Galaxy { get; private set; }
        public StarSystem ActiveSystem { get; private set; }
        public World World => ActiveSystem.World;

        /// <summary>The battle the clock was last dropped to real time for (UnitySpaceApp.battleTimeFor).</summary>
        Battle battleTimeFor;

        /// <summary>The ship whose battle the followed view puts into battle time (the camera's focus target), or null.</summary>
        public Ship Followed;

        /// <summary>A crew-flown plotted course being followed (UnitySpaceApp.Course with CourseByCrew and courseFollow).</summary>
        public Ship CourseShip;
        public StarSystem CourseDestination;
        public bool CourseArrived, CourseEnded;

        public int BattlesStarted;

        /// <summary>The battle a "skirmish" scenario started (kept after it ends and leaves War.Battles).</summary>
        public Battle Skirmish;

        /// <summary>UnitySpaceApp.Awake: the clock (a fixed start date instead of now), the galaxy, the first system active.</summary>
        public static Simulation Build(double startJd, int seed)
        {
            var sim = new Simulation();
            // Clock = SimClock.StartingNow(); Clock.SetWarp(SimClock.StartingWarp.Index) -- the setting's default, unbound.
            sim.Clock = new SimClock(startJd);
            sim.Clock.SetWarp(SimClock.DefaultWarpIndex);
            sim.Galaxy = Galaxy.CreateDefault(sim.Clock.Jd, seed);
            // SubscribeWorlds: the game only toasts from these; the Bench counts.
            foreach (var system in sim.Galaxy.Systems) sim.SubscribeWorld(system);
            sim.Galaxy.SystemAdded += (s, jd) => sim.SubscribeWorld(s);
            sim.Activate(sim.Galaxy.Systems[0]);
            return sim;
        }

        void SubscribeWorld(StarSystem s) => s.World.War.BattleStarted += b => BattlesStarted++;

        /// <summary>UnitySpaceApp.Activate, simulation part: the system becomes the focus and the galaxy's viewpoint.</summary>
        public void Activate(StarSystem system)
        {
            ActiveSystem = system;
            Galaxy.Promote(system, Clock.Jd);
            Galaxy.Viewpoint = system.Origin;
        }

        /// <summary>
        /// Where the player is (UnitySpaceApp.PlayerHelioKm): the game's camera starts by Earth and the Bench never moves it,
        /// so this is Earth's heliocentric position in Sol, and the active system's root (body 0) anywhere else.
        /// </summary>
        public DVec3 PlayerHelioKm()
        {
            var cat = ActiveSystem.Catalog;
            int body = 0;
            for (int i = 0; i < cat.Count; i++)
                if (cat[i].Name == "Earth") { body = i; break; }
            return ActiveSystem.Ephemeris.Helio(body);
        }

        /// <summary>One frame of UnitySpaceApp.Update, simulation steps only, in the game's order.</summary>
        public void Tick(double dt)
        {
            Clock.Advance(dt);
            Galaxy.Update(Clock.Jd);
            World.UpdateAdventures(Clock.Jd, PlayerHelioKm());
            BattleTime();
            UpdateCourseShip();
        }

        /// <summary>UnitySpaceApp.BattleTime: a followed ship in a battle drops the clock to real time, once per battle.</summary>
        void BattleTime()
        {
            var battle = Followed?.Battle;
            if (battle == null || battle == battleTimeFor) return;
            EnterBattleTime(battle);
        }

        /// <summary>UnitySpaceApp.EnterBattleTime.</summary>
        public void EnterBattleTime(Battle battle)
        {
            battleTimeFor = battle;
            if (Clock.WarpIndex > 0) Clock.SetWarp(0);
        }

        /// <summary>UnitySpaceApp.WatchBattle, simulation part: the biggest hull is followed and battle time starts.</summary>
        public void WatchBattle(Battle battle)
        {
            Ship flag = null;
            foreach (var u in battle.Units)
                if (u.Ship != null && u.Present && (flag == null || u.Combat.Spec.Hull > flag.Combat.Spec.Hull)) flag = u.Ship;
            if (flag == null) return;
            Followed = flag;
            EnterBattleTime(battle);
        }

        /// <summary>
        /// SceneTour.TriggerSkirmish: <paramref name="classes"/> for each side parked near <paramref name="body"/>, already
        /// hostile, and the fight started at once.
        /// </summary>
        public Battle TriggerSkirmish(int body, Faction aggressor, Faction defender, ShipClass[] classes)
        {
            double jd = Clock.Jd;
            var dir = new DVec3(1, 0.15, 0.4).Normalized;
            var side = DVec3.Cross(new DVec3(0, 1, 0), dir).Normalized;
            for (int i = 0; i < classes.Length; i++)
                World.Fleet.SpawnAt("Showcase " + aggressor.Name + " " + (i + 1), classes[i], aggressor, body, dir * (2000 + i * 400), -dir, jd);
            for (int i = 0; i < classes.Length; i++)
                World.Fleet.SpawnAt("Showcase " + defender.Name + " " + (i + 1), classes[i], defender, body, dir * (2000 + i * 400) + side * 3000, dir, jd);
            return World.War.TryStartBattle(body, aggressor, jd);
        }

        /// <summary>
        /// UnitySpaceApp.TrySendFollowedShip / StartCrewCourse: the followed ship, if not the player's own, is sent by its
        /// crew on the quickest course to <paramref name="to"/> and the view goes with it. False if it cannot go.
        /// </summary>
        public bool StartCrewCourse(Ship ship, StarSystem to)
        {
            if (ship.Faction != null && ship.Faction.IsHouse) return false;
            var course = TravelCourse.Plan(Galaxy.Network, ActiveSystem, to, ship.Faction, Clock.Jd);
            if (course == null || Galaxy.Traffic == null || Galaxy.Traffic.SendPlotted(ship, ActiveSystem, to, Clock.Jd) == null) return false;
            CourseShip = ship;
            CourseDestination = to;
            Followed = ship;
            return true;
        }

        /// <summary>UnitySpaceApp.UpdateCourseShip for a followed crew course.</summary>
        void UpdateCourseShip()
        {
            if (CourseShip == null || CourseEnded) return;
            var ship = CourseShip;
            if (ship.Destroyed) { CourseEnded = true; return; }
            var v = ship.Voyage;
            var where = SystemHolding(ship);
            if (where == null) return; // inside a gate
            if (where != ActiveSystem) Activate(where);
            if (v == null || v.Purpose != VoyagePurpose.Plotted)
            {
                CourseEnded = true;
                CourseArrived = where == CourseDestination;
            }
        }

        /// <summary>UnitySpaceApp.SystemHolding: the system whose fleet holds the ship now, or null (inside a gate).</summary>
        public StarSystem SystemHolding(Ship ship)
        {
            foreach (var s in Galaxy.Systems)
            {
                var ships = s.World.Fleet.Ships;
                if (ship.Id >= 0 && ship.Id < ships.Count && ships[ship.Id] == ship) return s;
            }
            return null;
        }
    }
}
