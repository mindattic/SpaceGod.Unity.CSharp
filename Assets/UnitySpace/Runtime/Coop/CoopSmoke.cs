// US-T39: the headless two-instance co-op proof (new file; tools/play.ps1 -Track CSharp -Mode coop-smoke). US-T43: the
// client now also keeps a seat through a resync, flies a helm, trades, uses a cockpit's keys, takes the party to another
// star and has the host load a save.
using System;
using System.Diagnostics;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.Input;
using UnityEngine;

namespace Unity.SpaceGod.Coop
{
    /// <summary>
    /// <c>-SpaceGodCoopSmoke=host|client</c>: a listen server and a client on localhost (-nullrhi). The client joins (the
    /// host's save comes over and both load it), then plays as a player would, by the keys, the picker and the HUD's own
    /// handlers: '.' (time warp up: a request the host grants for everyone), '3' (select Earth: every peer sees it), a
    /// station's seat through the picker (a take the host's frame grants, through SeatAuthority on every peer), a
    /// deliberate resync (the seat is kept through it), a freighter spawned and flown (its helm on every peer, on the
    /// client's input stream), a trade at Earth's market, a pirate carrier flown into a fight and a fighter's cockpit taken
    /// there (its keys as commands), a trip to another star (the party's), and a load of the host's quicksave. Both log a
    /// digest of the shared state every <see cref="CoopPeer.DigestEvery"/> frames; the host compares them. After
    /// <see cref="ClientFrames"/> replayed frames the client says bye and both quit.
    /// </summary>
    public static class CoopSmoke
    {
        public static readonly string Role = Bootstrap.CommandLineValue("-SpaceGodCoopSmoke")?.ToLowerInvariant();
        public static bool Active => Role != null;
        public const int ClientFrames = 3000;
        static readonly TimeSpan Timeout = TimeSpan.FromMinutes(9);
        static readonly Stopwatch Since = Stopwatch.StartNew();

        /// <summary>The client's scripted keyboard and mouse (the link puts it in front of the real device).</summary>
        public static ScriptedInputDevice Script;

        static long byeAt = -1;
        static double byeSeconds; // US-T47
        static long replayed;
        static int quitIn = -1;
        static bool finished, hostSaved;
        static string warpBefore;
        static long fighterFrom = -1, travelFrom = -1, loadFrom = -1;
        static FighterSeat fighter;
        static bool fighterHeld;
        static int fighterRetakes;
        static Faction speaker; // US-T47
        static Ship hailed;
        static string productionBefore;
        static int objectivesBefore, recordBefore;
        static double treasuryBefore;
        static StarSystem travelTo; // US-T64

        // US-T64 (both tracks, the C++ script's): a step that needs an earlier ask carried out waits for it to come back
        // in a host frame. A client's ask reaches the host while the host is as far ahead as the client's backlog, so it
        // comes back only once the client has replayed that far: on a client that replays more slowly than the host makes
        // frames (its backlog growing to hundreds of frames), a fixed number of frames is no answer. The script's frame
        // count stands still while it waits (as at 729 for the freighter's helm), so every later step keeps its place
        // after the one it follows; a wait gives up after MaxWait replayed frames and the step goes on (its check then
        // fails, with a warning naming the wait).
        const int MaxWait = 3600;
        static int waited;
        static long waitingAt = -1;
        static int mostBehind; // the client's largest backlog, for the done line

        /// <summary>True while the script waits at step <paramref name="at"/> for <paramref name="ready"/>: the step is held.</summary>
        static bool Waits(long at, bool ready, string what, CoopPeer peer)
        {
            if (ready || replayed + 1 != at)
            {
                if (waitingAt == at && waited > 0)
                    CoopPeer.Log("smoke: step " + at + " waited " + waited + " frames for " + what + " (to frame " + peer.Frame + ")");
                if (waitingAt == at) { waitingAt = -1; waited = 0; }
                return false;
            }
            if (waitingAt != at) { waitingAt = at; waited = 0; }
            if (++waited <= MaxWait) return true;
            CoopPeer.Warn("smoke: step " + at + " gave up waiting for " + what + " after " + MaxWait + " frames (frame " + peer.Frame + ", backlog " + peer.Backlog + ")");
            waitingAt = -1;
            waited = 0;
            return false;
        }

        static void Finish(string why, CoopPeer peer, UnitySpaceApp app)
        {
            if (finished) return;
            finished = true;
            CoopPeer.Log("smoke (" + Role + ") done: " + why + "; frame " + peer.Frame + ", " +
                         (peer.IsHost ? peer.DigestMatches + " digest matches, " + peer.Desyncs + " desyncs" : peer.ClockCorrections + " clock corrections, at most " + mostBehind + " frames behind the host") +
                         (app != null ? ", " + app.CoopPilotFrames + " helm frames made here, " + peer.PilotSent + " helm inputs sent, " + app.CoopCockpitKeys + " cockpit keys, " +
                                        app.CoopActions + " actions, " + peer.Retakes + " seats kept through syncs, " + CoopSettings.Applied + " settings taken" : "") +
                         ", " + Since.Elapsed.TotalSeconds.ToString("F0") + " s; quitting");
            Application.Quit();
        }

        public static void HostFrame(CoopPeer peer, UnitySpaceApp app, FrameRecord rec)
        {
            if (Role != "host" || finished) return;
            // The host's quicksave, early (the client asks for it to be loaded near the end).
            if (!hostSaved && app != null && peer.DigestMatches >= 2)
            {
                hostSaved = true;
                bool saved = app.SaveTo(SaveSlots.Quick, "Quicksave");
                CoopPeer.Log("smoke: host quicksaves at frame " + rec.Frame + ": " + saved);
            }
            if (peer.AnyBye && byeAt < 0) { byeAt = rec.Frame; byeSeconds = Since.Elapsed.TotalSeconds; }
            // US-T47: and 5 s, so the client (30 of its own ticks, slower than the host's frames when catching up) quits first:
            // a client whose host has gone goes back to the start map, which is no end for the smoke.
            if (byeAt >= 0 && rec.Frame >= byeAt + 30 && Since.Elapsed.TotalSeconds - byeSeconds >= 5) Finish("the client said bye", peer, app);
            else if (Since.Elapsed > Timeout) Finish("TIMEOUT", peer, app);
        }

        /// <summary>Each engine tick on the client, before its frames: the countdown to quitting and the timeout.</summary>
        public static void ClientTick(CoopPeer peer)
        {
            if (Role != "client" || finished) return;
            if (quitIn > 0 && --quitIn == 0) Finish("said bye", peer, Bootstrap.App);
            else if (Since.Elapsed > Timeout) Finish("TIMEOUT", peer, Bootstrap.App);
        }

        /// <summary>After each replayed host frame on the client: the scripted player.</summary>
        public static void ClientFrame(CoopPeer peer, UnitySpaceApp app)
        {
            if (Role != "client" || finished || Script == null) return;
            if (peer.Backlog > mostBehind) mostBehind = peer.Backlog;
            // US-T47: the flying (from 730 on: W, X, F) waits until the spawned freighter's helm has come back in a host frame,
            // as the C++ script has since US-T44: on a busy machine the helm can still be in flight at 730 here too, and X
            // (roll at the helm) then opens the news panel in command (X is also ToggleNews), which keeps the cockpit's keys
            // off later on (a run with 0 cockpit keys: the helm came 39 frames after 730).
            if (replayed == 729 && app.Piloted.IsNone) return;
            // US-T64: the other steps that follow an ask (see Waits): the selection (applied with the frames) before the camera
            // goes to it; the seat held before the resync that must keep it, and handed back before Earth is picked again; the freighter's helm handed back before the carrier; the carrier's helm held
            // before it goes to its crew; the fighter's cockpit held before its keys, and handed back before the trip; the
            // party arrived before the galaxy map's screens; the speaker carried before the orders given as it; the orders and
            // the gift carried before they are checked, and so before the load (whose sync would skip the frames that carry
            // them on this client).
            var strategy = app.Galaxy.Polities.Command;
            bool bodySelected = app.CoopTargetKey(app.Selected).StartsWith("b ");
            if (Waits(200, bodySelected, "the selection", peer)) return;
            if (Waits(480, app.Seat != null, "the station's seat", peer)) return;
            if (Waits(670, app.Seat == null, "the station's seat handed back", peer)) return;
            if (Waits(680, bodySelected, "the selection", peer)) return;
            if (Waits(1040, app.Piloted.IsNone, "the freighter's helm handed back", peer)) return;
            if (Waits(1340, app.Piloted.Ship != null, "the carrier's helm", peer)) return;
            // US-T67: a fighter shot down under the client before its keys (a Shipping run: 44 frames after the take) leaves no
            // cockpit to wait for; the client takes another, a few times at most.
            if (fighter != null && replayed < fighterFrom + 100)
            {
                if (app.Seat is FighterSeat) fighterHeld = true;
                else if (fighterHeld && fighterRetakes < 3)
                {
                    CoopPeer.Log("smoke: client lost the cockpit of " + fighter.Name + " before its keys at frame " + peer.Frame + "; takes another");
                    fighter = null;
                    fighterHeld = false;
                    fighterRetakes++;
                    fighterFrom = replayed + 1;
                }
            }
            if (fighter != null && Waits(fighterFrom + 60, app.Seat is FighterSeat, "the fighter's cockpit", peer)) return;
            if (Waits(1760, app.Seat == null, "the fighter's cockpit handed back", peer)) return;
            if (Waits(2050, travelTo == null || app.ActiveSystem == travelTo, "the party's trip", peer)) return;
            if (Waits(2080, speaker == null || strategy.Player == speaker, "the speaker", peer)) return;
            if (speaker != null && hailed != null)
            {
                bool carried = strategy.Find(speaker) is { } d && d.Production.Name != productionBefore && app.Galaxy.Polities.Statecraft.Record.Count > recordBefore;
                if (Waits(2320, carried, "the orders and the gift", peer)) return;
            }
            long n = ++replayed;
            // The stick at the helm: throttle up, the mouse swinging the nose, a roll.
            if (n >= 740 && n < 800) Script.MouseDelta = new UnityEngine.Vector2(4f, 2f);
            switch (n)
            {
                case 30:
                    warpBefore = app.Clock.Warp.Label;
                    Script.Tap(Key.Period);
                    CoopPeer.Log("smoke: client presses '.' (time warp up) at frame " + peer.Frame + ", warp " + warpBefore);
                    break;
                case 180:
                    Script.Tap(Key.Digit3);
                    CoopPeer.Log("smoke: client presses '3' (select) at frame " + peer.Frame);
                    break;
                case 200:
                    app.FocusSelected();
                    break;
                case 300:
                    CoopPeer.Log("smoke: client's warp " + warpBefore + " -> " + app.Clock.Warp.Label + " (the host's clock) at frame " + peer.Frame);
                    break;
                case 330:
                    CoopPeer.Log("smoke: client selected " + app.CoopTargetName(app.CoopTargetKey(app.Selected)) + "; the host's roster has it as '" +
                                 peer.Players[peer.LocalSeat]?.Selection + "'");
                    break;
                case 360:
                {
                    var stations = Embodiables.StationsIn(app.World);
                    IEmbodiable seat = stations.Count > 0 ? stations[0] : null;
                    if (seat == null)
                        foreach (var b in app.World.War.Battles)
                        {
                            foreach (var s in Embodiables.SeatsIn(app.World, b))
                                if (!(s is ShipHelm)) { seat = s; break; }
                            if (seat != null) break;
                        }
                    if (seat == null) { CoopPeer.Warn("smoke: no seat to take in " + app.ActiveSystem.Name); break; }
                    CoopPeer.Log("smoke: client picks seat '" + seat.Name + "' (key " + app.CoopSeatKey(seat) + ") at frame " + peer.Frame);
                    app.PickSeat(seat);
                    break;
                }
                case 480:
                    CoopPeer.Log("smoke: client holds " + (app.Seat?.Name ?? "nothing") + " and asks for a resync at frame " + peer.Frame + " (a deliberate one: the seat must be kept)");
                    peer.RequestResync("a deliberate resync (smoke)");
                    break;
                case 600:
                    CoopPeer.Log("smoke: after the resync the client holds " + (app.Seat?.Name ?? "nothing") + " at frame " + peer.Frame + ", warp " + app.Clock.Warp.Label);
                    break;
                case 640:
                    Script.Tap(Key.P); // back to command: the seat goes back to its AI (a release command)
                    break;
                case 670:
                    Script.Tap(Key.Digit3); // Earth again, and the camera on it (the spawns below are at the camera)
                    break;
                case 680:
                    app.FocusSelected();
                    break;
                case 700:
                    CoopPeer.Log("smoke: client spawns a freighter at its camera (the spawn grid's call) at frame " + peer.Frame);
                    app.SpawnShip(ShipClass.Freighter, false);
                    break;
                case 730:
                    CoopPeer.Log("smoke: client at the helm of " + (app.Piloted.Ship?.Name ?? "nothing") + " (helm seat " + app.CoopHelmSeat + ") at frame " + peer.Frame + "; flies it");
                    Script.Hold(Key.W);
                    break;
                case 760: Script.Hold(Key.X); break;
                case 780: Script.Release(Key.X); break;
                case 820:
                    Script.Tap(Key.F); // flight assist off, in the pilot loop on every peer
                    break;
                case 900:
                    Script.Release(Key.W);
                    CoopPeer.Log("smoke: client's helm at frame " + peer.Frame + ": " + app.PilotSpeedKmS.ToString("0.000") + " km/s, flight assist " + (app.PilotFlightAssist ? "on" : "off"));
                    break;
                case 920:
                {
                    Settlement market = null;
                    foreach (var s in app.Economy.Settlements)
                        if (s.Discovered && app.CanTradeNow(s)) { market = s; break; }
                    if (market == null) { CoopPeer.Warn("smoke: no market in reach of the helm at frame " + peer.Frame); break; }
                    Good good = Good.Water;
                    for (int g = 0; g < GoodInfo.Count; g++)
                        if (market.Stock[g] > 50) { good = (Good)g; break; }
                    CoopPeer.Log("smoke: client buys 1 crate of " + GoodInfo.Of(good).Name + " at " + market.Name + " (the market board's Buy) at frame " + peer.Frame);
                    app.CoopHud.CoopSmokeBuy(market, good);
                    break;
                }
                case 1000:
                    CoopPeer.Log("smoke: client's hold: " + (app.Piloted.Ship?.Cargo ?? "?") + "; releases the helm at frame " + peer.Frame);
                    Script.Tap(Key.P);
                    break;
                case 1040:
                    CoopPeer.Log("smoke: client spawns a pirate carrier at its camera to start a fight at frame " + peer.Frame);
                    app.SpawnShip(ShipClass.Carrier, true);
                    break;
                case 1340:
                {
                    // Its crew takes it back (a carrier's AI launches its squadrons in a fight); the cockpits are then there to take.
                    var carrier = app.Piloted.Ship;
                    CoopPeer.Log("smoke: client's carrier " + (carrier?.Name ?? "?") + " is " + (carrier?.Battle != null ? "in " + carrier.Battle.Name : "in no fight") +
                                 "; " + app.World.War.Battles.Count + " battles in " + app.ActiveSystem.Name + "; hands the helm to its crew at frame " + peer.Frame);
                    Script.Tap(Key.P);
                    fighterFrom = n + 20;
                    break;
                }
                case 1700:
                    if (fighter == null) CoopPeer.Warn("smoke: no fighter aloft to take by frame " + peer.Frame);
                    Script.Tap(Key.P);
                    break;
                case 1760:
                {
                    StarSystem to = null;
                    foreach (var s in app.Galaxy.Systems)
                        if (s != app.ActiveSystem && !s.IsVoidBase) { to = s; break; }
                    CoopPeer.Log("smoke: client asks for the party's trip to " + to?.Name + " at frame " + peer.Frame + " (from " + app.ActiveSystem.Name + ")");
                    if (to != null) app.TravelTo(to);
                    travelFrom = n;
                    travelTo = to;
                    break;
                }
                // US-T47: the galaxy map's strategy and envoy screens and a hail (F12), from the client.
                case 2050:
                {
                    var factions = app.Galaxy.Polities.Factions;
                    Ship other = null;
                    foreach (var s in app.World.Fleet.Ships)
                        if (!s.Destroyed && Statecraft.Party(s.Faction)) { other = s; break; }
                    speaker = null;
                    foreach (var f in factions)
                        if (Statecraft.Party(f) && f != other?.Faction) { speaker = f; break; }
                    hailed = other;
                    var map = app.CoopHud.CoopMap;
                    CoopPeer.Log("smoke: client speaks for the " + speaker?.Name + " (the COMMAND panel's arrows) at frame " + peer.Frame);
                    if (speaker != null) map.CoopSmokeSpeak(speaker);
                    break;
                }
                case 2080:
                    if (speaker == null) break;
                    productionBefore = app.Galaxy.Polities.Command.Find(speaker)?.Production.Name ?? ProductionFocus.Balanced.Name;
                    CoopPeer.Log("smoke: client clicks Production (" + productionBefore + ") at frame " + peer.Frame);
                    app.CoopHud.CoopMap.CoopSmokeProduction();
                    break;
                case 2110:
                    if (speaker == null) break;
                    objectivesBefore = app.Galaxy.Polities.Command.ObjectiveCount;
                    CoopPeer.Log("smoke: client orders the " + speaker.Name + "'s navy to EXPLORE " + app.ActiveSystem.Name + " at frame " + peer.Frame);
                    app.CoopHud.CoopMap.CoopSmokeExplore(app.ActiveSystem);
                    break;
                case 2160:
                    if (hailed == null) { CoopPeer.Warn("smoke: no ship of a state to hail in " + app.ActiveSystem.Name); break; }
                    app.Select(Target.OfShip(hailed));
                    break;
                case 2170:
                    if (hailed == null) break;
                    CoopPeer.Log("smoke: client selects " + hailed.Name + " (" + hailed.Faction.Name + ") and presses F12 (hail) at frame " + peer.Frame);
                    Script.Tap(Key.F12);
                    break;
                case 2240:
                    if (hailed == null || speaker == null) break;
                    recordBefore = app.Galaxy.Polities.Statecraft.Record.Count;
                    treasuryBefore = hailed.Faction.Treasury;
                    CoopPeer.Log("smoke: client sends the " + hailed.Faction.Name + " a gift (the envoy screen's button) at frame " + peer.Frame + "; envoy open: " + app.ShowGalaxyMap);
                    app.CoopHud.CoopMap.CoopSmokeGift(hailed.Faction);
                    break;
                case 2320:
                {
                    if (speaker == null || hailed == null) break;
                    var command = app.Galaxy.Polities.Command;
                    string production = command.Find(speaker)?.Production.Name ?? "-";
                    bool ok = command.Player == speaker && production != productionBefore && app.Galaxy.Polities.Statecraft.Record.Count > recordBefore;
                    CoopPeer.Log("smoke: statecraft at frame " + peer.Frame + ": speaker " + command.Player?.Name + ", production " + productionBefore + " -> " + production +
                                 ", objectives " + objectivesBefore + " -> " + command.ObjectiveCount + ", record " + recordBefore + " -> " + app.Galaxy.Polities.Statecraft.Record.Count +
                                 ", the " + hailed.Faction.Name + "'s treasury " + treasuryBefore.ToString("N0") + " -> " + hailed.Faction.Treasury.ToString("N0") + "; carried out: " + ok);
                    app.ShowGalaxyMap = false;
                    break;
                }
                case 2500:
                    CoopPeer.Log("smoke: client is in " + app.ActiveSystem.Name + " at frame " + peer.Frame + "; asks the host to load its quicksave");
                    app.LoadFrom(SaveSlots.Quick);
                    loadFrom = n;
                    break;
                case 2800:
                    CoopPeer.Log("smoke: after the load the client is in " + app.ActiveSystem.Name + " at frame " + peer.Frame);
                    break;
                case ClientFrames:
                    peer.SayBye();
                    quitIn = 30;
                    break;
            }
            // The fight: once a fighter is aloft, take its cockpit, then its keys (power to weapons, flight assist, a flare).
            if (fighterFrom >= 0 && n >= fighterFrom && fighter == null && n % 20 == 0)
            {
                foreach (var b in app.World.War.Battles)
                {
                    foreach (var s in Embodiables.SeatsIn(app.World, b))
                        if (s is FighterSeat f) { fighter = f; break; }
                    if (fighter != null) break;
                }
                if (fighter != null)
                {
                    CoopPeer.Log("smoke: client takes the cockpit of " + fighter.Name + " (key " + app.CoopSeatKey(fighter) + ") at frame " + peer.Frame);
                    app.PickSeat(fighter); // hands the carrier's helm back first (a release), then the take
                    fighterFrom = n;
                }
            }
            else if (fighter != null && n == fighterFrom + 60)
            {
                CoopPeer.Log("smoke: client in " + (app.Seat?.Name ?? "nothing") + " presses 7 (power to weapons) at frame " + peer.Frame);
                Script.Tap(Key.Digit7);
            }
            else if (fighter != null && n == fighterFrom + 80) Script.Tap(Key.F);
            else if (fighter != null && n == fighterFrom + 100) Script.Tap(Key.LeftCtrl);
        }
    }
}
