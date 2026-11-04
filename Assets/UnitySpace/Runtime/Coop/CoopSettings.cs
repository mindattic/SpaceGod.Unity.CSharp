// US-T43: the settings the Core reads, shared in co-op (new file; docs/MIGRATION.md "Co-op").
using System;
using System.Collections.Generic;
using System.Text;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod.Coop
{
    /// <summary>
    /// The Core reads dozens of tuning settings from the player's profile (the groups below: the war's, the economy's, FTL,
    /// the AI's...). In co-op they are the host's: every sync carries the host's values and every peer takes them before
    /// it loads the save; a change the host makes goes out as a frame entry ("set &lt;key&gt; &lt;value&gt;") every peer
    /// applies at the same frame; a change a client makes is put back at once and asked of the host, whose frame then sets
    /// it everywhere. The view's own settings (graphics, LOD, controls, audio) stay each machine's.
    /// </summary>
    public static class CoopSettings
    {
        /// <summary>The groups whose settings the simulation reads (SettingGroups' names).</summary>
        public static readonly HashSet<string> SharedGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            SettingGroups.Streaming, SettingGroups.Simulation, SettingGroups.Ftl, SettingGroups.Civilizations, SettingGroups.Ai,
            SettingGroups.Dogfighting, SettingGroups.Broadsides, SettingGroups.Weapons, SettingGroups.Economy,
        };

        static bool hooked, applying;
        static readonly Dictionary<string, string> shared = new Dictionary<string, string>(StringComparer.Ordinal);

        public static int Applied { get; private set; }

        public static bool IsShared(GameSetting s) => s != null && SharedGroups.Contains(s.Group);

        /// <summary>A setting's value as the wire carries it (exact: a range as its IEEE bits, a choice as its index).</summary>
        public static string ValueOf(GameSetting s) => s switch
        {
            ToggleSetting t => t.Value ? "1" : "0",
            RangeSetting r => Wire.D(r.Value),
            ChoiceSetting c => Wire.I(c.Index),
            _ => null,
        };

        static void Set(GameSetting s, string value)
        {
            switch (s)
            {
                case ToggleSetting t: t.Set(value == "1"); break;
                case RangeSetting r: r.Set(Wire.ParseD(value)); break;
                case ChoiceSetting c: c.Set(Wire.ParseI(value)); break;
            }
        }

        /// <summary>Watches every shared setting on this machine (when co-op starts, and again at each sync for any declared
        /// since: a setting registers when its class is first used).</summary>
        public static void Hook()
        {
            if (!hooked)
            {
                hooked = true;
                SettingsRegistry.DiscoverLoadedDeclarations();
            }
            foreach (var s in SettingsRegistry.Default.All)
            {
                if (!IsShared(s) || !watched.Add(s.Key)) continue;
                shared[s.Key] = ValueOf(s);
                s.Changed += Changed;
            }
        }

        static readonly HashSet<string> watched = new HashSet<string>(StringComparer.Ordinal);

        static void Changed(GameSetting s)
        {
            if (applying) return;
            var peer = CoopPeer.Current;
            string value = ValueOf(s);
            if (peer == null)
            {
                shared[s.Key] = value;
                return;
            }
            if (peer.IsHost)
            {
                // Takes effect at the host's next frame (its pause menu, where settings are changed, holds the frames), and
                // that frame's entry sets it on every client at the same point.
                shared[s.Key] = value;
                peer.QueueEntry("set " + s.Key + " " + value);
                return;
            }
            // A client's own change would run here ahead of everyone: put back, and asked of the host.
            peer.RequestSetting(s.Key, value);
            if (shared.TryGetValue(s.Key, out var was) && was != value)
            {
                applying = true;
                try { Set(s, was); }
                finally { applying = false; }
            }
        }

        /// <summary>A "set" entry from a frame (or a sync's list): applied without echoing it back.</summary>
        public static void Apply(string key, string value)
        {
            var s = SettingsRegistry.Default.Find(key);
            if (s == null || !IsShared(s)) { CoopPeer.Warn("setting '" + key + "' is not a shared one here"); return; }
            shared[key] = value;
            if (ValueOf(s) == value) return;
            applying = true;
            try { Set(s, value); }
            catch (Exception e) { CoopPeer.Warn("could not set '" + key + "' to " + value + ": " + e.Message); }
            finally { applying = false; }
            Applied++;
        }

        /// <summary>The host's values of every shared setting, one "key value" a line (what a sync carries).</summary>
        public static string Snapshot()
        {
            Hook();
            var sb = new StringBuilder();
            foreach (var s in SettingsRegistry.Default.All)
            {
                if (!IsShared(s)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(s.Key).Append(' ').Append(ValueOf(s));
            }
            return sb.ToString();
        }

        /// <summary>Takes the host's values; returns how many differed here.</summary>
        public static int ApplySnapshot(IEnumerable<string> lines)
        {
            Hook();
            int before = Applied;
            foreach (var l in lines)
            {
                int sp = l.IndexOf(' ');
                if (sp > 0) Apply(l.Substring(0, sp), l.Substring(sp + 1));
            }
            return Applied - before;
        }

        /// <summary>Whether the setting with this key is one the host decides (the client's request is checked against it).</summary>
        public static bool IsSharedKey(string key) => IsShared(SettingsRegistry.Default.Find(key));
    }
}
