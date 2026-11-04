using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.SpaceGod.Core;

namespace Bench.CSharp
{
    /// <summary>
    /// The canonical state digest: a deterministic text dump of the simulation that Bench.Cpp must reproduce byte for
    /// byte from the C++ Core. The format is specified in Bench/README.md ("Digest format"); change both together and
    /// bump <see cref="Version"/>.
    /// </summary>
    public static class Digest
    {
        public const string Version = "spacegod-digest 1";

        /// <summary>A double as its IEEE-754 bits: 16 upper-case hex digits of BitConverter.DoubleToInt64Bits.</summary>
        public static string H(double d) => BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);

        static string I(int i) => i.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "1" : "0";
        static string V(DVec3 v) => H(v.X) + "," + H(v.Y) + "," + H(v.Z);
        static string S(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace(' ', '_').Replace('\n', '_').Replace('\r', '_');
        static string F(Faction f) => f == null ? "-" : S(f.Prefix);

        /// <summary>64-bit FNV-1a over the UTF-8 bytes of <paramref name="text"/>.</summary>
        public static ulong Fnv1a64(string text)
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                h ^= b;
                h *= 0x100000001B3UL;
            }
            return h;
        }

        public static string Hex(ulong h) => h.ToString("X16", CultureInfo.InvariantCulture);

        public static string Write(Simulation sim, string scenario)
        {
            var sb = new StringBuilder(1 << 20);
            void L(string line) => sb.Append(line).Append('\n');

            var g = sim.Galaxy;
            L(Version);
            L("scenario " + S(scenario));
            L("clock jd=" + H(sim.Clock.Jd) + " warp=" + I(sim.Clock.WarpIndex) + " paused=" + B(sim.Clock.Paused));
            int inTransit = 0;
            if (g.Traffic != null) foreach (var _ in g.Traffic.InTransit()) inTransit++;
            L("galaxy systems=" + I(g.Systems.Count) + " nearby=" + I(g.NearbyStars.Count) + " intransit=" + I(inTransit) +
              " focus=" + S(g.Focus?.Name) + " active=" + S(sim.ActiveSystem.Name) + " battlesStarted=" + I(sim.BattlesStarted));
            var arrivals = new StringBuilder("arrivals");
            foreach (VoyagePurpose p in Enum.GetValues(typeof(VoyagePurpose)))
            {
                int n = 0;
                if (g.Traffic != null) g.Traffic.Arrivals.TryGetValue(p, out n);
                arrivals.Append(' ').Append(p.ToString()).Append('=').Append(I(n));
            }
            L(arrivals.ToString());
            if (sim.CourseShip != null)
                L("course ship=" + S(sim.CourseShip.Name) + " to=" + S(sim.CourseDestination?.Name) + " ended=" + B(sim.CourseEnded) +
                  " arrived=" + B(sim.CourseArrived) + " in=" + S(sim.SystemHolding(sim.CourseShip)?.Name));
            if (sim.Skirmish != null)
            {
                L("skirmish");
                WriteBattle(L, sim.Skirmish);
            }

            for (int si = 0; si < g.Systems.Count; si++)
            {
                var sys = g.Systems[si];
                var w = sys.World;
                var cat = sys.Catalog;
                var ships = w.Fleet.Ships;
                ulong eh = 0xCBF29CE484222325UL;
                var evb = new StringBuilder();
                foreach (var e in w.Events)
                    evb.Append(H(e.Jd)).Append('|').Append(e.Kind.ToString()).Append('|').Append(I(e.Body)).Append('|').Append(e.Text).Append('\n');
                eh = Fnv1a64(evb.ToString());
                L("system " + I(si) + " " + S(sys.Name) + " jd=" + H(w.Jd) + " coarse=" + B(w.Coarse) + " bodies=" + I(cat.Count) +
                  " ships=" + I(ships.Count) + " settlements=" + I(w.Economy.Settlements.Count) + " battles=" + I(w.War.Battles.Count) +
                  " ended=" + I(w.War.Ended.Count) + " wrecks=" + I(w.War.Wrecks.Items.Count) + " events=" + I(w.Events.Count) + " eventsHash=" + Hex(eh));

                for (int b = 0; b < cat.Count; b++)
                    L("body " + I(b) + " " + S(cat[b].Name) + " " + V(sys.Ephemeris.Helio(b)));

                foreach (var f in w.Fleet.Factions)
                    L("faction " + F(f) + " " + S(f.Name) + " treasury=" + H(f.Treasury) + " losses=" + I(w.War.Losses(f)));

                foreach (var st in w.Economy.Settlements)
                {
                    var stock = new StringBuilder();
                    for (int k = 0; k < st.Stock.Length; k++)
                    {
                        if (k > 0) stock.Append(',');
                        stock.Append(H(st.Stock[k]));
                    }
                    L("settlement " + I(st.Id) + " " + S(st.Name) + " owner=" + F(st.Owner) + " pop=" + H(st.Population) + " stock=" + stock);
                }

                double jd = w.Jd;
                for (int k = 0; k < ships.Count; k++)
                {
                    var s = ships[k];
                    var line = new StringBuilder();
                    line.Append("ship ").Append(I(s.Id)).Append(' ').Append(S(s.Name)).Append(' ').Append(s.Spec != null ? s.Spec.Class.ToString() : "-")
                        .Append(' ').Append(F(s.Faction)).Append(" phase=").Append(s.Phase.ToString()).Append(" destroyed=").Append(B(s.Destroyed))
                        .Append(" battle=").Append(B(s.Battle != null)).Append(" voyage=").Append(B(s.Voyage != null))
                        .Append(" hull=").Append(s.Combat != null ? H(s.Combat.Hull) : "-");
                    if (!s.Destroyed)
                    {
                        try
                        {
                            var state = w.Fleet.StateOf(s, jd);
                            line.Append(" pos=").Append(V(state.HelioKm)).Append(" facing=").Append(V(state.Facing)).Append(" speed=").Append(H(state.SpeedKmS));
                        }
                        catch (Exception) { line.Append(" pos=! facing=! speed=!"); }
                        try { line.Append(" vel=").Append(V(w.Fleet.HelioVelocityOf(s, jd))); }
                        catch (Exception) { line.Append(" vel=!"); }
                    }
                    L(line.ToString());
                }

                foreach (var bt in w.War.Battles) WriteBattle(L, bt);
            }
            L("end");
            return sb.ToString();
        }

        /// <summary>
        /// Trace mode: one line for the digest <paramref name="text"/> after tick <paramref name="tick"/>: the FNV-1a of its
        /// head (everything before the first system) and of each system's block, so the first differing tick and system show
        /// between the two runners. Bench.Cpp's Digest::TraceLine is the same.
        /// </summary>
        public static string TraceLine(int tick, string text)
        {
            var sb = new StringBuilder("tick " + I(tick));
            int start = 0, section = -1;
            void Emit(int end) => sb.Append(section < 0 ? " head=" : " s" + I(section) + "=").Append(Hex(Fnv1a64(text.Substring(start, end - start))));
            for (int pos = 0; pos < text.Length;)
            {
                int eol = text.IndexOf('\n', pos);
                if (eol < 0) eol = text.Length;
                if (string.CompareOrdinal(text, pos, "system ", 0, 7) == 0)
                {
                    Emit(pos);
                    start = pos;
                    section++;
                }
                pos = eol + 1;
            }
            Emit(text.Length);
            return sb.ToString();
        }

        static void WriteBattle(Action<string> L, Battle bt)
        {
            int present = 0;
            foreach (var u in bt.Units) if (u.Present) present++;
            L("battle " + S(bt.Name) + " units=" + I(bt.Units.Count) + " present=" + I(present) + " over=" + B(bt.Over) + " winner=" + I(bt.Winner));
            foreach (var u in bt.Units)
                L("unit " + I(u.Id) + " side=" + I(u.Side) + " " + u.Kind.ToString() + " " + F(u.Faction) + " present=" + B(u.Present) +
                  " pos=" + V(u.Pos) + " vel=" + V(u.Vel) + " hull=" + (u.Combat != null ? H(u.Combat.Hull) : "-"));
        }
    }
}
