// US-T39: co-op's side of the app (new file, a partial of the ported UnitySpaceApp; docs/MIGRATION.md "Co-op"). The ported
// originals call in here only at lines marked US-T39, and every call is the original's own behaviour when CoopPeer.Current is
// null (single-player).
using System;
using System.Collections.Generic;
using System.Text;
using Unity.SpaceGod.Cameras;
using Unity.SpaceGod.Coop;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.UI;
using UnityEngine;

namespace Unity.SpaceGod
{
    public sealed partial class UnitySpaceApp
    {
        LocalSession coopWatched;
        ClockState coopLeft;
        bool coopLeftValid;
        double coopSimRate = double.NaN;
        bool coopStepped;
        string coopLastControl;
        string coopLastSelection = "n";

        // ---------------------------------------------------------------- the frame's clock step

        /// <summary>
        /// <c>Clock.Advance(dt)</c> (the original's), in lockstep in co-op. On the host: the clients' clock requests, the
        /// frame's commands, then the clock as it stands is recorded and advanced. On a client: the same commands, any change
        /// this machine made to its clock since the last frame goes to the host as a request, then the clock is put in the
        /// host's state and advanced by the host's seconds, which lands it on the host's date to the bit.
        /// </summary>
        void CoopAdvance(float dt)
        {
            var peer = CoopPeer.Current;
            if (peer == null)
            {
                Clock.Advance(dt);
                return;
            }
            CoopWatchSession();
            coopStepped = false;
            if (peer.IsHost)
            {
                var rec = peer.HostOpenFrame();
                foreach (var (seat, request) in peer.TakeClockRequests()) CoopClockRequest(seat, request);
                CoopApplyEntries(peer, rec.Entries);
                CoopShareSelection(peer);
                CoopHostPilotFrame(peer, rec); // US-T43: the helm holder's hands for this frame, into the record
                rec.Before = ClockState.Of(Clock);
                Clock.Advance(dt);
                rec.JdAfter = Clock.Jd;
            }
            else
            {
                var rec = peer.ClientRecord;
                if (rec == null) return; // nothing from the host yet: time holds
                CoopApplyEntries(peer, rec.Entries);
                CoopShareSelection(peer);
                if (coopLeftValid)
                {
                    // The keys (. , Space) or this machine's own rules (a seat taken drops to real time) moved the clock: the host's call.
                    if (Clock.WarpIndex != coopLeft.Warp) peer.RequestClock("warp " + Clock.WarpIndex);
                    if (Clock.PauseHeld != coopLeft.PauseHeld) peer.RequestClock("pause " + (Clock.PauseHeld ? 1 : 0));
                }
                rec.Before.ApplyTo(Clock);
                Clock.Advance(dt);
                if (Clock.Jd != rec.JdAfter)
                {
                    peer.ClockCorrections++;
                    Clock.SetJd(rec.JdAfter);
                }
                peer.Consumed = true;
            }
            coopLeft = ClockState.Of(Clock);
            coopLeftValid = true;
            coopSimRate = Clock.Warp.SimSecondsPerSecond * Clock.Speed;
            coopStepped = true;
        }

        /// <summary>A client's warp or pause, on the host's clock (the one every peer's follows).</summary>
        void CoopClockRequest(int seat, string request)
        {
            var f = request.Split(' ');
            if (f.Length < 2) return;
            if (f[0] == "warp")
            {
                Clock.SetWarp(Wire.ParseI(f[1]));
                ShowToast(CoopPeer.Current.NameOf(seat) + ": time " + Clock.Warp.Label);
            }
            else if (f[0] == "pause" && (f[1] == "1") != Clock.PauseHeld) TogglePause();
        }

        /// <summary>The frame's commands, in order, on every peer: session commands through the original's session (its
        /// SeatAuthority validates and applies them), roster entries to the co-op peer.</summary>
        void CoopApplyEntries(CoopPeer peer, List<string> entries)
        {
            coopPilotFrame = null;
            foreach (var e in entries)
            {
                // US-T43: the app's own entries, in the frames' order with the session's.
                if (CoopAppEntry(peer, e)) continue;
                if (!e.StartsWith("s ", StringComparison.Ordinal))
                {
                    peer.ApplyRosterEntry(e);
                    continue;
                }
                string codec = e.Substring(2);
                string key = null;
                int at = codec.IndexOf(" @", StringComparison.Ordinal);
                if (at >= 0)
                {
                    key = codec.Substring(at + 2);
                    codec = codec.Substring(0, at);
                }
                IGameCommand cmd;
                try { cmd = CommandCodec.Read(codec); }
                catch (FormatException x)
                {
                    CoopPeer.Warn("bad command '" + codec + "': " + x.Message);
                    continue;
                }
                // A take names its seat by key: each peer's session gives the seat its id as it is first named (in the frames'
                // order, so the same id everywhere); the sender's id would mean nothing here.
                if (cmd is TakeSeatCommand take && key != null)
                {
                    var seat = CoopResolveSeat(key);
                    if (seat == null)
                    {
                        CoopPeer.Log(cmd.Player + " asked for seat '" + key + "', which is gone");
                        continue;
                    }
                    cmd = new TakeSeatCommand(take.Player, session.IdOf(seat));
                }
                session.Submit(cmd);
            }
            session.ApplyPending();
        }

        void CoopShareSelection(CoopPeer peer)
        {
            string key = CoopTargetKey(Selected);
            if (key == coopLastSelection) return;
            coopLastSelection = key;
            peer.Selected(key);
        }

        // ---------------------------------------------------------------- adventures

        /// <summary>
        /// <c>World.UpdateAdventures(Clock.Jd, PlayerHelioKm())</c> when <paramref name="wanted"/> (the original's line). In
        /// co-op the host's call is everyone's: whether it ran this frame (the host between the stars skips it) and from the
        /// host's player point, which places the adventures; a client's own camera does not.
        /// </summary>
        void CoopUpdateAdventures(bool wanted)
        {
            var peer = CoopPeer.Current;
            if (peer == null)
            {
                if (wanted) World.UpdateAdventures(Clock.Jd, PlayerHelioKm());
                return;
            }
            var rec = peer.IsHost ? peer.Open : peer.ClientRecord;
            if (rec == null) return;
            if (peer.IsHost)
            {
                rec.Adventures = wanted;
                if (wanted) rec.AdventurePoint = PlayerHelioKm();
            }
            if (rec.Adventures) World.UpdateAdventures(Clock.Jd, rec.AdventurePoint);
        }

        // ---------------------------------------------------------------- seats

        void CoopWatchSession()
        {
            if (coopWatched == session) return;
            coopWatched = session;
            session.EventRaised += CoopSessionEvent;
        }

        /// <summary>What the session's authority said, on every peer: logged, and for this machine's player the seat's view
        /// (what <see cref="TakeSeat"/> does once a seat is held), its loss, or the refusal.</summary>
        void CoopSessionEvent(IGameEvent e)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return;
            bool mine = e.Player == session.LocalPlayer;
            switch (e)
            {
                case SeatTaken t:
                {
                    var seat = session.State.Seat(t.Seat);
                    CoopPeer.Log(peer.NameOf(t.Player.Seat) + " (" + t.Player + ") takes seat " + t.Seat + " (" + seat?.Name + ") at frame " + peer.Frame);
                    if (mine && seat != null) CoopSeatTakenHere(seat);
                    break;
                }
                case SeatReleased r:
                    CoopPeer.Log(peer.NameOf(r.Player.Seat) + " (" + r.Player + ") lets go of seat " + r.Seat + (r.Reason != null ? ": " + r.Reason : "") + " at frame " + peer.Frame);
                    if (mine && r.Reason != null) ReturnToCommand(r.Reason, session.State.Seat(r.Seat)?.Name);
                    break;
                case CommandRefused c:
                    CoopPeer.Log(peer.NameOf(c.Player.Seat) + "'s " + (c.Command != null ? CommandCodec.Write(c.Command).Split(' ')[0] : "command") + " refused: " + c.Reason);
                    if (mine) ShowToast("Can't: " + c.Reason);
                    break;
            }
        }

        /// <summary>The tail of <see cref="TakeSeat"/> (the original's lines), run when the host's frame hands the seat over.</summary>
        void CoopSeatTakenHere(IEmbodiable next)
        {
            var pose = next.PoseAt(Clock.Jd);
            seatAim = ToVector(pose.Forward.SwapYZ).normalized;
            Clock.SetWarp(0);
            SpaceCamera.EnterPilotChase(SeatTarget(next), next is FighterSeat ? 7 : 2.5);
            if (next is MountedTurret gun)
            {
                SpaceCamera.PilotView = Quaternion.LookRotation(seatAim, MountRoof(pose, seatAim));
                SpaceCamera.PilotEye = () => MountEye(gun);
            }
            else SpaceCamera.PilotEye = null;
            ShowToast((next is FighterSeat ? "Flying " : "Manning ") + next.Name + ", " + Clock.Warp.Label + SeatKeys(next));
        }

        /// <summary>
        /// In co-op a take goes to the host as a command, with the seat's key; the view follows when a frame grants it
        /// (<see cref="CoopSessionEvent"/>). False in single-player: <see cref="TakeSeat"/> goes on as the original.
        /// </summary>
        bool CoopTakeSeat(IEmbodiable next)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return false;
            string key = CoopSeatKey(next);
            if (key == null)
            {
                ShowToast("Can't share " + next.Name + " in co-op: man a gun, a fighter or a station");
                return true;
            }
            // Id 0: the sender's session must not name the seat (that would hand out an id the others never saw).
            peer.Submit("s " + CommandCodec.Write(new TakeSeatCommand(session.LocalPlayer, 0)) + " @" + key);
            ShowToast("Asking for " + next.Name + "...");
            return true;
        }

        /// <summary>A release or a move up to the host, in co-op: to the host as a command (true); false in single-player.</summary>
        bool CoopRoutes(IGameCommand command)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return false;
            if (command is ReleaseSeatCommand && HeldNonHelm == null) return true; // nothing to hand back
            if (!peer.Applying) peer.Submit("s " + CommandCodec.Write(command));
            return true;
        }

        /// <summary>
        /// Co-op's <see cref="UpdateSeat"/>: this machine's stick goes to the host as the local player's control command
        /// (when it changes), and every player's seat is stepped, here as on every peer, on the frame's sim seconds. The
        /// cockpit's extra keys (<see cref="UpdateCockpit"/>: flight assist, locks, wing orders, power) act on the Core
        /// directly and are not shared yet, so they are off in co-op.
        /// </summary>
        void CoopUpdateSeat(float dt)
        {
            var peer = CoopPeer.Current;
            var held = HeldNonHelm;
            var rot = SpaceCamera.Rotation;
            var up = rot * Vector3.up;
            if (held != null)
            {
                var look = ClickPanelOpen ? Vector2.zero : Input.MouseDelta;
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
                string line = CommandCodec.Write(new SeatControlCommand(session.LocalPlayer, input));
                if (line != coopLastControl)
                {
                    coopLastControl = line;
                    peer.Submit("s " + line);
                }
                // US-T43: the cockpit's own keys (UpdateCockpit's: flight assist, a lock, flares, the number keys) as commands,
                // each carried out on every peer at its frame (CoopCockpitKey).
                if (held is FighterSeat && !ClickPanelOpen && CoopPeer.FreshEdges(ref coopKeyTick))
                {
                    string me = Wire.I(peer.LocalSeat);
                    if (Input.ToggleFlightAssist) peer.Submit("k " + me + " fa");
                    if (Input.LockTarget) peer.Submit("k " + me + " lock " + Wire.D(input.Aim.X) + " " + Wire.D(input.Aim.Y) + " " + Wire.D(input.Aim.Z));
                    if (Input.DropMine) peer.Submit("k " + me + " mine");
                    if (Input.NumberKey >= 0) peer.Submit("k " + me + " n " + Wire.I(Input.NumberKey));
                }
            }
            else coopLastControl = null;
            if (!coopStepped) return;
            session.Advance(dt * coopSimRate);
            var still = HeldNonHelm;
            if (still != null && still == held)
            {
                var pose = held.PoseAt(Clock.Jd);
                var roof = ToVector(pose.Up.SwapYZ);
                SpaceCamera.PilotView = Quaternion.LookRotation(seatAim, Vector3.Dot(roof, seatAim) > 0.99f ? up : roof);
            }
        }

        /// <summary>
        /// A trip to another star (US-T43): the party shares one system (the galaxy runs one at full detail, its Focus, and
        /// the helm, the law, the chase and the adventures are about one player point), so a trip is the party's, and none
        /// starts while another player holds a seat or the helm there (it would be torn from them). True: refused.
        /// </summary>
        bool CoopRefusesTravel(StarSystem system)
        {
            var peer = CoopPeer.Current;
            if (peer == null || coopExecuting > 0) return false;
            string other = CoopOtherAtControls(peer.LocalSeat);
            if (other == null) return false;
            ShowToast("The party travels together: " + other + " is at the controls in " + ActiveSystem.Name);
            return true;
        }

        /// <summary>The trip itself (the tail of <see cref="TravelTo"/>), into the frames: carried out at its frame on every
        /// peer, where the host's view makes it and every peer arrives with the host's Activate (a post step).</summary>
        bool CoopPartyTrip(StarSystem system)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return false;
            if (coopExecuting > 0) return peer.IsClient; // a client goes with the host's view: its Activate comes in a frame
            ShowToast("Asking for the party's trip to " + system.Name + "...");
            return CoopSends("travel", system.Name);
        }

        /// <summary>The name of a player other than <paramref name="seat"/> holding a seat or the helm, or null.</summary>
        string CoopOtherAtControls(int seat)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return null;
            if (coopHelmSeat >= 0 && coopHelmSeat != seat && (!Piloted.IsNone || InFtlCruise)) return peer.NameOf(coopHelmSeat);
            foreach (var p in session.Players)
                if (p.Seat != seat && session.State.SeatOf(p) != null) return peer.NameOf(p.Seat);
            return null;
        }

        /// <summary>Whether this player is at some controls, or in co-op anyone is (a reset of time or a trip would take them).</summary>
        bool CoopAtControls()
        {
            if (CoopPeer.Current == null) return AtControls;
            if (coopExecuting == 0 && AtControls) return true;
            return CoopOtherAtControls(-1) != null;
        }

        /// <summary>At the end of <see cref="Activate"/>: the host's frame tells clients to make the same system the active one
        /// (not for an Activate every peer makes itself: a synced load, an entry carried out everywhere, the shared helm).</summary>
        void CoopActivated(StarSystem system)
        {
            var peer = CoopPeer.Current;
            if (peer == null || !peer.IsHost || peer.Applying || peer.Open == null || coopEverywhere > 0) return;
            peer.Open.Post.Add("activate " + system.Name);
        }

        /// <summary>At the end of <see cref="ApplyLoaded"/>: the clock is a new one; a load the host made itself goes to everyone.</summary>
        void CoopLoaded()
        {
            coopLeftValid = false;
            coopLastControl = null;
            var peer = CoopPeer.Current;
            if (peer == null || peer.Applying) return;
            // US-T43: a helm in the host's own save stays with whoever flew the helm before it, else the host.
            if (Piloted.IsNone && !InFtlCruise) coopHelmSeat = -1;
            else if (coopHelmSeat < 0) coopHelmSeat = 0;
            if (peer.IsHost)
            {
                peer.Open?.Post.Clear();
                peer.RequestSync("the host loaded a save", load: true);
            }
        }

        /// <summary>What a client does after replaying a host frame's Update: the frame's post steps.</summary>
        public void CoopApplyPost(FrameRecord rec)
        {
            foreach (var p in rec.Post)
            {
                if (!p.StartsWith("activate ", StringComparison.Ordinal)) continue;
                var system = Galaxy.Find(p.Substring(9));
                if (system == null || system == ActiveSystem) continue;
                Activate(system);
                // US-T43: the host's view made the trip; this one arrives with it, on the system's overview (as StarTravel ends).
                if (!AtControls)
                {
                    SetSystemVisible(true);
                    FocusSystemOverview();
                }
                CoopPeer.Log("arrived in " + system.Name + " with the host's frame " + rec.Frame);
            }
        }

        // ---------------------------------------------------------------- keys for seats and targets

        /// <summary>
        /// A seat by what it is in the shared world, so every peer finds the same one: "F b u" a fighter's cockpit (battle b
        /// of the active system's war, its unit u), "T b u m" a gun (mount m of that unit), "S i" a station (i-th of
        /// <see cref="Embodiables.StationsIn"/>), "SB b u" a station fighting as unit u. Null for one co-op does not share.
        /// </summary>
        public string CoopSeatKey(IEmbodiable seat)
        {
            var battles = World.War.Battles;
            switch (seat)
            {
                case FighterSeat f:
                {
                    int b = battles.IndexOf(f.Battle), u = f.Battle.Units.IndexOf(f.Unit);
                    return b < 0 || u < 0 ? null : "F " + b + " " + u;
                }
                case MountedTurret t:
                {
                    int b = battles.IndexOf(t.Battle), u = t.Battle.Units.IndexOf(t.Unit), m = t.Unit.Combat.Subsystems.IndexOf(t.Mount);
                    return b < 0 || u < 0 || m < 0 ? null : "T " + b + " " + u + " " + m;
                }
                case StationSeat s:
                {
                    int i = Embodiables.StationsIn(World).IndexOf(s);
                    if (i >= 0) return "S " + i;
                    var unit = s.Unit(out var battle);
                    int b = battle != null ? battles.IndexOf(battle) : -1, u = unit != null && battle != null ? battle.Units.IndexOf(unit) : -1;
                    return b < 0 || u < 0 ? null : "SB " + b + " " + u;
                }
                default:
                    return null;
            }
        }

        public IEmbodiable CoopResolveSeat(string key)
        {
            try
            {
                var f = key.Split(' ');
                var battles = World.War.Battles;
                Battle Battle(int i) => i >= 0 && i < battles.Count ? battles[i] : null;
                BattleUnit Unit(Battle b, int i) => b != null && i >= 0 && i < b.Units.Count ? b.Units[i] : null;
                switch (f[0])
                {
                    case "F":
                    {
                        var b = Battle(Wire.ParseI(f[1]));
                        var u = Unit(b, Wire.ParseI(f[2]));
                        return u == null ? null : Embodiables.FighterOf(b, u);
                    }
                    case "T":
                    {
                        var b = Battle(Wire.ParseI(f[1]));
                        var u = Unit(b, Wire.ParseI(f[2]));
                        int m = Wire.ParseI(f[3]);
                        if (u == null || m < 0 || m >= u.Combat.Subsystems.Count) return null;
                        var mount = u.Combat.Subsystems[m];
                        foreach (var gun in Embodiables.TurretsOn(World, b, u))
                            if (gun.Mount == mount) return gun;
                        return null;
                    }
                    case "S":
                    {
                        var all = Embodiables.StationsIn(World);
                        int i = Wire.ParseI(f[1]);
                        return i >= 0 && i < all.Count ? all[i] : null;
                    }
                    case "SB":
                    {
                        var b = Battle(Wire.ParseI(f[1]));
                        return Embodiables.StationOf(World, b, Unit(b, Wire.ParseI(f[2])));
                    }
                }
            }
            catch (Exception e)
            {
                CoopPeer.Warn("bad seat key '" + key + "': " + e.Message);
            }
            return null;
        }

        /// <summary>A selection as every peer can read it back: "n", "b &lt;body&gt;", "s &lt;ship id&gt;", "u &lt;battle&gt; &lt;unit&gt;", else "o &lt;kind&gt;".</summary>
        public string CoopTargetKey(Target t)
        {
            if (t.IsNone) return "n";
            if (t.IsBody) return "b " + t.Body;
            if (t.IsShip) return "s " + t.Ship.Id;
            if (t.IsUnit)
            {
                int b = World.War.Battles.IndexOf(t.Battle), u = t.Battle.Units.IndexOf(t.Unit);
                if (b >= 0 && u >= 0) return "u " + b + " " + u;
            }
            return "o " + t.Kind;
        }

        /// <summary>A selection key's name in the active system, for the HUD.</summary>
        public string CoopTargetName(string key)
        {
            try
            {
                var f = key.Split(' ');
                switch (f[0])
                {
                    case "n": return null;
                    case "b": { int i = Wire.ParseI(f[1]); return i >= 0 && i < Catalog.Count ? Catalog[i].Name : "a body"; }
                    case "s": { int i = Wire.ParseI(f[1]); return i >= 0 && i < Fleet.Ships.Count ? Fleet.Ships[i].Name : "a ship"; }
                    case "u":
                    {
                        int b = Wire.ParseI(f[1]), u = Wire.ParseI(f[2]);
                        var bs = World.War.Battles;
                        return b < bs.Count && u < bs[b].Units.Count ? bs[b].Units[u].Name : "a unit";
                    }
                    default: return f.Length > 1 ? "a " + f[1].ToLowerInvariant() : "something";
                }
            }
            catch (Exception) { return "something"; }
        }

        // ---------------------------------------------------------------- saves, digest, presence

        /// <summary>Why a sync save cannot be taken now (between the stars, under FTL), or null.</summary>
        public string CoopSaveBlocker() => SaveBlocker();

        /// <summary>The whole simulation as Core/Persistence writes it (what a save slot holds).</summary>
        public byte[] CoopCaptureSave() => SaveGame.ToBytes(CaptureSave(), "coop", "Co-op sync");

        /// <summary>Loads those bytes as a save is loaded (the original's ApplyLoaded): every peer's world from the same bytes;
        /// then (US-T43) the sync's state: the host's shared settings, who flies the helm the save holds, and every player's
        /// seat taken again, in seat order, so every peer's SeatAuthority names them with the same ids.</summary>
        public void CoopApplySave(byte[] bytes, string state)
        {
            var lines = new List<string>((state ?? "").Split('\n'));
            int helm = -1;
            var seats = new List<(int Player, string Key)>();
            var settings = new List<string>();
            foreach (var l in lines)
            {
                if (l.StartsWith("helm ", StringComparison.Ordinal)) helm = Wire.ParseI(l.Substring(5));
                else if (l.StartsWith("seat ", StringComparison.Ordinal))
                {
                    var f = l.Split(new[] { ' ' }, 3);
                    seats.Add((Wire.ParseI(f[1]), f[2]));
                }
                else if (l.Length > 0) settings.Add(l);
            }
            int changed = CoopSettings.ApplySnapshot(settings);
            if (changed > 0) CoopPeer.Log("took " + changed + " of the host's shared settings");
            // A resync (not a load) leaves this player's view as it was: the same system, scale, selection and camera.
            bool keepView = lines.Contains("keepview");
            string system = ActiveSystem?.Name, selected = CoopTargetKey(Selected), focus = CoopTargetKey(SpaceCamera.FocusTarget);
            var mode = SpaceCamera.Mode;
            var pose = (SpaceCamera.ScenePosition, SpaceCamera.Rotation);
            var scale = (ScaleGoal, scaleFrom, scaleT, Scale.Blend);
            coopHelmSeat = helm;
            coopEverywhere++;
            coopShared++;
            try { ApplyLoaded(SaveGame.FromBytes(bytes)); }
            finally
            {
                coopShared--;
                coopEverywhere--;
            }
            if (Piloted.IsNone && !InFtlCruise) coopHelmSeat = -1;
            CoopWatchSession();
            if (keepView && system == ActiveSystem.Name)
            {
                (ScaleGoal, scaleFrom, scaleT, Scale.Blend) = scale;
                Scale.Layout(Ephemeris, bodyScene);
                UpdateRadii();
                Selected = CoopResolveTarget(selected);
                if (!CoopHelmMine || Piloted.IsNone)
                {
                    SpaceCamera.ExitFocus();
                    SpaceCamera.SetPose(pose.Item1, pose.Item2);
                    var f = CoopResolveTarget(focus);
                    if (mode != SpaceCamera.CameraMode.Free && !f.IsNone && !f.IsUnit) SpaceCamera.FocusOn(f);
                }
            }
            CoopRetake(seats);
        }

        /// <summary>The seats of <paramref name="seats"/> taken again on the fresh session after a sync's load, on every peer
        /// in the same order (the clock's warp kept: a seat taken drops to real time, which a resync must not do).</summary>
        void CoopRetake(List<(int Player, string Key)> seats)
        {
            var peer = CoopPeer.Current;
            if (seats.Count == 0 || peer == null) return;
            var keep = ClockState.Of(Clock);
            foreach (var (player, key) in seats)
            {
                var seat = CoopResolveSeat(key);
                if (seat == null)
                {
                    CoopPeer.Log(peer.NameOf(player) + "'s seat '" + key + "' is not in the loaded world: back in command");
                    continue;
                }
                session.Submit(new TakeSeatCommand(new PlayerId(player), session.IdOf(seat)));
                session.ApplyPending();
                peer.Retakes++;
                CoopPeer.Log(peer.NameOf(player) + " (player " + (player + 1) + ") keeps seat " + session.State.SeatIdOf(new PlayerId(player)) + " (" + seat.Name + ") through the sync");
            }
            keep.ApplyTo(Clock);
        }

        /// <summary>What a sync carries besides the save (US-T43): "helm &lt;seat&gt;" and a "seat &lt;player&gt; &lt;key&gt;" per
        /// player holding one (<see cref="CoopSeatKey"/>), before the shared settings' lines.</summary>
        public string CoopSyncState(bool keepView)
        {
            var sb = new StringBuilder();
            if (keepView) sb.Append("keepview\n");
            sb.Append("helm ").Append(Piloted.IsNone && !InFtlCruise ? -1 : coopHelmSeat);
            foreach (var p in session.Players)
            {
                var held = session.State.SeatOf(p);
                string key = held != null ? CoopSeatKey(held) : null;
                if (key != null) sb.Append("\nseat ").Append(p.Seat).Append(' ').Append(key);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A cheap digest of the shared state (the idea of Bench/CSharp's, much smaller): the clock as the frame advanced it,
        /// every system's date, detail and counts, every faction's treasury, and in the active system every ship's position,
        /// every settlement's people and stock, every battle unit, and who holds which seat; hashed with FNV-1a.
        /// </summary>
        public string CoopDigest()
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("clock ").Append((coopLeftValid ? coopLeft : ClockState.Of(Clock)).Write()).Append('\n');
            sb.Append("active ").Append(ActiveSystem.Name).Append('\n');
            foreach (var sys in Galaxy.Systems)
            {
                var w = sys.World;
                sb.Append("system ").Append(sys.Name).Append(' ').Append(Wire.D(w.Jd)).Append(w.Coarse ? " c " : " f ")
                  .Append(w.Fleet.Ships.Count).Append(' ').Append(w.Economy.Settlements.Count).Append(' ').Append(w.War.Battles.Count)
                  .Append(' ').Append(w.Events.Count);
                foreach (var f in w.Fleet.Factions) sb.Append(' ').Append(Wire.D(f.Treasury));
                sb.Append('\n');
            }
            var world = World;
            double jd = world.Jd;
            foreach (var s in world.Fleet.Ships)
            {
                sb.Append("ship ").Append(s.Id).Append(' ').Append(s.Phase).Append(s.Destroyed ? " x" : "");
                if (!s.Destroyed)
                {
                    try
                    {
                        var st = world.Fleet.StateOf(s, jd);
                        sb.Append(' ').Append(Wire.D(st.HelioKm.X)).Append(' ').Append(Wire.D(st.HelioKm.Y)).Append(' ').Append(Wire.D(st.HelioKm.Z));
                    }
                    catch (Exception) { sb.Append(" !"); }
                }
                sb.Append('\n');
            }
            foreach (var st in world.Economy.Settlements)
            {
                sb.Append("settlement ").Append(st.Id).Append(' ').Append(Wire.D(st.Population));
                foreach (var k in st.Stock) sb.Append(' ').Append(Wire.D(k));
                sb.Append('\n');
            }
            foreach (var b in world.War.Battles)
                foreach (var u in b.Units)
                    sb.Append("unit ").Append(u.Id).Append(u.Present ? " p " : " - ").Append(Wire.D(u.Pos.X)).Append(' ').Append(Wire.D(u.Pos.Y))
                      .Append(' ').Append(Wire.D(u.Pos.Z)).Append(' ').Append(u.Combat != null ? Wire.D(u.Combat.Hull) : "-").Append('\n');
            foreach (var p in session.Players) sb.Append("seat ").Append(p.Seat).Append(' ').Append(session.State.SeatIdOf(p)).Append('\n');
            // US-T43: the helm, as every peer flies it: who, which hull, where and how fast, its tanks and hold, the course.
            sb.Append("helm ").Append(coopHelmSeat).Append(' ').Append(Piloted.IsNone ? -1 : Piloted.Ship.Id);
            if (!Piloted.IsNone)
            {
                var ps = Piloted.Ship;
                sb.Append(' ').Append(pilotFrameBody).Append(' ').Append(Wire.D(pilotOffsetKm.X)).Append(' ').Append(Wire.D(pilotOffsetKm.Y)).Append(' ').Append(Wire.D(pilotOffsetKm.Z))
                  .Append(' ').Append(Wire.D(pilotVelKmS.X)).Append(' ').Append(Wire.D(pilotVelKmS.Y)).Append(' ').Append(Wire.D(pilotVelKmS.Z))
                  .Append(' ').Append(Wire.D(pilot.Forward.X)).Append(' ').Append(Wire.D(pilot.Forward.Y)).Append(' ').Append(Wire.D(pilot.Forward.Z))
                  .Append(' ').Append(Wire.D(ps.Propellant)).Append(' ').Append(ps.Cargo ?? "-").Append(' ').Append(Wire.D(ps.Credits)).Append(pilotFlightAssistOff ? " fa-off" : " fa-on");
            }
            sb.Append(' ').Append(Course?.Destination?.Name ?? "-").Append(' ').Append(PlottedCourse != null ? "ftl" : "-").Append(InFtlCruise ? " cruise" : "").Append('\n');
            CoopDigestStatecraft(sb); // US-T47
            return Wire.Hex(Wire.Fnv1a64(sb.ToString()));
        }

        /// <summary>US-T47: the strategy layer and statecraft, as the galaxy map's screens write them: the polity the party
        /// speaks for, every polity's directives, the objectives, pacts, proposals waiting and the record, and between every
        /// two states how they stand and what each thinks of the other.</summary>
        void CoopDigestStatecraft(StringBuilder sb)
        {
            var polities = Galaxy.Polities;
            var command = polities.Command;
            var statecraft = polities.Statecraft;
            var factions = polities.Factions;
            int player = -1;
            for (int i = 0; i < factions.Count; i++)
                if (factions[i] == command.Player) player = i;
            sb.Append("polities ").Append(player).Append(' ').Append(command.ObjectiveCount).Append(' ').Append(statecraft.Pacts.Count)
              .Append(' ').Append(statecraft.Inbox.Count).Append(' ').Append(statecraft.Record.Count).Append('\n');
            for (int i = 0; i < factions.Count; i++)
            {
                var d = command.Find(factions[i]);
                if (d == null) continue;
                sb.Append("directives ").Append(i).Append(' ').Append(d.Production.Name).Append(" / ").Append(d.Build.Name).Append(" / ")
                  .Append(d.Research?.Name ?? "-").Append(' ').Append(Wire.D(d.ResearchPoints));
                foreach (var t in ResearchTrack.All) sb.Append(' ').Append(d.Level(t));
                sb.Append('\n');
            }
            for (int i = 0; i < factions.Count; i++)
            {
                if (!Statecraft.Party(factions[i])) continue;
                for (int j = i + 1; j < factions.Count; j++)
                {
                    if (!Statecraft.Party(factions[j])) continue;
                    sb.Append("states ").Append(i).Append(' ').Append(j).Append(' ').Append((int)polities.Diplomacy.Get(factions[i], factions[j]))
                      .Append(' ').Append(Wire.D(statecraft.Opinion(factions[i], factions[j]))).Append(' ').Append(Wire.D(statecraft.Opinion(factions[j], factions[i]))).Append('\n');
                }
            }
        }

        /// <summary>Where this machine's player is, for the others' markers: the seat held, else the camera, as a body of the
        /// active system and a true-km offset from it (every peer lays its own scene out at its own scale).</summary>
        public (int Body, DVec3 OffsetKm) CoopPresence()
        {
            var held = HeldNonHelm;
            if (held != null)
            {
                var pose = held.PoseAt(Clock.Jd);
                return (pose.FrameBody, pose.OffsetKm);
            }
            int frame = SpaceCamera.FrameAt(SpaceCamera.ScenePosition);
            return (frame, Scale.InverseLocalOffset(Catalog[frame], SpaceCamera.ScenePosition - BodyScene(frame)));
        }

        /// <summary>Co-op's HUD (<see cref="CoopHud"/>'s OnGUI): who is playing, what each holds and selected, and a marker
        /// with the name on every other player where they are.</summary>
        internal void CoopGui()
        {
            var peer = CoopPeer.Current;
            if (peer == null || !ShowHud || ScreenCleared) return;
            var color = GUI.color;
            float x = Screen.width - 340, y = 6;
            GUI.color = new Color(0.55f, 0.85f, 1f, 1f);
            GUI.Label(new Rect(x, y, 334, 20), "CO-OP  ·  " + (peer.IsHost ? "hosting" : "joined " + peer.NameOf(0)) + "  ·  frame " + peer.Frame);
            y += 18;
            foreach (var p in peer.Players)
            {
                if (p == null || p.Left) continue;
                var seat = session.State.SeatOf(p.Id);
                string sel = CoopTargetName(p.Selection);
                bool helm = p.Seat == coopHelmSeat && !Piloted.IsNone; // US-T43: the party's one helm
                string line = "P" + (p.Seat + 1) + "  " + p.Name + (p.Seat == 0 ? " (host)" : "") + (p.Seat == peer.LocalSeat ? " (you)" : "") +
                              "  ·  " + (helm ? "at the helm of " + Piloted.Ship.Name : seat != null ? seat.Name : "in command") + (sel != null ? "  ·  " + sel : "");
                GUI.Label(new Rect(x, y, 334, 20), line);
                y += 18;
                if (p.Seat == peer.LocalSeat || p.Body < 0 || p.Body >= Catalog.Count || !SystemVisible) continue;
                var scene = BodyScene(p.Body) + Scale.LocalOffset(Catalog[p.Body], p.OffsetKm);
                if (ToGui(scene, out var g, out _)) GUI.Label(new Rect(g.x - 4, g.y - 10, 220, 20), "+ P" + (p.Seat + 1) + " " + p.Name);
            }
            GUI.color = color;
        }
    }

    // ==================================================================== US-T43: the helm, the cockpit, and the Core's other writers

    public sealed partial class UnitySpaceApp
    {
        /// <summary>The player flying the helm (the app's one pilot loop, <see cref="Piloted"/>), or -1. Every peer flies it,
        /// on that player's input; only that player's machine treats it as its own (<see cref="IsPiloting"/> outside the
        /// shared steps, its view, its toasts).</summary>
        int coopHelmSeat = -1;

        /// <summary>Above 0 inside a step every peer runs alike on the shared state (the pilot loop, a frame's entry carried
        /// out, a synced load): there the helm is everyone's.</summary>
        int coopShared;

        /// <summary>Above 0 where every peer makes the same Activate itself (no post step needed).</summary>
        int coopEverywhere;

        /// <summary>Above 0 while a frame's "x" entry is carried out (its own hooks do not send it again).</summary>
        int coopExecuting;
        bool coopExecMine;

        /// <summary>Above 0 while another player's action or helm is replayed here: their toasts are theirs.</summary>
        int coopMuteToasts;

        /// <summary>This frame's hands at the helm (the holder's), from the frame's "pi" entry.</summary>
        PilotFrame coopPilotFrame;
        bool? coopPanelOverride;
        long coopKeyTick = -1;

        static float CoopLookSensitivity = float.NaN;

        /// <summary>The camera up frame a fresh helm is levelled on, while the holder's frame is flown (its camera's, not this one's).</summary>
        static Quaternion? CoopUpFrame;

        /// <summary>The asker's "now" while a reset of time is carried out (every peer resets to the same date).</summary>
        double? CoopNow;

        public int CoopHelmSeat => coopHelmSeat;
        bool CoopHelmHere => CoopPeer.Current == null || coopShared > 0 || coopHelmSeat == CoopPeer.Current.LocalSeat;
        bool CoopHelmMine => CoopPeer.Current == null || coopHelmSeat == CoopPeer.Current.LocalSeat;
        bool CoopIsClient => CoopPeer.Current != null && CoopPeer.Current.IsClient;
        bool CoopExecuting => coopExecuting > 0;

        /// <summary>Whether this machine is the one acting (single-player, its own input, or its own entry carried out).</summary>
        bool CoopActing => CoopPeer.Current == null || coopExecuting == 0 || coopExecMine;

        /// <summary>The HUD (its "x" entries are carried out through it).</summary>
        internal Hud CoopHud => hud;

        internal bool CoopActingHere => CoopActing;

        /// <summary>A target key (<see cref="CoopTargetKey"/>) read back in the active system.</summary>
        internal Target CoopTarget(string key) => CoopResolveTarget(key);

        void CoopShared(Action step)
        {
            if (CoopPeer.Current == null) { step(); return; }
            coopShared++;
            try { step(); }
            finally { coopShared--; }
        }

        /// <summary>
        /// In co-op an action that writes the Core goes into the frames as "x &lt;seat&gt; &lt;verb&gt; &lt;args&gt;" and is carried
        /// out at its frame on every peer (<see cref="CoopExecute"/>), which calls the same method again: there (and in
        /// single-player) this is false and the original goes on.
        /// </summary>
        internal bool CoopSends(string verb, string args = "")
        {
            var peer = CoopPeer.Current;
            if (peer == null || coopExecuting > 0 || coopShared > 0 || peer.Applying) return false;
            peer.Submit("x " + Wire.I(peer.LocalSeat) + " " + verb + (string.IsNullOrEmpty(args) ? "" : " " + args));
            return true;
        }

        /// <summary>A spawn at this machine's camera (the dev grid, the practice fighter): where, in true km, goes with it.</summary>
        bool CoopSpawns(string verb, string args, int frame, DVec3 offsetKm, DVec3 forward)
        {
            string at = Wire.I(frame) + " " + Wire.D(offsetKm.X) + " " + Wire.D(offsetKm.Y) + " " + Wire.D(offsetKm.Z) + " " +
                        Wire.D(forward.X) + " " + Wire.D(forward.Y) + " " + Wire.D(forward.Z) + " " + CoopUpFrameText(SpaceCamera.UpFrame);
            return CoopSends(verb, (string.IsNullOrEmpty(args) ? "" : args + " ") + at);
        }

        static string CoopUpFrameText(Quaternion q) => Wire.F(q.x) + "," + Wire.F(q.y) + "," + Wire.F(q.z) + "," + Wire.F(q.w);

        static Quaternion CoopParseUpFrame(string s)
        {
            var c = s.Split(',');
            return new Quaternion(Wire.ParseF(c[0]), Wire.ParseF(c[1]), Wire.ParseF(c[2]), Wire.ParseF(c[3]));
        }

        // ---------------------------------------------------------------- the helm: taken, released, flown

        /// <summary>A take of the helm from this machine's input (P, a seat cycled, the picker, a spawn) goes into the frames
        /// as "h &lt;seat&gt; take &lt;ship&gt; &lt;up frame&gt;"; every peer takes it at that frame (<see cref="CoopHelmEntry"/>).</summary>
        bool CoopRoutesHelmTake(Target t)
        {
            var peer = CoopPeer.Current;
            if (peer == null || coopShared > 0 || coopExecuting > 0 || peer.Applying || !t.IsShip) return false;
            if (coopHelmSeat >= 0 && coopHelmSeat != peer.LocalSeat && (!Piloted.IsNone || InFtlCruise))
            {
                ShowToast(peer.NameOf(coopHelmSeat) + " has the helm: the party flies one hull (man a gun, a fighter or a station meanwhile)");
                return true;
            }
            peer.Submit("h " + Wire.I(peer.LocalSeat) + " take " + Wire.I(t.Ship.Id) + " " + CoopUpFrameText(SpaceCamera.UpFrame));
            ShowToast("Asking for the helm of " + t.Ship.Name + "...");
            return true;
        }

        /// <summary>A release from this machine's input (P, another seat taken): into the frames; the deterministic losses
        /// (destroyed, boarded, a gate, the crew's course) happen inside the shared steps and are not routed.</summary>
        bool CoopRoutesHelmRelease()
        {
            var peer = CoopPeer.Current;
            if (peer == null || coopShared > 0 || coopExecuting > 0 || peer.Applying) return false;
            if (coopHelmSeat == peer.LocalSeat) peer.Submit("h " + Wire.I(peer.LocalSeat) + " release");
            return true;
        }

        /// <summary>A frame's "h" entry, on every peer: the take is refused if another player holds the helm or it cannot be
        /// taken now; else the original's <see cref="EnterPiloting"/> runs, on the taker's up frame.</summary>
        void CoopHelmEntry(CoopPeer peer, int seat, string[] f)
        {
            bool mine = seat == peer.LocalSeat;
            coopShared++;
            coopEverywhere++;
            if (!mine) coopMuteToasts++;
            try
            {
                if (f[2] == "release")
                {
                    if (coopHelmSeat != seat) return;
                    if (!Piloted.IsNone) ExitPiloting();
                    coopHelmSeat = -1;
                    peer.ForgetPilot(seat);
                    CoopPeer.Log(peer.NameOf(seat) + " (player " + (seat + 1) + ") hands the helm back at frame " + peer.Frame);
                    return;
                }
                int id = Wire.ParseI(f[3]);
                var ship = id >= 0 && id < Fleet.Ships.Count ? Fleet.Ships[id] : null;
                string refused = ship == null || ship.Destroyed ? "that hull is gone"
                    : coopHelmSeat >= 0 && coopHelmSeat != seat && (!Piloted.IsNone || InFtlCruise) ? peer.NameOf(coopHelmSeat) + " has the helm"
                    : !Embodiables.HelmOf(World, ship).CanEmbody ? "can't take the helm of that ship right now" : null;
                if (refused != null)
                {
                    CoopPeer.Log(peer.NameOf(seat) + "'s take of the helm refused: " + refused);
                    ShowToast("Can't: " + refused);
                    return;
                }
                if (!Piloted.IsNone) ExitPiloting();
                coopHelmSeat = seat;
                CoopUpFrame = f.Length > 4 ? CoopParseUpFrame(f[4]) : Quaternion.identity;
                try { EnterPiloting(Target.OfShip(ship)); }
                finally { CoopUpFrame = null; }
                CoopPeer.Log(peer.NameOf(seat) + " (player " + (seat + 1) + ") takes the helm of " + ship.Name + " (ship " + ship.Id + ") at frame " + peer.Frame);
            }
            finally
            {
                coopShared--;
                coopEverywhere--;
                if (!mine) coopMuteToasts--;
            }
        }

        /// <summary>This machine's hands now, as the helm's frame carries them.</summary>
        PilotFrame CoopLocalPilotFrame()
        {
            var p = PilotFrame.Of(Input, SpaceCamera.LookY);
            p.Sensitivity = SpaceCamera.LookSensitivity;
            p.PanelOpen = ClickPanelOpen;
            p.Selected = CoopTargetKey(Selected);
            p.Hovered = CoopTargetKey(Hovered);
            p.UpFrame = SpaceCamera.UpFrame;
            return p;
        }

        /// <summary>The host's frame record gets the helm holder's hands: its own, or what the holder (a client) sent since.</summary>
        void CoopHostPilotFrame(CoopPeer peer, FrameRecord rec)
        {
            coopPilotFrame = null;
            if (coopHelmSeat < 0) return;
            var frame = coopHelmSeat == peer.LocalSeat ? CoopLocalPilotFrame() : peer.HostPilotFor(coopHelmSeat);
            string text = frame.Write();
            rec.Entries.Add("pi " + Wire.I(coopHelmSeat) + " " + text);
            coopPilotFrame = PilotFrame.Read(text); // the same numbers the clients read
            CoopPilotFrames++;
        }

        public int CoopPilotFrames { get; private set; }

        /// <summary>
        /// <see cref="UpdatePiloting"/> (the original's whole pilot loop: flight, collisions, the battle, the law, the chase,
        /// gates, FTL, lanes) on every peer alike, on the helm holder's frame of input: this machine's keys, panels,
        /// sensitivity, selection and hover are put aside for it and back after. The holder (a client) sends its own hands
        /// first; they reach the loop when the host's frame brings them back, as every peer's do.
        /// </summary>
        void CoopUpdatePiloting(float dt)
        {
            var peer = CoopPeer.Current;
            if (peer == null)
            {
                UpdatePiloting(dt);
                return;
            }
            if (peer.IsClient && peer.ClientRecord == null) return;
            if (peer.IsClient && coopHelmSeat == peer.LocalSeat) peer.SendPilot(CoopLocalPilotFrame());
            bool mine = CoopHelmMine;
            var frame = coopHelmSeat >= 0 ? coopPilotFrame ?? PilotFrame.Idle() : PilotFrame.Idle();
            var live = PilotFrame.Snapshot(Input);
            var selected = Selected;
            var hovered = Hovered;
            var view = SpaceCamera.PilotView;
            var lockOn = SpaceCamera.PilotLockTarget;
            var eye = SpaceCamera.PilotEye;
            frame.ApplyTo(Input, SpaceCamera.LookY);
            coopPanelOverride = frame.PanelOpen;
            CoopLookSensitivity = coopHelmSeat >= 0 ? frame.Sensitivity : float.NaN;
            CoopUpFrame = frame.UpFrame;
            var flownSelected = Selected = CoopResolveTarget(frame.Selected);
            Hovered = CoopResolveTarget(frame.Hovered);
            coopShared++;
            coopEverywhere++;
            if (!mine) coopMuteToasts++;
            try { UpdatePiloting(dt); }
            finally
            {
                coopShared--;
                coopEverywhere--;
                if (!mine) coopMuteToasts--;
                PilotFrame.Restore(Input, live);
                coopPanelOverride = null;
                CoopLookSensitivity = float.NaN;
                CoopUpFrame = null;
                // What the loop selected (a gate's arriving hull) is the holder's to keep; the rest put back.
                if (!mine || Selected == flownSelected) Selected = selected;
                Hovered = hovered;
                if (!mine)
                {
                    SpaceCamera.PilotView = view;
                    SpaceCamera.PilotLockTarget = lockOn;
                    SpaceCamera.PilotEye = eye;
                }
                if (Piloted.IsNone && !InFtlCruise && coopHelmSeat >= 0)
                {
                    CoopPeer.Log(peer.NameOf(coopHelmSeat) + "'s helm ends at frame " + peer.Frame);
                    peer.ForgetPilot(coopHelmSeat);
                    coopHelmSeat = -1;
                }
            }
        }

        /// <summary>A selection key read back (<see cref="CoopTargetKey"/>), or none.</summary>
        Target CoopResolveTarget(string key)
        {
            try
            {
                var f = (key ?? "n").Split(' ');
                switch (f[0])
                {
                    case "b": { int i = Wire.ParseI(f[1]); return i >= 0 && i < Catalog.Count ? Target.OfBody(i) : Target.None; }
                    case "s":
                    {
                        int i = Wire.ParseI(f[1]);
                        return i >= 0 && i < Fleet.Ships.Count && !Fleet.Ships[i].Destroyed ? Target.OfShip(Fleet.Ships[i]) : Target.None;
                    }
                    case "u":
                    {
                        int b = Wire.ParseI(f[1]), u = Wire.ParseI(f[2]);
                        var bs = World.War.Battles;
                        return b >= 0 && b < bs.Count && u >= 0 && u < bs[b].Units.Count ? Target.OfUnit(bs[b], bs[b].Units[u]) : Target.None;
                    }
                    default: return Target.None;
                }
            }
            catch (Exception) { return Target.None; }
        }

        // ---------------------------------------------------------------- a frame's app entries

        /// <summary>The app's entries of a frame (true: one of them): "h" the helm, "pi" its hands, "k" a cockpit key, "x" an
        /// action, "set" a shared setting, and on the host a client's "a &lt;seat&gt; load &lt;slot&gt;".</summary>
        bool CoopAppEntry(CoopPeer peer, string e)
        {
            int sp = e.IndexOf(' ');
            string kind = sp < 0 ? e : e.Substring(0, sp);
            switch (kind)
            {
                case "h":
                case "pi":
                case "k":
                case "x":
                case "set":
                    break;
                case "a":
                {
                    var a = e.Split(new[] { ' ' }, 4);
                    if (a.Length == 4 && a[2] == "load" && peer.IsHost)
                    {
                        ShowToast(peer.NameOf(Wire.ParseI(a[1])) + " asks to load " + SlotName(a[3]));
                        RequestLoad(a[3]);
                    }
                    return false; // the roster logs it
                }
                default:
                    return false;
            }
            session.ApplyPending(); // the session's commands before this one, in the frame's order
            try
            {
                if (kind == "set")
                {
                    var s = e.Split(' ');
                    if (s.Length >= 3) CoopSettings.Apply(s[1], s[2]);
                    return true;
                }
                var f = e.Split(new[] { ' ' }, kind == "x" ? 4 : kind == "pi" ? 3 : 5);
                int seat = Wire.ParseI(f[1]);
                switch (kind)
                {
                    case "h": CoopHelmEntry(peer, seat, e.Split(' ')); break;
                    case "pi": if (peer.IsClient && seat == coopHelmSeat) coopPilotFrame = PilotFrame.Read(f[2]); break;
                    case "k": CoopCockpitKey(peer, seat, e.Split(' ')); break;
                    case "x": CoopExecute(peer, seat, f[2], f.Length > 3 ? f[3] : ""); break;
                }
            }
            catch (Exception x)
            {
                CoopPeer.Warn("entry '" + (e.Length > 80 ? e.Substring(0, 80) : e) + "' failed: " + x);
            }
            return true;
        }

        public int CoopCockpitKeys { get; private set; }
        public int CoopActions { get; private set; }

        /// <summary>A cockpit key at its frame on every peer: the original's <see cref="UpdateCockpit"/> run with that key
        /// alone pressed, on the fighter that player holds.</summary>
        void CoopCockpitKey(CoopPeer peer, int seat, string[] f)
        {
            if (!(session.State.SeatOf(new PlayerId(seat)) is FighterSeat cockpit))
            {
                CoopPeer.Log(peer.NameOf(seat) + "'s cockpit key '" + f[2] + "' with no cockpit held: ignored");
                return;
            }
            bool mine = seat == peer.LocalSeat;
            var live = PilotFrame.Snapshot(Input);
            PilotFrame.Idle().ApplyTo(Input, null);
            var aim = DVec3.Zero;
            switch (f[2])
            {
                case "fa": Input.ToggleFlightAssist = true; break;
                case "lock":
                    Input.LockTarget = true;
                    aim = new DVec3(Wire.ParseD(f[3]), Wire.ParseD(f[4]), Wire.ParseD(f[5]));
                    break;
                case "mine": Input.DropMine = true; break;
                case "n": Input.NumberKey = Wire.ParseI(f[3]); break;
            }
            coopShared++;
            if (!mine) coopMuteToasts++;
            try { UpdateCockpit(cockpit, aim); }
            finally
            {
                coopShared--;
                if (!mine) coopMuteToasts--;
                PilotFrame.Restore(Input, live);
            }
            CoopCockpitKeys++;
            CoopPeer.Log(peer.NameOf(seat) + " (player " + (seat + 1) + ") cockpit key '" + string.Join(" ", f, 2, Math.Min(2, f.Length - 2)) + "' on " + cockpit.Name + " at frame " + peer.Frame);
        }

        /// <summary>
        /// An "x" entry at its frame on every peer: the same method the asker's input called, called again here in the shared
        /// state (its own hook now lets it through), its toasts the asker's only.
        /// </summary>
        void CoopExecute(CoopPeer peer, int seat, string verb, string args)
        {
            bool mine = seat == peer.LocalSeat;
            var a = args.Length > 0 ? args.Split(' ') : new string[0];
            coopExecuting++;
            coopShared++;
            coopEverywhere++;
            bool wasMine = coopExecMine;
            coopExecMine = mine;
            if (!mine) coopMuteToasts++;
            try
            {
                switch (verb)
                {
                    case "spawn": CoopSpawnShip(peer, seat, (ShipClass)Wire.ParseI(a[0]), a[1] == "1", a, 2); break;
                    case "practice": CoopSpawnPractice(peer, seat, a, 0); break;
                    case "travel":
                    {
                        var to = Galaxy.Find(args);
                        if (to == null) break;
                        string other = CoopOtherAtControls(seat);
                        if (other != null) { ShowToast("The party travels together: " + other + " is at the controls"); break; }
                        ShowToast((mine ? "" : peer.NameOf(seat) + " takes the party to ") + (mine ? "Setting out for " : "") + to.Name);
                        TravelTo(to);
                        break;
                    }
                    case "plot": PlotCourse(Galaxy.Find(args)); break;
                    case "autopilot": EngageCourseAutopilot(); break;
                    case "crew":
                    {
                        int id = Wire.ParseI(a[0]);
                        var ship = id >= 0 && id < Fleet.Ships.Count ? Fleet.Ships[id] : null;
                        var to = Galaxy.Find(string.Join(" ", a, 2, a.Length - 2));
                        if (ship != null && to != null) StartCrewCourse(ship, to, a[1] == "1");
                        break;
                    }
                    case "tanker": CallTanker(); break;
                    case "ftlplot":
                    {
                        string name = string.Join(" ", a, 1, a.Length - 1);
                        var system = Galaxy.Find(name);
                        FtlTarget target = system != null ? FtlTarget.OfStar(system) : null;
                        if (target == null)
                            foreach (var star in Galaxy.NearbyStars)
                                if (star.Name == name) { target = FtlTarget.OfStar(star); break; }
                        if (target != null) PlotFtlCourse(target, a[0] == "1");
                        break;
                    }
                    case "ftlclear": ClearFtlCourse(); break;
                    case "ftlauto": EngageFtlAutopilot(); break;
                    case "reset":
                        CoopNow = Wire.ParseD(a[0]);
                        try { ResetToNow(); }
                        finally { CoopNow = null; }
                        break;
                    default:
                        if (!hud.CoopExecute(verb, a) && !UI.GalaxyMapScreen.CoopExecute(this, verb, a)) // US-T47: the galaxy map's screens and the hail
                            CoopPeer.Warn("unknown action '" + verb + "' from player " + (seat + 1));
                        break;
                }
                CoopActions++;
                CoopPeer.Log(peer.NameOf(seat) + " (player " + (seat + 1) + ") action '" + verb + (args.Length > 0 ? " " + (args.Length > 60 ? args.Substring(0, 60) + "..." : args) : "") + "' at frame " + peer.Frame);
            }
            finally
            {
                coopExecuting--;
                coopShared--;
                coopEverywhere--;
                coopExecMine = wasMine;
                if (!mine) coopMuteToasts--;
            }
        }

        static void CoopReadAt(string[] a, int at, out int frame, out DVec3 offsetKm, out DVec3 forward, out Quaternion up)
        {
            frame = Wire.ParseI(a[at]);
            offsetKm = new DVec3(Wire.ParseD(a[at + 1]), Wire.ParseD(a[at + 2]), Wire.ParseD(a[at + 3]));
            forward = new DVec3(Wire.ParseD(a[at + 4]), Wire.ParseD(a[at + 5]), Wire.ParseD(a[at + 6]));
            up = a.Length > at + 7 ? CoopParseUpFrame(a[at + 7]) : Quaternion.identity;
        }

        /// <summary>The tail of <see cref="SpawnShip"/> (the original's lines) at the asker's spot, and the asker at its helm.</summary>
        void CoopSpawnShip(CoopPeer peer, int seat, ShipClass cls, bool hostile, string[] a, int at)
        {
            CoopReadAt(a, at, out int frame, out var offsetKm, out var forward, out var up);
            var faction = hostile ? World.Pirates : World.Sol;
            string name = "Sandbox " + cls + " " + (++spawnedCount);
            var ship = Fleet.SpawnAt(name, cls, faction, frame, offsetKm, forward, Clock.Jd);
            ship.Ports = World.FreeShipPorts(); // not a hidden haven's rock, as for every free ship
            CoopPeer.Log(peer.NameOf(seat) + " spawns " + ship.Name + " (ship " + ship.Id + ") at frame " + peer.Frame);
            CoopHelmEntry(peer, seat, new[] { "h", Wire.I(seat), "take", Wire.I(ship.Id), CoopUpFrameText(up) });
        }

        /// <summary>The tail of <see cref="SpawnPracticeFighter"/> (the original's lines) at the asker's spot, and the asker at its helm.</summary>
        void CoopSpawnPractice(CoopPeer peer, int seat, string[] a, int at)
        {
            CoopReadAt(a, at, out int frame, out var offsetKm, out var forward, out var up);
            double jd = Clock.Jd;
            var frameHelio = Ephemeris.Helio(frame);
            var flown = !Piloted.IsNone ? Piloted.Ship : null;
            if (flown != null && coopHelmSeat == seat) ExitPiloting(); // the practice fighter flown is brought over too
            var ship = ReusablePracticeFighter();
            CollectPracticeObstacles(ship);
            double lengthKm = ship?.LengthKm ?? CombatSpec.Of(ShipClass.Fighter).LengthKm;
            var spot = PracticeSpawn.Clear(frameHelio + offsetKm, forward, lengthKm, practiceObstacles) - frameHelio;
            if (ship != null)
            {
                ship.Combat.Repair(1);
                ship.Combat.FreeResupply();
                ship.Propellant = ship.PropellantCapacity;
                ship.OutOfPropellant = false;
                ship.StrandedInFlight = false;
                Fleet.PinAt(ship, frame, spot, forward, jd);
            }
            else
            {
                ship = Fleet.SpawnAt(PracticeSpawn.Name, ShipClass.Fighter, World.Sol, frame, spot, forward, jd);
                ship.Ports = World.FreeShipPorts();
            }
            CoopHelmEntry(peer, seat, new[] { "h", Wire.I(seat), "take", Wire.I(ship.Id), CoopUpFrameText(up) });
            ShowToast("Practice fighter: " + ship.Name + " at the camera  (Shift+V again to bring it to the camera, P to hand it back)");
        }

        // ---------------------------------------------------------------- loads

        /// <summary>A client's load (F8, the slots, the menu) asks the host to load that slot of its own: every peer then
        /// loads the host's save (true: asked). The host's own load goes on, and its sync follows.</summary>
        bool CoopRoutesLoad(string slot)
        {
            var peer = CoopPeer.Current;
            if (peer == null || peer.IsHost || peer.Applying || !peer.Running) return false;
            peer.RequestLoad(slot);
            ShowSaves = false;
            ShowToast("Asked " + peer.NameOf(0) + " (the host) to load " + SlotName(slot) + ": the party loads the host's save");
            return true;
        }

        /// <summary>The host's own load: its helm is everyone's (shared), and stays with whoever flew it, else the host.</summary>
        void CoopLoadingHere(Action load)
        {
            if (CoopPeer.Current == null) { load(); return; }
            if (coopHelmSeat < 0) coopHelmSeat = CoopPeer.Current.LocalSeat;
            coopShared++;
            try { load(); }
            finally { coopShared--; }
        }
    }

    /// <summary>The co-op HUD on the app's GameObject: its OnGUI runs with the app's own (US-T28's IMGUI host lists every live
    /// MonoBehaviour's), and draws <see cref="UnitySpaceApp.CoopGui"/>.</summary>
    public sealed class CoopHud : MonoBehaviour
    {
        void OnGUI() => Bootstrap.App?.CoopGui();
    }
}
