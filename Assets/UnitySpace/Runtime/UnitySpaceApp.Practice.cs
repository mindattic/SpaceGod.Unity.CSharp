using System.Collections.Generic;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod
{
    /// <summary>
    /// The practice fighter (U.SPC-T601, <see cref="PracticeSpawn"/>): Shift+V, or the button on the Spawn &amp; Pilot
    /// screen, puts a home-side fighter at the camera's spot in whatever system and view the player is in, clear of
    /// anything there, and the player straight at its helm, to fly alongside the hulls around it and practise the flight.
    /// The system keeps one: pressing again brings the same hull, mended and refuelled, to the new spot.
    /// </summary>
    public sealed partial class UnitySpaceApp
    {
        readonly List<SpawnObstacle> practiceObstacles = new List<SpawnObstacle>();

        /// <summary>Spawns (or brings over) this system's practice fighter at the camera and takes its helm.</summary>
        public void SpawnPracticeFighter()
        {
            // Between the stars there is no system to fly in (HandleCommands does not even get here then).
            if (Travel.Active || InFtlCruise) return;

            // Where the camera is now, taken before any helm is let go: its free-flight frame goes stale while it is
            // focused on something far off.
            int frame = SpaceCamera.FrameAt(SpaceCamera.ScenePosition);
            var offsetKm = Scale.InverseLocalOffset(Catalog[frame], SpaceCamera.ScenePosition - BodyScene(frame));
            var forwardUnity = SpaceCamera.Rotation * Vector3.forward;
            var forward = new DVec3(forwardUnity.x, forwardUnity.y, forwardUnity.z).SwapYZ;

            RememberCommand();
            ReleaseSeat();
            if (IsPiloting) ExitPiloting();
            // Out of any screen the spawn was asked from (the spawn grid, the galaxy map): straight to the helm.
            ShowSpawn = ShowGalaxyMap = ShowPicker = false;
            if (CoopSpawns("practice", "", frame, offsetKm, forward)) return; // US-T43: the rest on every peer (CoopSpawnPractice)

            double jd = Clock.Jd;
            var frameHelio = Ephemeris.Helio(frame);
            var ship = ReusablePracticeFighter();
            CollectPracticeObstacles(ship);
            double lengthKm = ship?.LengthKm ?? CombatSpec.Of(ShipClass.Fighter).LengthKm;
            var at = PracticeSpawn.Clear(frameHelio + offsetKm, forward, lengthKm, practiceObstacles) - frameHelio;

            if (ship != null)
            {
                // The same hull, brought over: whole again, tanks full, parked here.
                ship.Combat.Repair(1);
                ship.Combat.FreeResupply();
                ship.Propellant = ship.PropellantCapacity;
                ship.OutOfPropellant = false;
                ship.StrandedInFlight = false;
                Fleet.PinAt(ship, frame, at, forward, jd);
            }
            else
            {
                ship = Fleet.SpawnAt(PracticeSpawn.Name, ShipClass.Fighter, World.Sol, frame, at, forward, jd);
                ship.Ports = World.FreeShipPorts(); // not a hidden haven's rock, as for every free ship
            }

            EnterPiloting(Target.OfShip(ship));
            ShowToast("Practice fighter: " + ship.Name + " at the camera  (Shift+V again to bring it to the camera, P to hand it back)");
        }

        /// <summary>
        /// This system's practice fighter, if it can simply be brought over: still here, whole enough to fly, on the home
        /// side, nobody's prize and in no fight. A lost or taken one stays where it is (a wreck, a prize) and a new one is made.
        /// </summary>
        Ship ReusablePracticeFighter()
        {
            foreach (var s in Fleet.Ships)
            {
                if (!PracticeSpawn.Is(s)) continue;
                if (!Fleet.IsPresent(s) || s.Destroyed || s.Derelict || s.Battle != null || s.IsEscort) continue;
                if (s.Faction != World.Sol || s.Combat == null || !s.Combat.Alive) continue;
                if (s.Phase == ShipPhase.Lane || s.Planetside) continue;
                if (World.Holdings != null && World.Holdings.Owns(s)) continue; // enlisted in the player's fleet: theirs now
                if (!Embodiables.HelmOf(World, s).CanEmbody) continue;
                return s;
            }
            return null;
        }

        /// <summary>Everything near enough to matter that the fighter must not start inside, heliocentric km: the system's
        /// hulls (and the craft of its fights), stations, wrecks' debris and its worlds. <paramref name="self"/> is left out.</summary>
        void CollectPracticeObstacles(Ship self)
        {
            practiceObstacles.Clear();
            double jd = Clock.Jd;
            var battles = new HashSet<Battle>();
            foreach (var s in Fleet.Ships)
            {
                if (s == self || s.Destroyed || s.Combat == null || s.Planetside || s.IsEscort) continue;
                if (s.Battle != null)
                {
                    battles.Add(s.Battle);
                    continue;
                }
                var helio = s.Derelict && s.Orbit != null ? DerelictHelio(Ephemeris, s, jd) : Fleet.StateOf(s, jd).HelioKm;
                practiceObstacles.Add(new SpawnObstacle(helio, s.LengthKm * 0.5));
            }
            foreach (var b in battles)
            {
                var origin = Ephemeris.Helio(b.Body) + b.Anchor;
                foreach (var u in b.Units)
                    if (u.Present && (self == null || u.Ship != self))
                        practiceObstacles.Add(new SpawnObstacle(origin + u.Pos, u.LengthKm * 0.5));
            }
            foreach (var st in World.Infrastructure.Structures)
                practiceObstacles.Add(new SpawnObstacle(st.HelioAt(Ephemeris, jd), st.LengthKm * 0.5));
            foreach (var w in World.War.Wrecks.Items)
                practiceObstacles.Add(new SpawnObstacle(World.WreckHelio(w, jd), w.DebrisRadiusKm(jd) + w.LengthKm));
            foreach (var body in Catalog.Bodies)
                if (body.RadiusKm > 0)
                    practiceObstacles.Add(new SpawnObstacle(Ephemeris.Helio(body.Index), body.RadiusKm * 1.02));
        }
    }
}
