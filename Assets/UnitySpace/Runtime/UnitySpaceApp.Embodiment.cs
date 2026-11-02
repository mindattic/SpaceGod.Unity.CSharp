using System.Collections.Generic;
using Unity.SpaceGod.Cameras;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod
{
    /// <summary>
    /// Take charge of anything, any time (U.SPC-T422, see docs/EMBODIMENT.md). From the strategy view, ' and ; jump
    /// straight into the next seat in the fight in view (a ship's helm, a launched fighter's or bomber's cockpit) and /
    /// mans a gun on the hull flown or selected; P hands whatever is held back to its AI and returns to command, the
    /// view and time warp the commander had. Nothing pauses and nothing loads: the clock runs on at real time while a
    /// seat is held, the camera glides from seat to seat, and every other seat goes on under its own AI. A ship's helm
    /// is flown by the existing pilot loop (<see cref="EnterPiloting"/>); every other seat is held by
    /// <see cref="seat"/> and driven here through <see cref="IEmbodiable.Control"/>.
    /// </summary>
    public sealed partial class UnitySpaceApp
    {
        /// <summary>The non-helm seat the player holds (a fighter's cockpit, a gun mount), if any.</summary>
        readonly EmbodimentSession seat = new EmbodimentSession();

        /// <summary>Where the stick points at a seat, Unity axes (unit): the mouse swings it, the view follows it.</summary>
        Vector3 seatAim = Vector3.forward;

        /// <summary>What the commander had before taking a seat: their time warp and what the camera was following,
        /// restored on <see cref="ReturnToCommand"/>. <see cref="commandWarp"/> is -1 when nothing is remembered.</summary>
        int commandWarp = -1;

        Target commandFocus = Target.None;

        /// <summary>True while a non-helm seat (a fighter, a gun) is held.</summary>
        public bool IsSeated => seat.Active;

        /// <summary>What is held at a non-helm seat, or null.</summary>
        public IEmbodiable Seat => seat.Current;

        /// <summary>The player is at some controls (a helm or any other seat): the strategy view's keys and clicks stand aside.</summary>
        public bool AtControls => IsPiloting || IsSeated;

        /// <summary>Keeps the commander's view and time warp, once, as a seat is first taken from command.</summary>
        void RememberCommand()
        {
            if (AtControls) return;
            commandWarp = Clock.WarpIndex;
            commandFocus = SpaceCamera.Mode != SpaceCamera.CameraMode.Free ? SpaceCamera.FocusTarget : Target.None;
        }

        /// <summary>
        /// Takes <paramref name="next"/>, handing back whatever was held first: a ship's helm through the pilot loop, any
        /// other seat through <see cref="seat"/>. The clock drops to real time (a fight is flown at the pace it is fought)
        /// but never stops, and the camera glides to the new seat.
        /// </summary>
        void TakeSeat(IEmbodiable next)
        {
            if (next == null) return;
            if (!next.CanEmbody)
            {
                ShowToast("Can't take " + next.Name + ": " + next.Blocker);
                return;
            }
            RememberCommand();
            if (next is ShipHelm helm)
            {
                seat.Release();
                if (IsPiloting && Piloted.Ship == helm.Ship) return;
                if (IsPiloting) ExitPiloting();
                EnterPiloting(Target.OfShip(helm.Ship));
                return;
            }
            if (IsPiloting) ExitPiloting();
            if (!seat.Switch(next))
            {
                ShowToast("Can't take " + next.Name + ": " + (next.Blocker ?? "out of reach"));
                return;
            }
            var pose = next.PoseAt(Clock.Jd);
            seatAim = ToVector(pose.Forward.SwapYZ).normalized;
            Clock.SetWarp(0);
            SpaceCamera.EnterPilotChase(SeatTarget(next), next is FighterSeat ? 7 : 2.5);
            ShowToast((next is FighterSeat ? "Flying " : "Manning ") + next.Name + ", " + Clock.Warp.Label + SeatKeys(next));
        }

        static string SeatKeys(IEmbodiable s) => s is FighterSeat
            ? "  (mouse aims, W/S throttle, A/D Q/E strafe, Z/X roll, Shift boost, F flight assist, left mouse guns, right mouse " +
              "weapon group, 1 next group, T lock, 6 next weak point, Ctrl flares, 2 attack my target, 3 cover me, 4 form up, " +
              "5 engage at will, 7/8/9 power to weapons/shields/engines, 0 even power, ' ; next seat, P back to command)"
            : "  (mouse lays it, left mouse fires, / next gun, P back to command)";

        /// <summary>What the camera rides with at a seat: the craft itself, or the hull a gun is mounted on.</summary>
        static Target SeatTarget(IEmbodiable s)
        {
            switch (s)
            {
                case FighterSeat f: return Target.OfUnit(f.Battle, f.Unit);
                case MountedTurret t: return t.Unit.Ship != null ? Target.OfShip(t.Unit.Ship) : Target.OfUnit(t.Battle, t.Unit);
                default: return Target.None;
            }
        }

        /// <summary>
        /// Hands whatever is held back to its AI and returns to command: the camera goes back to what the commander was
        /// following (or stays free where the seat was) and time to the warp they had. <paramref name="why"/>, when given,
        /// says why the seat was lost.
        /// </summary>
        void ReturnToCommand(string why = null)
        {
            string name = seat.Current?.Name;
            seat.Release();
            if (IsPiloting)
            {
                name = Piloted.Ship?.Name;
                ExitPiloting();
            }
            SpaceCamera.PilotView = null;
            SpaceCamera.ExitFocus();
            if (!commandFocus.IsNone && !(commandFocus.IsShip && commandFocus.Ship.Destroyed) && !commandFocus.IsUnit)
                SpaceCamera.FocusOn(commandFocus);
            if (commandWarp >= 0) Clock.SetWarp(commandWarp);
            commandWarp = -1;
            commandFocus = Target.None;
            string head = why != null && name != null ? "The " + name + " is " + why : name != null ? name + " handed back to its crew" : "Handed back";
            ShowToast(head + ": back in command, " + Clock.Warp.Label);
        }

        /// <summary>The fight the strategy view is on: the seat's, the flown hull's, the selection's or the followed ship's,
        /// else the battle nearest the camera.</summary>
        Battle BattleInView()
        {
            if (seat.Current is FighterSeat f) return f.Battle;
            if (seat.Current is MountedTurret t) return t.Battle;
            if (IsPiloting && Piloted.Ship?.Battle != null) return Piloted.Ship.Battle;
            if (Selected.IsShip && Selected.Ship.Battle != null) return Selected.Ship.Battle;
            var focus = SpaceCamera.FocusTarget;
            if (focus.IsShip && focus.Ship.Battle != null) return focus.Ship.Battle;
            Battle best = null;
            double bestD = double.MaxValue;
            foreach (var b in World.War.Battles)
            {
                if (b.Over) continue;
                double d = (BattleScene(b, DVec3.Zero) - SpaceCamera.ScenePosition).Length;
                if (d < bestD)
                {
                    bestD = d;
                    best = b;
                }
            }
            return best;
        }

        /// <summary>Where a seat is in the scene.</summary>
        DVec3 SeatScene(IEmbodiable s)
        {
            switch (s)
            {
                case ShipHelm h: return TargetScene(Target.OfShip(h.Ship));
                case FighterSeat f: return BattleScene(f.Battle, f.Unit.Pos);
                case MountedTurret t: return BattleScene(t.Battle, t.Unit.PointOf(t.Mount.Def));
                default: return SpaceCamera.ScenePosition;
            }
        }

        /// <summary>
        /// ' / ; (U.SPC-T422): from command, the seat in the fight in view nearest the middle of the screen; at a seat or
        /// a helm, the next (or previous) one in the same fight.
        /// </summary>
        void CycleSeat(int step)
        {
            var battle = BattleInView();
            var seats = Embodiables.SeatsIn(World, battle);
            if (seats.Count == 0)
            {
                ShowToast(battle == null ? "No battles under way: nothing to take charge of" : "Nobody left to fly in " + battle.Name);
                return;
            }
            IEmbodiable current = seat.Current ?? (IsPiloting && Piloted.Ship != null ? Embodiables.HelmOf(World, Piloted.Ship) : null);
            if (current is MountedTurret gun) current = gun.Host;
            IEmbodiable next;
            if (current == null || !seats.Contains(current))
            {
                // From command: what the player is looking at.
                var forward = SpaceCamera.Rotation * Vector3.forward;
                next = null;
                float best = float.MaxValue;
                foreach (var s in seats)
                {
                    var to = ToVector(SeatScene(s) - SpaceCamera.ScenePosition);
                    float angle = to.sqrMagnitude > 0 ? Vector3.Angle(forward, to) : 0;
                    if (angle < best)
                    {
                        best = angle;
                        next = s;
                    }
                }
            }
            else next = Embodiables.Cycle(seats, current, step);
            TakeSeat(next);
        }

        /// <summary>
        /// / (U.SPC-T422): the next gun mount on the hull held (or, from command, the one selected in a fight); past its
        /// last gun, back up to its helm.
        /// </summary>
        void ManNextGun()
        {
            BattleUnit unit = null;
            Battle battle = null;
            if (seat.Current is MountedTurret held)
            {
                unit = held.Unit;
                battle = held.Battle;
            }
            else if (IsPiloting && Piloted.Ship?.Battle != null)
            {
                battle = Piloted.Ship.Battle;
                unit = battle.UnitOf(Piloted.Ship);
            }
            else if (Selected.IsShip && Selected.Ship.Battle != null)
            {
                battle = Selected.Ship.Battle;
                unit = battle.UnitOf(Selected.Ship);
            }
            if (unit == null)
            {
                ShowToast("Select a hull in a fight (or fly one) to man its guns");
                return;
            }
            var guns = new List<IEmbodiable>(Embodiables.TurretsOn(World, battle, unit));
            if (guns.Count == 0)
            {
                ShowToast("The " + unit.Name + " has no gun left to man");
                return;
            }
            if (seat.Current is MountedTurret last && guns.IndexOf(last) == guns.Count - 1 && last.Host != null)
            {
                TakeSeat(last.Host);
                return;
            }
            TakeSeat(Embodiables.Cycle(guns, seat.Current, 1));
        }

        // ---------------------------------------------------------------- the picker (U.SPC-T446)

        /// <summary>The take-charge picker (F9): every seat on the thing in hand and around the camera, to click into.</summary>
        public bool ShowPicker { get; set; }

        /// <summary>How many ships near the camera, outside any fight, the picker offers the helms of.</summary>
        const int PickerNearbyShips = 12;

        List<IEmbodiable> pickerSeats = new List<IEmbodiable>();
        float pickerBuiltAt = -10f;

        void TogglePicker()
        {
            ShowPicker = !ShowPicker;
            pickerBuiltAt = -10f;
        }

        /// <summary>What the player holds now, a helm or any other seat, or null in command.</summary>
        public IEmbodiable HeldSeat => seat.Current ?? (IsPiloting && Piloted.Ship != null ? Embodiables.HelmOf(World, Piloted.Ship) : null);

        /// <summary>
        /// The picker's list (U.SPC-T446), rebuilt a few times a second from <see cref="Embodiables.Pickable"/>: the seats on
        /// the hull held (or flown, or selected) and its gun mounts, then every seat in the fight in view, then the helms of
        /// the ships nearest the camera. Never a list of its own: a new kind registered in <see cref="Embodiables"/> shows here.
        /// </summary>
        public List<IEmbodiable> PickerSeats()
        {
            if (Time.unscaledTime - pickerBuiltAt < 0.25f) return pickerSeats;
            pickerBuiltAt = Time.unscaledTime;
            var battle = BattleInView();
            BattleUnit onUnit = null;
            Ship onShip = null;
            switch (seat.Current)
            {
                case MountedTurret t:
                    onUnit = t.Unit;
                    battle = t.Battle;
                    break;
                case FighterSeat f:
                    onUnit = f.Unit;
                    battle = f.Battle;
                    break;
                default:
                    if (IsPiloting) onShip = Piloted.Ship;
                    else if (Selected.IsShip) onShip = Selected.Ship;
                    else if (Selected.IsUnit && !Selected.Battle.Over)
                    {
                        onUnit = Selected.Unit;
                        battle = Selected.Battle;
                    }
                    break;
            }
            pickerSeats = Embodiables.Pickable(World, battle, onUnit, onShip, ShipsNearCamera(PickerNearbyShips));
            return pickerSeats;
        }

        /// <summary>The <paramref name="count"/> ships in view of the system nearest the camera and not in a fight (those
        /// come with their fight), nearest first.</summary>
        List<Ship> ShipsNearCamera(int count)
        {
            var near = new List<(double d, Ship s)>();
            var eye = SpaceCamera.ScenePosition;
            foreach (var s in Fleet.Ships)
            {
                if (s.Destroyed || s.Battle != null || s.Phase == ShipPhase.Landed) continue;
                near.Add(((TargetScene(Target.OfShip(s)) - eye).Length, s));
            }
            near.Sort((a, b) => a.d.CompareTo(b.d));
            var list = new List<Ship>();
            for (int i = 0; i < near.Count && i < count; i++) list.Add(near[i].s);
            return list;
        }

        /// <summary>
        /// A seat clicked in the picker: taken through <see cref="TakeSeat"/>, which always hands the seat held back to its
        /// AI first (<see cref="EmbodimentSession.Switch"/>, or the pilot loop for a helm). The picker closes so the mouse
        /// goes back to the stick.
        /// </summary>
        public void PickSeat(IEmbodiable next)
        {
            if (next == null) return;
            ShowPicker = false;
            if (Equals(next, HeldSeat)) return;
            TakeSeat(next);
        }

        /// <summary>The picker's Back to command button: what P does at the controls.</summary>
        public void PickCommand()
        {
            ShowPicker = false;
            if (AtControls) ReturnToCommand();
        }

        /// <summary>
        /// One frame at a non-helm seat: the mouse swings the stick (and the view after it), W/S work a craft's throttle,
        /// the triggers go to what is held, and a seat that is lost (shot down, the fight over) returns the player to
        /// command, saying why. Runs after <see cref="UpdatePiloting"/>, which clears the view's aim each frame.
        /// </summary>
        void UpdateSeat(float dt)
        {
            if (!seat.Active) return;
            var held = seat.Current;
            var look = ClickPanelOpen ? Vector2.zero : Input.MouseDelta;
            var rot = SpaceCamera.Rotation;
            var up = rot * Vector3.up;
            var right = rot * Vector3.right;
            seatAim = (Quaternion.AngleAxis(look.x * SpaceCamera.LookSensitivity, up) *
                       Quaternion.AngleAxis(-SpaceCamera.LookY(look.y) * SpaceCamera.LookSensitivity, right) * seatAim).normalized;
            var input = new EmbodimentInput
            {
                Aim = new DVec3(seatAim.x, seatAim.y, seatAim.z).SwapYZ,
                Throttle = Mathf.Clamp(Input.Move.z, -1f, 1f),
                StrafeRight = Mathf.Clamp(Input.Move.x, -1f, 1f),
                StrafeUp = Mathf.Clamp(Input.Move.y, -1f, 1f),
                Roll = Mathf.Clamp(Input.Roll, -1f, 1f),
                Boost = Input.Boost,
                Primary = Input.Fire && !ClickPanelOpen,
                Secondary = Input.LookHeld && !ClickPanelOpen,
            };
            if (held is FighterSeat cockpit && !ClickPanelOpen) UpdateCockpit(cockpit, input.Aim);
            // On the sim clock's time, so a fight run faster is flown faster too.
            double simDt = dt * Clock.Warp.SimSecondsPerSecond * Clock.Speed;
            if (!seat.Step(input, simDt))
            {
                ReturnToCommand(seat.EndedBecause ?? "lost");
                return;
            }
            var pose = held.PoseAt(Clock.Jd);
            var roof = ToVector(pose.Up.SwapYZ);
            SpaceCamera.PilotView = Quaternion.LookRotation(seatAim, Vector3.Dot(roof, seatAim) > 0.99f ? up : roof);
        }

        /// <summary>
        /// The dogfight game's keys in a cockpit (U.SPC-T424): F flight assist, T a lock on the hostile nearest the crosshair
        /// (again for the next), Ctrl flares, and the number keys: 1 the next weapon group on the second trigger, 2-5 orders
        /// to the wing (attack my target, cover me, form up, engage at will), 6 the next weak point on the locked hull, 7/8/9
        /// a pip of power to weapons, shields or engines, 0 an even share.
        /// </summary>
        void UpdateCockpit(FighterSeat f, DVec3 aim)
        {
            if (Input.ToggleFlightAssist) ShowToast(f.ToggleFlightAssist() ? "Flight assist off: it drifts on its momentum" : "Flight assist on");
            if (Input.LockTarget)
            {
                var locked = f.CycleLock(aim);
                ShowToast(locked == null ? "Nothing hostile near the crosshair to lock"
                    : "Locking " + locked.Name + (f.Unit.PlayerSubTarget != null ? " @ " + WeakPoints.Label(f.Unit.PlayerSubTarget) : "") + ": hold the crosshair on it");
            }
            if (Input.DropMine) f.DropCountermeasures();
            switch (Input.NumberKey)
            {
                case 1:
                    var nextGroup = f.CycleSecondaryGroup();
                    ShowToast(nextGroup == null ? "No guided weapons aboard" : "Second trigger: " + WeaponGroups.Label(nextGroup.Value));
                    break;
                case 2: OrderWing(f, WingOrder.AttackMyTarget); break;
                case 3: OrderWing(f, WingOrder.CoverMe); break;
                case 4: OrderWing(f, WingOrder.FormUp); break;
                case 5: OrderWing(f, WingOrder.EngageAtWill); break;
                case 6:
                    var weak = f.CycleWeakPoint();
                    ShowToast(weak == null ? "Lock a capital ship (T) to pick a weak point" : "Weak point: " + WeakPoints.Label(weak));
                    break;
                case 7: f.ShiftPower(PowerSystem.Weapons); break;
                case 8: f.ShiftPower(PowerSystem.Shields); break;
                case 9: f.ShiftPower(PowerSystem.Engines); break;
                case 0: f.BalancePower(); break;
            }
        }

        void OrderWing(FighterSeat f, WingOrder order)
        {
            int heard = f.OrderWing(order);
            ShowToast(heard == 0 ? "No wingmen left to order" : "Wing (" + heard + "): " + Wing.Label(order));
        }

        /// <summary>The HUD's line at a non-helm seat (null when none is held): what is held, its state, and the keys out.</summary>
        public string SeatLine()
        {
            switch (seat.Current)
            {
                case FighterSeat f:
                    return f.Readout() + "  ·  ' ; next seat   P back to command";
                case MountedTurret t:
                    return "GUN  ·  " + t.Name + "  ·  " + (t.Mount.Cooldown > 0 ? "reloading" : "ready") + "  ·  / next gun   P back to command";
                default:
                    return null;
            }
        }
    }
}
