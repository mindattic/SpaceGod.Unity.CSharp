using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Unity.SpaceGod.Core;

namespace Bench.CSharp
{
    /// <summary>
    /// Bench.CSharp: runs one named scenario of Bench/scenarios.json over Unity.SpaceGod's own C# Core, headless, and
    /// writes its digest and timings to Bench/results/csharp-&lt;scenario&gt;.json (+ .digest.txt). One scenario per
    /// process, so the peak working set is that scenario's own. See Bench/README.md.
    ///
    ///   Bench.CSharp --list
    ///   Bench.CSharp --scenario sol-idle [--scenarios path] [--out dir] [--ticks N] [--trace N] [--dump-at T]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            string scenariosPath = null, outDir = null, name = null;
            bool list = false;
            int? ticksOverride = null;
            int traceEvery = 0, dumpAt = -1;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--scenarios": scenariosPath = args[++i]; break;
                    case "--out": outDir = args[++i]; break;
                    case "--scenario": name = args[++i]; break;
                    case "--ticks": ticksOverride = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--trace": traceEvery = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--dump-at": dumpAt = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--list": list = true; break;
                    default: Console.Error.WriteLine("unknown argument " + args[i]); return 2;
                }
            }
            scenariosPath ??= FindScenarios();
            if (scenariosPath == null) { Console.Error.WriteLine("Bench/scenarios.json not found; pass --scenarios"); return 2; }
            outDir ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenariosPath)), "results");

            var file = ScenarioFile.Load(scenariosPath);
            if (list)
            {
                foreach (var s in file.Scenarios) Console.WriteLine(s.Name);
                return 0;
            }
            var scenario = file.Find(name);
            if (scenario == null) { Console.Error.WriteLine("no scenario '" + name + "' in " + scenariosPath + " (--list names them)"); return 2; }
            if (ticksOverride.HasValue) scenario.Ticks = ticksOverride.Value;

            Directory.CreateDirectory(outDir);
            var result = Run(file, scenario, outDir, traceEvery, dumpAt);
            string json = Path.Combine(outDir, "csharp-" + scenario.Name + ".json");
            File.WriteAllText(Path.Combine(outDir, "csharp-" + scenario.Name + ".digest.txt"), result.DigestText);
            File.WriteAllText(json, result.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.WriteLine(Summary(scenario.Name, result.Json));
            return 0;
        }

        sealed class RunResult
        {
            public JsonObject Json;
            public string DigestText;
        }

        static RunResult Run(ScenarioFile file, Scenario sc, string outDir, int traceEvery, int dumpAt)
        {
            var proc = Process.GetCurrentProcess();
            long alloc0 = GC.GetTotalAllocatedBytes(true);
            var total = Stopwatch.StartNew();

            var sw = Stopwatch.StartNew();
            var sim = Simulation.Build(file.StartJd, file.Seed);
            sw.Stop();
            double buildMs = sw.Elapsed.TotalMilliseconds;
            long alloc1 = GC.GetTotalAllocatedBytes(true);

            total.Stop();
            string buildDigest = Digest.Write(sim, sc.Name);
            total.Start();

            // The scenario's warp, then its setup (which may change it, as the game's own calls do).
            sim.Clock.SetWarp(sc.Warp);
            Setup(sim, sc);
            // The player's own '.' / ',' after the setup (e.g. speeding battle time back up): -1 leaves it.
            if (sc.ThenWarp >= 0) sim.Clock.SetWarp(sc.ThenWarp);

            // UnitySpaceApp.Update: dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f), a float; here a fixed frame time.
            double dt = (double)Math.Min(1f / sc.Fps, 0.1f);
            double startJd = sim.Clock.Jd;
            var tickMs = new List<double>(Math.Min(sc.Ticks, 1 << 20));
            string stop = "ticks";
            // Trace mode (--trace N, --dump-at T), untimed: every N ticks one line of hashes (Digest.TraceLine), and the full
            // digest at tick T, so the first divergence from Bench.Cpp (which writes the same files as cpp-*) can be found.
            StreamWriter trace = traceEvery > 0 ? new StreamWriter(Path.Combine(outDir, "csharp-" + sc.Name + ".trace.txt")) { NewLine = "\n" } : null;
            void TraceAt(int t)
            {
                if (trace == null && t != dumpAt) return;
                total.Stop();
                if (trace != null && t % traceEvery == 0) { trace.WriteLine(Digest.TraceLine(t, Digest.Write(sim, sc.Name))); trace.Flush(); }
                if (t == dumpAt) File.WriteAllText(Path.Combine(outDir, "csharp-" + sc.Name + ".t" + t + ".digest.txt"), Digest.Write(sim, sc.Name));
                total.Start();
            }
            TraceAt(0);
            for (int t = 0; t < sc.Ticks; t++)
            {
                if (Done(sim, sc, startJd, out stop)) break;
                long a = Stopwatch.GetTimestamp();
                sim.Tick(dt);
                long b = Stopwatch.GetTimestamp();
                tickMs.Add((b - a) * 1000.0 / Stopwatch.Frequency);
                stop = "ticks";
                TraceAt(t + 1);
            }
            trace?.Dispose();
            if (stop == "ticks" && Done(sim, sc, startJd, out var late)) stop = late;
            total.Stop();
            long alloc2 = GC.GetTotalAllocatedBytes(true);
            proc.Refresh();

            string digest = Digest.Write(sim, sc.Name);
            ulong hash = Digest.Fnv1a64(digest);

            var sorted = tickMs.ToArray();
            Array.Sort(sorted);
            double sum = 0;
            foreach (var x in sorted) sum += x;

            var json = new JsonObject
            {
                ["track"] = "csharp",
                ["scenario"] = sc.Name,
                ["description"] = sc.Description,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["digestVersion"] = Digest.Version,
                ["startJd"] = file.StartJd,
                ["seed"] = file.Seed,
                ["warp"] = sc.Warp,
                ["thenWarp"] = sc.ThenWarp,
                ["fps"] = sc.Fps,
                ["dtSeconds"] = dt,
                ["ticksRun"] = tickMs.Count,
                ["stopReason"] = stop,
                ["endJd"] = sim.Clock.Jd,
                ["simDaysAdvanced"] = sim.Clock.Jd - startJd,
                ["endWarp"] = sim.Clock.WarpIndex,
                ["buildDigestHash"] = Digest.Hex(Digest.Fnv1a64(buildDigest)),
                ["digestHash"] = Digest.Hex(hash),
                ["digestBytes"] = System.Text.Encoding.UTF8.GetByteCount(digest),
                ["digestFile"] = "csharp-" + sc.Name + ".digest.txt",
                ["timings"] = new JsonObject
                {
                    ["buildMs"] = Round(buildMs),
                    ["tickMeanMs"] = Round(sorted.Length > 0 ? sum / sorted.Length : 0),
                    ["tickP50Ms"] = Round(Percentile(sorted, 0.50)),
                    ["tickP95Ms"] = Round(Percentile(sorted, 0.95)),
                    ["tickP99Ms"] = Round(Percentile(sorted, 0.99)),
                    ["tickMaxMs"] = Round(sorted.Length > 0 ? sorted[sorted.Length - 1] : 0),
                    ["ticksTotalMs"] = Round(sum),
                    ["totalMs"] = Round(total.Elapsed.TotalMilliseconds),
                },
                ["memory"] = new JsonObject
                {
                    ["allocatedBuildBytes"] = alloc1 - alloc0,
                    ["allocatedRunBytes"] = alloc2 - alloc1,
                    ["peakWorkingSetBytes"] = proc.PeakWorkingSet64,
                },
            };
            return new RunResult { Json = json, DigestText = digest };
        }

        static void Setup(Simulation sim, Scenario sc)
        {
            var w = sim.World;
            switch (sc.Setup?.Kind ?? "none")
            {
                case "none":
                    break;
                case "skirmish":
                {
                    int body = sim.ActiveSystem.Catalog[sc.Setup.Body].Index;
                    var classes = new ShipClass[sc.Setup.Classes.Length];
                    for (int i = 0; i < classes.Length; i++) classes[i] = (ShipClass)Enum.Parse(typeof(ShipClass), sc.Setup.Classes[i]);
                    var battle = sim.TriggerSkirmish(body, FactionOf(w, sc.Setup.Aggressor), FactionOf(w, sc.Setup.Defender), classes);
                    if (battle == null) throw new InvalidOperationException("the skirmish did not start");
                    sim.Skirmish = battle;
                    sim.Clock.Paused = false;
                    sim.WatchBattle(battle);
                    break;
                }
                case "crew-course":
                {
                    var to = sim.Galaxy.Find(sc.Setup.To) ?? throw new InvalidOperationException("no system " + sc.Setup.To);
                    // The first ship (by Id) in the active system that its crew can take there: the one the player follows.
                    bool sent = false;
                    foreach (var ship in w.Fleet.Ships)
                    {
                        if (sc.Setup.Military.HasValue && ship.Spec != null && ship.Spec.IsMilitary != sc.Setup.Military.Value) continue;
                        if (sim.StartCrewCourse(ship, to)) { sent = true; break; }
                    }
                    if (!sent) throw new InvalidOperationException("no ship could be sent to " + sc.Setup.To);
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown setup kind " + sc.Setup.Kind);
            }
        }

        static Faction FactionOf(World w, string name) => name switch
        {
            "Sol" => w.Sol,
            "Belt" => w.Belt,
            "Reach" => w.Reach,
            "Independents" => w.Independents,
            "Pirates" => w.Pirates,
            _ => throw new InvalidOperationException("unknown faction " + name),
        };

        static bool Done(Simulation sim, Scenario sc, double startJd, out string reason)
        {
            reason = null;
            if (sc.SimDays > 0 && sim.Clock.Jd - startJd >= sc.SimDays) { reason = "simDays"; return true; }
            switch (sc.Until ?? "ticks")
            {
                case "battle-over":
                    if (sim.Skirmish != null && sim.Skirmish.Over) { reason = "battle-over"; return true; }
                    break;
                case "course-ended":
                    if (sim.CourseEnded) { reason = sim.CourseArrived ? "course-arrived" : "course-ended"; return true; }
                    break;
            }
            return false;
        }

        static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int i = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, i))];
        }

        static double Round(double ms) => Math.Round(ms, 4);

        static string Summary(string name, JsonObject j)
        {
            var t = j["timings"].AsObject();
            var m = j["memory"].AsObject();
            return string.Format(CultureInfo.InvariantCulture,
                "{0,-14} ticks={1,6} stop={2,-14} days={3,9:0.###} build={4,8:0.0}ms tick mean={5:0.000} p50={6:0.000} p95={7:0.000} max={8:0.000}ms total={9:0.0}ms alloc={10:0.0}MB peakWS={11:0.0}MB digest={12}",
                name, (int)j["ticksRun"], (string)j["stopReason"], (double)j["simDaysAdvanced"], (double)t["buildMs"], (double)t["tickMeanMs"],
                (double)t["tickP50Ms"], (double)t["tickP95Ms"], (double)t["tickMaxMs"], (double)t["totalMs"],
                ((long)m["allocatedBuildBytes"] + (long)m["allocatedRunBytes"]) / 1048576.0, (long)m["peakWorkingSetBytes"] / 1048576.0, (string)j["digestHash"]);
        }

        static string FindScenarios()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (; dir != null; dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "scenarios.json");
                if (File.Exists(p) && dir.Name == "Bench") return p;
                p = Path.Combine(dir.FullName, "Bench", "scenarios.json");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public sealed class ScenarioFile
    {
        public double StartJd;
        public int Seed;
        public List<Scenario> Scenarios = new List<Scenario>();

        public Scenario Find(string name) => Scenarios.Find(s => s.Name == name);

        public static ScenarioFile Load(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            var f = new ScenarioFile
            {
                StartJd = (double)root["startJd"],
                Seed = (int)root["seed"],
            };
            foreach (var n in root["scenarios"].AsArray())
            {
                var o = n.AsObject();
                var s = new Scenario
                {
                    Name = (string)o["name"],
                    Description = (string)o["description"],
                    Warp = (int)o["warp"],
                    Fps = (int)o["fps"],
                    Ticks = (int)o["ticks"],
                    SimDays = o["simDays"] != null ? (double)o["simDays"] : 0,
                    Until = (string)o["until"],
                    ThenWarp = o["thenWarp"] != null ? (int)o["thenWarp"] : -1,
                };
                if (o["setup"] is JsonObject su)
                {
                    s.Setup = new ScenarioSetup
                    {
                        Kind = (string)su["kind"],
                        Body = (string)su["body"],
                        Aggressor = (string)su["aggressor"],
                        Defender = (string)su["defender"],
                        To = (string)su["to"],
                        Military = su["military"] != null ? (bool)su["military"] : (bool?)null,
                    };
                    if (su["classes"] is JsonArray ca)
                    {
                        s.Setup.Classes = new string[ca.Count];
                        for (int i = 0; i < ca.Count; i++) s.Setup.Classes[i] = (string)ca[i];
                    }
                }
                f.Scenarios.Add(s);
            }
            return f;
        }
    }

    public sealed class Scenario
    {
        public string Name, Description, Until;
        public int Warp, Fps, Ticks, ThenWarp = -1;
        public double SimDays;
        public ScenarioSetup Setup;
    }

    public sealed class ScenarioSetup
    {
        public string Kind, Body, Aggressor, Defender, To;
        public string[] Classes;
        public bool? Military;
    }
}
