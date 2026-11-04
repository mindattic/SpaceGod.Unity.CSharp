// US-T47: the galaxy map's strategy (COMMAND) and envoy (DIPLOMACY) screens and the polity hail (F12) in co-op (new file,
// a partial of the ported GalaxyMapScreen; docs/MIGRATION.md "Co-op"). The ported screen calls in here only at lines marked
// US-T47: in single-player every call is false and the original goes on.
using System;
using System.Collections.Generic;
using Unity.SpaceGod.Coop;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.UI
{
    public sealed partial class GalaxyMapScreen
    {
        // ---------------------------------------------------------------- keys every peer reads back the same

        /// <summary>A polity by its place in <see cref="Polities.Factions"/> (-1: none).</summary>
        static int KF(UnitySpaceApp app, Faction f) => f == null ? -1 : IndexOf(app.Galaxy.Polities.Factions, f);
        static Faction F(UnitySpaceApp app, string k) => At(app.Galaxy.Polities.Factions, k);

        /// <summary>A system's world by its place in <see cref="Polities.Worlds"/> (-1: none).</summary>
        static int KW(UnitySpaceApp app, World w) => w == null ? -1 : IndexOf(app.Galaxy.Polities.Worlds, w);
        static World W(UnitySpaceApp app, string k) => At(app.Galaxy.Polities.Worlds, k);

        /// <summary>A ship as "&lt;world&gt;:&lt;index in its fleet&gt;" ("-": none, or inside a gate).</summary>
        static string KS(UnitySpaceApp app, Ship s)
        {
            var w = s != null ? app.Galaxy.Polities.Command.WorldOf(s) : null;
            int i = w != null ? IndexOf(w.Fleet.Ships, s) : -1;
            return w == null || i < 0 ? "-" : Wire.I(KW(app, w)) + ":" + Wire.I(i);
        }

        static Ship S(UnitySpaceApp app, string k)
        {
            int c = k.IndexOf(':');
            if (c < 0) return null;
            var w = W(app, k.Substring(0, c));
            return w != null ? At(w.Fleet.Ships, k.Substring(c + 1)) : null;
        }

        static int IndexOf<T>(IReadOnlyList<T> list, T item) where T : class
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        static T At<T>(IReadOnlyList<T> list, string k) where T : class
        {
            int i = Wire.ParseI(k);
            return i >= 0 && i < list.Count ? list[i] : null;
        }

        /// <summary>A screen's button that writes the Core, in co-op: into the frames, carried out at its frame on every peer
        /// (<see cref="CoopExecute"/>). False in single-player and while carrying it out: the original goes on.</summary>
        bool CoopSends(string verb, params object[] args) => CoopSends(app, verb, args);

        static bool CoopSends(UnitySpaceApp app, string verb, params object[] args)
        {
            if (CoopPeer.Current == null) return false;
            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++) parts[i] = Convert.ToString(args[i], System.Globalization.CultureInfo.InvariantCulture);
            return app.CoopSends(verb, string.Join(" ", parts));
        }

        // ---------------------------------------------------------------- the speaker

        /// <summary>The polity sent last as the speaker (until the frames bring it back), so a screen drawn every frame asks once.</summary>
        static Faction speakSent;
        static long speakSentAt = -1000;

        /// <summary>
        /// <c>command.Player = f</c>: the polity the player commands and speaks for, which the Core reads everywhere (the AI
        /// leaves it be, proposals to it wait for an answer). The party has one, as it has one helm: in co-op a change goes
        /// out as a "speak" entry; true when sent (or already asked for), so the screen keeps its own pick meanwhile.
        /// </summary>
        bool CoopSpeaks(Faction f)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return false;
            if (app.Galaxy.Polities.Command.Player == f) return true; // already the party's
            if (speakSent == f && peer.Frame - speakSentAt < 120) return true;
            if (!CoopSends("speak", KF(app, f))) return false;
            speakSent = f;
            speakSentAt = peer.Frame;
            return true;
        }

        /// <summary>The envoy screen, drawn: in co-op it speaks for the party's polity (another player may have picked it)
        /// rather than writing its own pick back every frame.</summary>
        void CoopAdoptSpeaker(List<Faction> states)
        {
            var player = app.Galaxy.Polities.Command.Player;
            if (CoopPeer.Current == null || player == null || player == commanded || !states.Contains(player)) return;
            if (speakSent != null && speakSent != player && CoopPeer.Current.Frame - speakSentAt < 120) return; // ours is on its way
            commanded = player;
            envoyWith = null;
            warArmed = false;
        }

        /// <summary>The directives the COMMAND panel reads: in co-op what the Core holds, without making them (the original's
        /// <c>command.Of</c> makes them the first time; on one peer alone that would be a write no other peer made).</summary>
        PolityDirectives CoopDirectives(StrategicCommand command, Faction f) =>
            CoopPeer.Current == null ? command.Of(f) : command.Find(f) ?? new PolityDirectives(f);

        // ---------------------------------------------------------------- the buttons, sent

        bool CoopSendsProduction(ProductionFocus p) => CoopSends("production", KF(app, commanded), IndexOf(ProductionFocus.All, p));
        bool CoopSendsBuild(BuildPlan p) => CoopSends("build", KF(app, commanded), IndexOf(BuildPlan.All, p));
        bool CoopSendsResearch(ResearchTrack t) => CoopSends("research", KF(app, commanded), t == null ? -1 : IndexOf(ResearchTrack.All, t));

        bool CoopSendsOrder(ObjectiveKind kind, StarSystem target, World from, Ship charge) =>
            CoopSends("objective", KF(app, commanded), (int)commandScope, KS(app, commandGroup), KW(app, from), (int)kind, KW(app, target.World), KS(app, charge));

        bool CoopSendsClear(World from) => CoopSends("standdown", KF(app, commanded), (int)commandScope, KS(app, commandGroup), KW(app, from));

        bool CoopSendsAnswer(Proposal p, bool accept) =>
            CoopSends("answer", IndexOf(app.Galaxy.Polities.Statecraft.Inbox, p), accept ? 1 : 0);

        bool CoopSendsBreak(Pact p) => CoopSends("breakpact", IndexOf(app.Galaxy.Polities.Statecraft.Pacts, p), KF(app, commanded));

        bool CoopSendsPropose(EnvoyOption o, Faction them) =>
            CoopSends("propose", IndexOf(Deal.All, o.Deal), KF(app, commanded), KF(app, them), Wire.D(o.Amount));

        bool CoopSendsGift(Faction them, double amount) => CoopSends("gift", KF(app, commanded), KF(app, them), Wire.D(amount));

        bool CoopSendsWar(Faction them) => CoopSends("declare", KF(app, commanded), KF(app, them));

        /// <summary>F12 (<see cref="UnitySpaceApp"/>'s HailPolity): the hail and its answer at its frame on every peer, the
        /// envoy screen opened on the hailer's machine.</summary>
        internal static bool CoopSendsHail(UnitySpaceApp app, Faction from, Faction to) => CoopSends(app, "hail", KF(app, from), KF(app, to));

        // ---------------------------------------------------------------- carried out, on every peer

        /// <summary>
        /// A frame's map action on every peer: what the button does (the original's call, by the same objects read back from
        /// their keys), its toast the asker's (others' are muted). False for a verb the map does not know.
        /// </summary>
        internal static bool CoopExecute(UnitySpaceApp app, string verb, string[] a)
        {
            var polities = app.Galaxy.Polities;
            var command = polities.Command;
            var statecraft = polities.Statecraft;
            double jd = app.Clock.Jd;
            switch (verb)
            {
                case "speak":
                    command.Player = F(app, a[0]);
                    if (app.CoopActingHere) speakSent = null;
                    return true;
                case "production":
                {
                    var f = F(app, a[0]);
                    int i = Wire.ParseI(a[1]);
                    if (f != null && i >= 0 && i < ProductionFocus.All.Count) command.SetProduction(f, ProductionFocus.All[i]);
                    return true;
                }
                case "build":
                {
                    var f = F(app, a[0]);
                    int i = Wire.ParseI(a[1]);
                    if (f != null && i >= 0 && i < BuildPlan.All.Count) command.SetBuild(f, BuildPlan.All[i]);
                    return true;
                }
                case "research":
                {
                    var f = F(app, a[0]);
                    int i = Wire.ParseI(a[1]);
                    if (f != null && i < ResearchTrack.All.Count) command.SetResearch(f, i < 0 ? null : ResearchTrack.All[i]);
                    return true;
                }
                case "objective":
                {
                    var f = F(app, a[0]);
                    var scope = (CommandScope)Wire.ParseI(a[1]);
                    var group = S(app, a[2]);
                    var from = W(app, a[3]) ?? app.ActiveSystem.World;
                    var kind = (ObjectiveKind)Wire.ParseI(a[4]);
                    var target = W(app, a[5]);
                    var charge = S(app, a[6]);
                    string why = command.Order(f, scope, group, from, kind, target, jd, out int given, -1, charge);
                    app.ShowToast(why ?? given + (given == 1 ? " group: " : " groups: ") + command.Describe(kind, target, -1, charge).ToLowerInvariant());
                    return true;
                }
                case "standdown":
                {
                    int n = command.Clear(F(app, a[0]), (CommandScope)Wire.ParseI(a[1]), S(app, a[2]), W(app, a[3]) ?? app.ActiveSystem.World);
                    app.ShowToast(n == 0 ? "No group there was under orders" : n + (n == 1 ? " group stands down" : " groups stand down"));
                    return true;
                }
                case "answer":
                {
                    var p = At(statecraft.Inbox, a[0]);
                    if (p == null) { app.ShowToast("That offer no longer stands"); return true; }
                    if (a[1] == "1") app.ShowToast(statecraft.Answer(p, true, jd) ? "Agreed: " + p.Describe() : "That offer no longer stands");
                    else
                    {
                        statecraft.Answer(p, false, jd);
                        app.ShowToast("Declined: " + p.Describe());
                    }
                    return true;
                }
                case "breakpact":
                {
                    var p = At(statecraft.Pacts, a[0]);
                    var by = F(app, a[1]);
                    if (p == null || by == null) return true;
                    statecraft.Break(p, by, jd);
                    app.ShowToast("You break the " + p.Describe() + ": they will not forget it");
                    return true;
                }
                case "propose":
                {
                    var deal = At(Deal.All, a[0]);
                    var from = F(app, a[1]);
                    var them = F(app, a[2]);
                    if (deal == null || from == null || them == null) return true;
                    string why = statecraft.Propose(deal, from, them, jd, out var answer, Wire.ParseD(a[3]));
                    app.ShowToast(why ?? (answer == null ? "Put to them: they will answer" : (answer.Accepted ? "Agreed: " : "Refused: ") + answer.Main));
                    return true;
                }
                case "gift":
                {
                    var by = F(app, a[0]);
                    var them = F(app, a[1]);
                    if (by == null || them == null) return true;
                    app.ShowToast(statecraft.Gift(by, them, Wire.ParseD(a[2]), jd) ?? "The " + them.Name + " receives your gift");
                    return true;
                }
                case "declare":
                {
                    var by = F(app, a[0]);
                    var them = F(app, a[1]);
                    if (by == null || them == null) return true;
                    string why = statecraft.DeclareWar(by, them, jd);
                    app.ShowToast(why ?? "The " + by.Name + " is at war with the " + them.Name);
                    return true;
                }
                case "hail":
                {
                    var from = F(app, a[0]);
                    var to = F(app, a[1]);
                    string answer = statecraft.Hail(from, to, jd);
                    app.ShowToast(answer);
                    CoopPeer.Log("hail: " + answer);
                    if (app.CoopActingHere && Statecraft.Party(to))
                    {
                        app.EnvoyTo = to;
                        app.ShowGalaxyMap = true;
                    }
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- the smoke run's clicks (CoopSmoke)

        /// <summary>The COMMAND panel's speaker arrows, to <paramref name="f"/>.</summary>
        internal void CoopSmokeSpeak(Faction f)
        {
            commanded = f;
            commandGroup = null;
            if (!CoopSpeaks(f)) app.Galaxy.Polities.Command.Player = f;
        }

        /// <summary>The COMMAND panel's Production button (the next focus).</summary>
        internal void CoopSmokeProduction()
        {
            var command = app.Galaxy.Polities.Command;
            var next = Next(ProductionFocus.All, CoopDirectives(command, commanded).Production);
            if (!CoopSendsProduction(next)) command.SetProduction(commanded, next);
        }

        /// <summary>An EXPLORE objective for <paramref name="target"/> to every group of the empire (the ORDERS buttons).</summary>
        internal void CoopSmokeExplore(StarSystem target)
        {
            commandScope = CommandScope.Polity;
            Give(app.Galaxy.Polities.Command, ObjectiveKind.Explore, target, null, app.Clock.Jd);
        }

        /// <summary>The envoy screen's SEND A GIFT button, to <paramref name="them"/>.</summary>
        internal void CoopSmokeGift(Faction them)
        {
            double gift = Statecraft.SuggestedGift(commanded);
            if (!CoopSendsGift(them, gift))
                app.ShowToast(app.Galaxy.Polities.Statecraft.Gift(commanded, them, gift, app.Clock.Jd) ?? "The " + them.Name + " receives your gift");
        }

        internal Faction CoopCommanded => commanded;
    }
}
