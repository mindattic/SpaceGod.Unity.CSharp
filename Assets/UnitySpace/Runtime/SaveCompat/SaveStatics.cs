// US-T70: SpaceGod.Unreal.CSharp's CSharp/Core/SaveCompat/SaveStatics.cs, copied unchanged, so the Unity copy reads the same saves as the
// Unreal C# copy, those of the C++ copy included (docs/PARITY.md "Saves"). Paths in the comments below are that repo's.
// Not part of Unity.SpaceGod (US-T41): the C# track's handling of an original bug in ObjectGraph's static-object table.
//
// The bug (Unity.SpaceGod Core/Persistence/ObjectGraph.cs, Statics.Ensure): to write an object that static data also holds
// (a ship class spec, a weapon table, ...) by name instead of by value, ObjectGraph walks every static field of every Core
// type once per process, at the first save or load, and names each object by the first path that reaches it. Two of those
// statics are ConditionalWeakTable caches keyed by live simulation objects: FlightDecks.decks (keyed by each carrier's
// BattleUnit) and ArmourAngling.Tables (keyed by CombatSpec). The walk enumerates them (a ConditionalWeakTable is a
// generic IEnumerable of KeyValuePairs), so it names the live battle units, their ships, factions, worlds and everything
// reachable from them ("Unity.SpaceGod.Core.FlightDecks::decks[0].key.Faction"), and it names the combat specs through the
// angling cache before their own table ("ArmourAngling::Tables[3].key" instead of "CombatSpec::Specs{0}[3]"). A save
// then writes those objects as bare names (no contents), and a process whose caches differ -- any fresh process, and any
// whose garbage collector has dropped other entries -- cannot load it ("The save refers to a static object this build
// does not have"). After Galaxy.CreateDefault (whose opening battles fill the deck cache) the walk names ~400 live objects
// this way; a save made then is lost.
//
// The fix (the same in both tracks): the walk does not look into ConditionalWeakTable caches: what they hold is the
// simulation's, not static data. The C++ Core's walk never declared them (Persistence/Registrations/Statics.cpp).
// Unity.SpaceGod's source stays unchanged, so this rebuilds ObjectGraph's private table with ObjectGraph's own Walk over
// the same statics in the same order, those two caches left out, and installs it before the first save or load
// (EnsureFixed), as Ensure would have built it then. Such saves name only real static data, so they load wherever the
// table is built this way (here, the C++ Core); saves the unfixed original made once those caches held anything remain
// unloadable in a fresh process, as before (their named objects' contents were never written).

#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Unity.SpaceGod.Core;

namespace UnrealSpaceGod.SaveCompat
{
    public static class SaveStatics
    {
        static readonly object gate = new object();
        static bool fixedUp;

        /// <summary>
        /// Before the first save or load of the process: builds ObjectGraph's static-object table without the
        /// ConditionalWeakTable caches (see the file comment). Later calls do nothing, as ObjectGraph builds its table once.
        /// </summary>
        public static void EnsureFixed()
        {
            lock (gate)
            {
                if (fixedUp) return;
                Rebuild();
                fixedUp = true;
            }
        }

        /// <summary>ObjectGraph's private static table (type ObjectGraph+Statics), built again now without the weak-table caches.</summary>
        public static void Rebuild()
        {
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var og = typeof(ObjectGraph);
            var statics = og.GetNestedType("Statics", BindingFlags.NonPublic) ?? throw new MissingMemberException(og.FullName, "Statics");
            var walk = statics.GetMethod("Walk", S) ?? throw new MissingMethodException(statics.FullName, "Walk");
            var refEq = (IEqualityComparer<object>)og.GetNestedType("RefEq", BindingFlags.NonPublic).GetField("Instance", S).GetValue(null);
            var core = og.Assembly;
            lock (statics.GetField("gate", S).GetValue(null))
            {
                // Statics.Ensure, verbatim but for the one skip.
                var k = new Dictionary<object, string>(refEq);
                var b = new Dictionary<string, object>();
                Type[] types;
                try { types = core.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types.Where(t => !t.ContainsGenericParameters).OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                 .OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        if (f.IsLiteral) continue;
                        if (IsWeakCache(f.FieldType)) continue; // the fix
                        object v;
                        try { v = f.GetValue(null); }
                        catch { continue; }
                        walk.Invoke(null, new object[] { v, t.FullName + "::" + f.Name, 0, k, b });
                    }
                }
                statics.GetField("byKey", S).SetValue(null, b);
                statics.GetField("keyOf", S).SetValue(null, k);
            }
        }

        static bool IsWeakCache(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>);

        /// <summary>Every key of ObjectGraph's static table, in walk order (building it as ObjectGraph would if not yet built).</summary>
        public static List<string> Keys()
        {
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var statics = typeof(ObjectGraph).GetNestedType("Statics", BindingFlags.NonPublic);
            statics.GetMethod("TryKey", S).Invoke(null, new object[] { new object(), null });
            var byKey = (IDictionary)statics.GetField("byKey", S).GetValue(null);
            var keys = new List<string>();
            foreach (DictionaryEntry e in byKey) keys.Add((string)e.Key);
            return keys;
        }
    }
}
