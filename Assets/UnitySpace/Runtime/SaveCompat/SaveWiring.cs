// US-T70: SpaceGod.Unreal.CSharp's CSharp/Core/SaveCompat/SaveWiring.cs, copied unchanged, so the Unity copy reads the same saves as the
// Unreal C# copy, those of the C++ copy included (docs/PARITY.md "Saves"). Paths in the comments below are that repo's.
// Not part of Unity.SpaceGod (US-T41): the post-load delegate wiring for the C# track, so a save written by the C++ Core
// (Cpp/Source/SpaceGodCore) runs when Unity.SpaceGod's own C# Core loads it.
//
// ObjectGraph saves every delegate as its invocation list and rebuilds it on load; the C++ port cannot (a std::function
// cannot be looked into), so a C++ save holds every delegate as null and the C# simulation would die on its first step
// (Fleet.ChooseNext, Economy.DockAllowed, ...). Unity.SpaceGod's Core has no rewiring step and stays unchanged, so this
// file does what its constructors did, on the loaded objects, in construction order. It mirrors
// Cpp/Source/SpaceGodCore/Persistence/Registrations/Wiring.cpp statement for statement:
//   - each world: World's constructor (the adventure hooks; MarketShocks' Economy.Sold subscription), War's constructor
//     (the escape pods' hooks), then World.Wire / WireMining / WireColonisation (+ WireColonyHulls, called as is),
//     TradeDesk's constructor (trade insurance), and War.Hook (called as is) for every battle still running;
//   - then the galaxy's: InterstellarTraffic's constructor and Register (called as is: gate arrivals, voyage orders,
//     the pirate clans' watch on each war), the deep-space cruises, the polities' ships in transit,
//     InterstellarColonisation's constructor and Wire (called as is).
// For an event with several subscribers the order is construction's. A change to the original's wiring must be repeated
// here (and in Wiring.cpp).
//
// What is wired: where the original assigns a method group (economy.DockAllowed = Law.AllowsDocking) the very same
// method of the very same Core object is bound, so a later C# save writes it exactly as the original's would. Where the
// original wiring method exists on its own and only wires (War.Hook, World.WireColonyHulls, InterstellarTraffic.Register,
// InterstellarColonisation.Wire) it is called, so those delegates are the original's own lambdas. The rest (the lambdas
// inside World.Wire, the War constructor's) are the original lambdas' bodies copied here, verbatim but for reaching
// private members by reflection. Those live outside Core, so ObjectGraph does not save them: a C# save of a rewired game
// holds them as null, and loading it rewires again (RewireIfNeeded), which replaces every slot listed here, so wiring
// twice never doubles a subscription. Handlers from outside Core (the Runtime's toasts, the Bench's counters) are kept,
// after Core's, as in a game that subscribed them after construction.
//
// Plain C# with reflection (no Core internals at compile time), so it compiles into any net8+ project that references a
// build of Unity.SpaceGod.Core: the C# track (CSharp/Script/ManagedUnrealSpaceGodCSharp) and Bench/CSharp.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.SpaceGod.Core;

namespace UnrealSpaceGod.SaveCompat
{
    public static class SaveWiring
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Rewires <paramref name="data"/> if it was loaded without its delegates (a save from the C++ Core, or a C# save
        /// of a game this rewired): true if it did. A save from the C# Core, whose delegates ObjectGraph rebuilt, is left
        /// as it is.
        /// </summary>
        public static bool RewireIfNeeded(SaveData data)
        {
            var g = data?.Galaxy;
            if (g == null || !NeedsRewiring(g)) return false;
            Rewire(g);
            return true;
        }

        /// <summary>A world that was wired (it has a war) and whose fleet has no ChooseNext: its delegates were not saved.</summary>
        public static bool NeedsRewiring(Galaxy g)
        {
            foreach (var s in g.Systems)
                if (s.World?.War != null && s.World.Fleet.ChooseNext == null) return true;
            return false;
        }

        /// <summary>Wires every world of <paramref name="g"/>, then the galaxy, as construction did (see the file comment).</summary>
        public static void Rewire(Galaxy g)
        {
            foreach (var s in g.Systems)
                if (s.World != null) RewireWorld(s.World);
            RewireGalaxy(g);
        }

        // ---------------------------------------------------------------- one world

        static void RewireWorld(World w)
        {
            // ---- World's constructor
            SetEvent(w.Adventures, "Spawned", (Action<Opportunity>)(op =>
                w.Log(op.SpawnJd, op.MarkerLabel + ": " + op.Hook, WorldEventKind.Opportunity, op.Ship?.Orbit?.Body ?? op.Body)));
            SetEvent(w.Adventures, "Resolved", (Action<Opportunity>)(op =>
                w.Log(op.ResolvedJd, op.MarkerLabel + (op.State == OpportunityState.Completed ? " done: " : " lost: ") + op.Outcome,
                    WorldEventKind.Opportunity, op.Ship?.Orbit?.Body ?? op.Body)));
            // MarketShocks' constructor: `world.Economy.Sold += OnSold`, first of Sold's subscribers (Companies' comes in Wire).
            var sold = new List<Delegate>();
            if (w.Shocks != null) sold.Add(Bind(typeof(Action<Ship, Settlement, Good, double>), w.Shocks, "OnSold"));
            // A world World.Wire never ran on (no war) has nothing more.
            var war = w.War;
            if (war == null) { SetField(w.Economy, "Sold", Combine(sold, w.Economy.Sold)); return; }
            var economy = w.Economy;
            var fleet = w.Fleet;
            WarCrews(war, w);

            // ---- World.Wire
            SetEvent(war, "BattleStarted", (Action<Battle>)w.Outlaws.OnBattleStarted,
                // Merchants heading into a fight that has just broken out turn aside for somewhere safe.
                (Action<Battle>)(b => w.DivertAround(b, b.StartJd)));
            // (InterstellarTraffic.Register's pirate-clan watch adds its own to these three, in RewireGalaxy.)
            SetEvent(war, "BattleEnded", (Action<Battle, double>)w.Law.OnBattleEnded, (Action<Battle, double>)w.Outlaws.OnBattleEnded,
                (Action<Battle, double>)w.Contracts.OnBattleEnded);
            SetEvent(war, "Plundered");
            SetEvent(war, "Heat");
            SetEvent(w.Law, "Committed", (Action<Crime>)w.Outlaws.OnCrime);
            // TradeDesk's constructor: the insurance hears of the house's losses.
            if (w.Trade != null)
            {
                SetEvent(w.Holdings, "ShipLost", Bind(typeof(Action<OwnedShip, double, double>), w.Trade.Insurance, "OnShipLost"));
                SetEvent(w.Holdings, "PropertyLost", Bind(typeof(Action<Property, double, double>), w.Trade.Insurance, "OnPropertyLost"));
            }
            WireMining(w);
            economy.DockAllowed = w.Law.AllowsDocking;
            economy.PriceFactor = w.Law.PriceFactorFor;
            WireColonisation(w);
            // Merchants and couriers go down to the surface through the rings; warships and pirates hold orbit, and so does a
            // bulk freighter, built in space and never landing: it trades from orbit (U.SPC-T482).
            fleet.Lands = s => w.IsTrader(s) && !BulkFreighters.NeverLands(s);
            economy.EstimateTravelDays = fleet.EstimateTravelDays;
            fleet.BurnsPropellant = w.BurnsPropellant;
            fleet.CanRefuelAt = economy.HasPropellantAt;
            fleet.ForceTopUp = (ship, body, jd) => economy.Refuel(ship, body, jd, force: true);
            economy.EstimateTrip = fleet.EstimateTrip;
            fleet.RanDry = (ship, when) =>
                w.Log(when, $"{ship.Name} has run its tanks dry off {w.Catalog[ship.Orbit.Body].Name} and calls for a tanker", WorldEventKind.Info, ship.Orbit.Body);
            fleet.TankerArrived = (ship, tonnes, when) =>
            {
                if (tonnes <= 0) return;
                double fee = Propulsion.TankerFee(tonnes);
                ship.Faction.Treasury -= fee;
                ship.Credits -= fee;
                w.Log(when, $"A tanker reaches {ship.Name} with {tonnes:N0} t of reaction mass", WorldEventKind.Info, ship.Orbit.Body);
            };
            var payToll = Fn<Action<Ship, LaneHop, double>>(w, "PayToll");
            fleet.LaneJoined = (ship, hop, when) =>
            {
                payToll(ship, hop, when);
                // A worn lane's rings may slip under the rider (U.SPC-T481).
                w.LaneUpkeep.OnLaneJoined(ship, hop, when);
            };
            fleet.Leaked = (ship, tonnes, when) =>
                w.Log(when, $"{ship.Name} vents {tonnes:N0} t of reaction mass from a holed tank", WorldEventKind.Info, ship.Orbit?.Body ?? -1);
            economy.Danger = body => war.Danger(body) * 0.08;
            var companies = w.Companies;
            SetField(economy, "Loaded", Combine(new List<Delegate> { (Action<Ship, Settlement>)companies.OnLoaded }, economy.Loaded));
            sold.Add((Action<Ship, Settlement, Good, double>)companies.OnSold);
            SetField(economy, "Sold", Combine(sold, economy.Sold));
            economy.RouteFactor = Companies.RouteFactor;
            economy.PaidFor = companies.PaidFor;
            SetEvent(w, "ShipAdded", (Action<Ship>)companies.OnShipAdded);
            // Each polity's production focus steers which of its industries grow (U.SPC-T423).
            economy.Priority = (s, kind) => w.Polities.Command.Priority(s.Owner, kind);
            // A lane short of its tenders comes first: everything else rides the lanes (U.SPC-T481).
            var requisition = Fn<Func<Settlement, ShipClass?>>(w, "Requisition");
            var chooseBuild = Fn<Func<Settlement, ShipClass?>>(w, "ChooseBuild");
            economy.ChooseBuild = yard => w.LaneUpkeep.OrderAt(yard) ?? (w.Companies.Enabled
                ? requisition(yard) ?? w.Companies.OrderAt(yard) ?? war.ChooseWarship(yard)
                : w.TraderCount() < w.MerchantTarget ? chooseBuild(yard) : war.ChooseWarship(yard) ?? chooseBuild(yard));
            economy.ShipBuilt = (Action<Settlement, ShipClass, double>)Bind(typeof(Action<Settlement, ShipClass, double>), w, "OnShipBuilt");
            economy.CanCrew = (yard, cls) => Crews.CanCrew(yard.Owner, cls);
            fleet.BeforeEvent = economy.AdvanceTo;
            // A group running the player down is flown by the chase, not its schedule (U.SPC-T273).
            fleet.IsHeld = ship => ship.Battle != null || ship.Derelict || ship == w.PilotedShip || (ship.Voyage != null && ship.Voyage.AtGate) ||
                                   (w.Pursuit != null && w.Pursuit.Active && w.Pursuit.Of(ship) != null);
            // The player's captains keep their own time in port (mining a claim, waiting on a charge).
            fleet.Dwell = (ship, normal) => w.Holdings.Owns(ship) ? w.Holdings.Dwell(ship, normal)
                : ship.Role == ShipRole.Tender ? w.LaneUpkeep.Dwell(ship)
                : ship.Role == ShipRole.Tug || ship.Role == ShipRole.Hauler ? normal : war.Dwell(ship, normal);
            var colonyHullArrived = Fn<Func<Ship, double, bool>>(w, "ColonyHullArrived");
            var tugArrived = Fn<Action<Ship, double>>(w, "TugArrived");
            var freighterArrived = Fn<Action<Ship, double>>(w, "FreighterArrived");
            fleet.Arrived = (ship, when) =>
            {
                // At a jump gate, not a port: the interstellar traffic takes it from here.
                if (ship.GateKm != null && ship.Orbit.Body == w.Catalog.Root.Index)
                {
                    ((Action<Ship, double>)GetField(w, "ReachedGate"))?.Invoke(ship, when);
                    return;
                }
                // A freighter bound for a jump gate carries goods for another system's markets: it keeps them through
                // any port it passes on the way to the gate (selling there skipped the gate's toll and the far market).
                bool bound = ship.Voyage != null && ship.GateKm != null;
                // A colony ship's hull lands its colonists where they are bound, and is broken up there for a new settlement (U.SPC-T524).
                if (ship.Role == ShipRole.Colonist && colonyHullArrived(ship, when)) return;
                // Contract cargo and people go to the contract, not the market (U.SPC-T101).
                // An ore tug's works latches its load on out in the field, or takes it in where it is for (U.SPC-T498).
                if (ship.Role == ShipRole.Tug) tugArrived(ship, when);
                // A works' freighter empties its hold into the warehouse its convoy is for (U.SPC-T523).
                if (ship.Role == ShipRole.Hauler) freighterArrived(ship, when);
                bool contracted = w.Contracts.OnArrived(ship, ship.Orbit.Body, when);
                if (w.IsTrader(ship) && !bound && !contracted) economy.OnArrive(ship, ship.Orbit.Body, when);
                war.OnArrived(ship, when);
            };
            fleet.ReachedRing = war.OnArrived;
            // A free ship does not wander to a known haven, or an armed port, of someone hostile to it: their platforms
            // would fire on it every time it came back (U.SPC-T291).
            fleet.Shuns = (ship, body) =>
            {
                // Local traffic keeps clear of a fight (U.SPC-T375).
                if (ship.Role == ShipRole.Commuter && w.War.BattleAt(body) != null) return true;
                foreach (var s in economy.At(body))
                    if ((s.Kind == SettlementKind.Haven || s.Platforms.Count > 0) && w.Diplomacy.Hostile(ship.Faction, s.Owner)) return true;
                return false;
            };
            var colonyHullNext = Fn<Func<Ship, int, double, int>>(w, "ColonyHullNext");
            fleet.ChooseNext = (ship, here, when) =>
            {
                // In port, a ship on propellant tops up its tanks before it plans the next run.
                if (w.BurnsPropellant(ship)) economy.Refuel(ship, here, when);
                if (ship.Voyage != null && w.VoyageOrders != null)
                {
                    int ordered = w.VoyageOrders(ship, here, when);
                    if (ordered >= 0) return ordered;
                }
                // A colony ship's hull flies its voyage: to the site, or home with colonists it could not land (U.SPC-T524).
                if (ship.Role == ShipRole.Colonist)
                {
                    int colonyNext = colonyHullNext(ship, here, when);
                    if (colonyNext >= 0) return colonyNext;
                }
                // A lane tender works its lane (U.SPC-T481).
                if (ship.Role == ShipRole.Tender && !w.Holdings.Owns(ship)) return w.LaneUpkeep.ChooseNext(ship, here, when);
                // An ore tug goes where its belt works sends it (U.SPC-T498).
                if (ship.Role == ShipRole.Tug && !w.Holdings.Owns(ship))
                {
                    int tugNext = w.Mining?.TugNext(ship, here) ?? -1;
                    return tugNext >= 0 ? tugNext : here;
                }
                // A works' freighter goes where its belt works sends it (U.SPC-T523).
                if (ship.Role == ShipRole.Hauler && !w.Holdings.Owns(ship))
                {
                    int haulerNext = w.Mining?.FreighterNext(ship, here) ?? -1;
                    return haulerNext >= 0 ? haulerNext : here;
                }
                // A clan's smuggler lies at its haven between runs (U.SPC-T94).
                if (ship.Runner) return ship.Route != null && ship.Route.Length > 0 ? ship.Route[0] : here;
                // The player's own ships go where their captains' standing orders take them.
                if (w.Holdings.Owns(ship)) return w.Holdings.ChooseNext(ship, here, when);
                // Local traffic (U.SPC-T375) keeps to its line or its whims, the fleet's own choice.
                if (ship.Role == ShipRole.Commuter) return -1;
                // A merchant, courier or free ship on a contract goes where it takes it; an idle one may take one here.
                if (w.IsTrader(ship) || ship.Role == ShipRole.Free)
                {
                    int job = w.Contracts.ChooseNext(ship, here, when);
                    if (job >= 0) return job;
                }
                // Free ships follow their own whims (the fleet's built-in choice), not orders or markets -- unless
                // their tanks are getting low, when even an idle wanderer heads for somewhere to fill them.
                if (ship.Role == ShipRole.Free)
                    return ship.PropellantFraction < 0.4 && !economy.HasPropellantAt(ship, here) ? economy.NearestPropellant(ship, here, when) : -1;
                if (!w.IsTrader(ship)) return war.ChooseNext(ship, here, when);
                int next = economy.PlanTrade(ship, here, when);
                if (next < 0 && !ship.HasCargo) ship.Cargo = "Empty, looking for work";
                return next;
            };

            // ---- War.StartBattle's hooks (War.Hook, the original's own), for the battles under way
            var hook = Fn<Action<Battle>>(war, "Hook");
            foreach (var b in war.Battles.ToList())
            {
                // Hook adds to UnitSurrendered, SurrenderDecided and CargoDemanded: a save that kept them is not doubled.
                foreach (var f in new[] { "UnitDestroyed", "Boards", "WantsAlive", "Launch", "FighterStep", "UnitCaptured", "UnitSurrendered", "SurrenderDecided", "CargoDemanded" })
                    SetField(b, f, Combine(new List<Delegate>(), (Delegate)GetField(b, f)));
                hook(b);
            }
        }

        /// <summary>War's constructor: the escape pods' hooks.</summary>
        static void WarCrews(War war, World world)
        {
            var crews = war.Crews;
            var podBody = SFn<Func<EscapePod, int>>(typeof(War), "PodBody");
            var cap = SFn<Func<string, string>>(typeof(War), "Cap");
            crews.HavenOf = war.HomeHaven;
            crews.RaidersTaken = (pod, by, when) =>
            {
                if (by.IsMilitary && pod.Haven != null) war.Discover(pod.Haven, by, when, $"raiders picked up from the {pod.Name}'s escape pods give it away");
            };
            SetEvent(crews, "Launched", (Action<EscapePod>)(pod => world.Log(pod.LaunchedJd,
                $"The {pod.Name} launches {pod.Pods} escape pod{(pod.Pods == 1 ? "" : "s")}: {pod.People:0} {pod.Owners.Name} crew adrift and broadcasting", WorldEventKind.Loss, podBody(pod))));
            SetEvent(crews, "Resolved", (Action<EscapePod, Ship>)((pod, ship) =>
            {
                string by = ship != null ? "the " + ship.Name : pod.TakenBy != null ? "the " + pod.TakenBy.Name : "nobody";
                string text = pod.Breacher ? pod.State switch
                {
                    // A drifting Breacher round (U.SPC-T519).
                    PodState.Rescued => $"{cap(by)} tows in the {pod.Name}: {pod.People:0} {pod.Owners.Name} marines home safe",
                    PodState.Hostage => $"{cap(by)} salvages the {pod.Name} and holds its {pod.People:0} marines for ransom or sale",
                    PodState.Prisoner => $"{cap(by)} tows in the {pod.Name} and takes its {pod.People:0} marines prisoner",
                    _ => $"The {pod.Name} falls silent: its air ran out with {pod.People:0} marines inside",
                } : pod.State switch
                {
                    PodState.Rescued => $"{cap(by)} picks up the {pod.Name}'s escape pods: {pod.People:0} crew safe",
                    PodState.Hostage => $"{cap(by)} hauls in the {pod.Name}'s escape pods and holds {pod.People:0} crew for ransom",
                    PodState.Prisoner => $"{cap(by)} picks up the {pod.Name}'s escape pods and takes {pod.People:0} prisoners",
                    _ => $"The {pod.Name}'s escape pods fall silent: their air ran out with {pod.People:0} aboard",
                };
                world.Log(pod.ResolvedJd, text, pod.State == PodState.Lost ? WorldEventKind.Loss : WorldEventKind.Info, podBody(pod));
            }),
                // World.Wire: `war.Crews.Resolved += Outlaws.OnPodResolved`, after the constructor's.
                (Action<EscapePod, Ship>)world.Outlaws.OnPodResolved);
            SetEvent(crews, "CaptivesReleased", (Action<Captives, string, double>)((c, how, when) => world.Log(when, how, WorldEventKind.Info, c.Haven?.Body ?? -1)));
        }

        /// <summary>World.WireMining, the delegate statements (the pipeline itself is saved).</summary>
        static void WireMining(World w)
        {
            var m = w.Mining;
            if (m == null) return;
            m.Position = (body, jd) => w.Ephemeris.HelioAt(body, jd);
            Group(m, "PiratePressure", w, "MiningPiratePressure");
            m.Raiders = () => w.Pirates;
            m.Guarded = wh =>
            {
                foreach (var s in w.Economy.Settlements)
                    if (s.Body == wh.Body && s.Owner == wh.Owner && !s.Fallen && s.Platforms.Count > 0) return true;
                return false;
            };
            m.Log = (jd, text, body) => w.Log(jd, text, WorldEventKind.Economy, body);
            Group(m, "HeavyLane", w, "MiningHeavyLane");
            // The ore tugs are real hulls in the fleet (U.SPC-T498): the works sends them, the fleet flies them.
            Group(m, "TugFree", w, "TugFree");
            Group(m, "HaulSent", w, "TugSent");
            Group(m, "TugThreat", w, "TugThreat");
            Group(m, "TugSeized", w, "HandOverTug");
            // So are the freighters its raw-material convoys sail in (U.SPC-T523): the works loads them, the fleet flies them.
            Group(m, "FreighterFree", w, "FreighterFree");
            Group(m, "ConvoySailed", w, "FreighterSailed");
            Group(m, "ConvoyDelivered", w, "FreighterDelivered");
            m.YardBuilt = (p, jd) =>
            {
                foreach (var s in w.Economy.Settlements)
                {
                    if (s.Body != p.SiteBody || s.Owner != p.Builder || s.Fallen) continue;
                    foreach (var f in s.Facilities)
                        if (f.Recipe.Kind == IndustryKind.Shipyard)
                        {
                            f.Level += 1;
                            f.BaseLevel += 1;
                            return;
                        }
                    s.Facilities.Add(new Facility(Recipe.Of(IndustryKind.Shipyard), 1));
                    return;
                }
            };
        }

        /// <summary>World.WireColonisation, the delegate statements (the colonisation itself is saved), then WireColonyHulls as is.</summary>
        static void WireColonisation(World w)
        {
            var c = w.Colonisation;
            if (c == null) return;
            c.TravelDays = (from, body, when) =>
                Contracts.TravelDays(from.Body == body ? 0 : (w.Ephemeris.HelioAt(from.Body, when) - w.Ephemeris.HelioAt(body, when)).Length);
            c.Launched = v => w.Log(v.DepartJd, v.Reinforcing != null
                ? $"The colony ship {v.Name} sails from {v.From.Name} with {v.Colonists:N0} settlers for {v.Reinforcing.Name}"
                : $"The colony ship {v.Name} sails from {v.From.Name} with {v.Colonists:N0} colonists to settle {v.Site.Name}" +
                  (v.Abroad ? $" in {v.Where}, through the gates" : ""), WorldEventKind.Launch, v.From.Body);
            c.Landed = (v, s) => w.Log(w.Economy.Jd, s != null
                ? $"The colony ship {v.Name} lands {v.Colonists:N0} settlers at {s.Name}"
                : $"The colony ship {v.Name} finds {v.Site.Name}{(v.Abroad ? " in " + v.Where : "")} taken and carries its colonists home",
                WorldEventKind.Info, v.Abroad ? v.From.Body : v.Body);
            c.Founded = s =>
            {
                // Settlers from another system bring their polity here with them (U.SPC-T293).
                w.Fleet.AddFaction(s.Owner);
                w.Log(w.Economy.Jd, $"{s.Owner.Name} colonists found {s.Name}, an outpost of {s.Population:N0} " +
                                    (w.Catalog[s.Body].HabitatSite ? "in a new habitat" : "on " + w.Catalog[s.Body].Name), WorldEventKind.Economy, s.Body);
            };
            c.Expanded = s => w.Log(w.Economy.Jd, $"{s.Name} builds out its habitat for {s.InitialPopulation * People.MaxGrowth:N0} people", WorldEventKind.Economy, s.Body);
            c.Promoted = s => w.Log(w.Economy.Jd, $"{s.Name} has grown from an outpost into a colony of {s.Population:N0}", WorldEventKind.Economy, s.Body);
            c.Ruined = s => w.Log(w.Economy.Jd, $"{s.Name} has failed: its last settlers leave and it stands empty, a ruin", WorldEventKind.Loss, s.Body);
            Fn<Action<Colonisation>>(w, "WireColonyHulls")(c);
        }

        // ---------------------------------------------------------------- the galaxy

        static void RewireGalaxy(Galaxy g)
        {
            var traffic = g.Traffic;
            if (traffic != null)
            {
                // InterstellarTraffic's constructor: Register (the original's) for each system, then the cruises.
                var register = Fn<Action<StarSystem>>(traffic, "Register");
                foreach (var s in g.Systems)
                {
                    if (s.World == null) continue;
                    SetEvent(s.World, "ReachedGate");
                    register(s);
                }
                SetEvent(g.DeepSpace, "Arrived", Bind(typeof(Action<Cruise, double>), traffic, "OnCruiseArrived"));
                SetEvent(g.DeepSpace, "ReachedStar", Bind(typeof(Action<Cruise, double>), traffic, "OnReachedStar"));
                // Galaxy.Connect: `Polities.InTransit = Traffic.InTransit`.
                g.Polities.InTransit = traffic.InTransit;
            }
            var colonisation = g.Colonisation;
            if (colonisation != null)
            {
                // InterstellarColonisation's constructor: Wire (the original's) for each system, and for each one added.
                var wire = Fn<Action<StarSystem>>(colonisation, "Wire");
                foreach (var s in g.Systems)
                    if (s.World != null) wire(s);
                SetEvent(g, "SystemAdded", (Action<StarSystem, double>)((s, jd) => wire(s)));
            }
            else SetEvent(g, "SystemAdded");
        }

        // ---------------------------------------------------------------- reflection

        /// <summary>
        /// Sets the delegate field behind <paramref name="name"/> (an event's backing field, or a multicast delegate field)
        /// to <paramref name="core"/> in order, then whatever handlers it held from outside Core (kept, after Core's).
        /// </summary>
        static void SetEvent(object owner, string name, params Delegate[] core)
        {
            if (owner == null) return;
            SetField(owner, name, Combine(core.ToList(), (Delegate)GetField(owner, name)));
        }

        /// <summary><paramref name="core"/>, then the handlers of <paramref name="old"/> whose methods are not Core's.</summary>
        static Delegate Combine(List<Delegate> core, Delegate old)
        {
            var all = new List<Delegate>(core);
            if (old != null)
                foreach (var d in old.GetInvocationList())
                    if (d.Method.DeclaringType?.Assembly != CoreAssembly) all.Add(d);
            return Delegate.Combine(all.ToArray());
        }

        static readonly Assembly CoreAssembly = typeof(World).Assembly;

        static FieldInfo FieldOf(Type t, string name)
        {
            for (var c = t; c != null; c = c.BaseType)
            {
                var f = c.GetField(name, Any | BindingFlags.DeclaredOnly) ?? c.GetField("<" + name + ">k__BackingField", Any | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            throw new MissingFieldException(t.FullName, name);
        }

        static object GetField(object owner, string name) => FieldOf(owner.GetType(), name).GetValue(owner);

        static void SetField(object owner, string name, object value) => FieldOf(owner.GetType(), name).SetValue(owner, value);

        /// <summary>`owner.member = target.method` (a method group, private ones too), as a delegate of the member's own type.</summary>
        static void Group(object owner, string member, object target, string method) =>
            SetField(owner, member, Bind(FieldOf(owner.GetType(), member).FieldType, target, method));

        /// <summary>A delegate of <paramref name="type"/> to <paramref name="target"/>'s method <paramref name="name"/> (the overload that fits).</summary>
        static Delegate Bind(Type type, object target, string name)
        {
            var ps = type.GetMethod("Invoke").GetParameters().Select(p => p.ParameterType).ToArray();
            for (var c = target.GetType(); c != null; c = c.BaseType)
            {
                var m = c.GetMethod(name, Any | BindingFlags.DeclaredOnly, null, ps, null);
                if (m != null) return m.IsStatic ? Delegate.CreateDelegate(type, m) : Delegate.CreateDelegate(type, target, m);
            }
            throw new MissingMethodException(target.GetType().FullName, name);
        }

        static T Fn<T>(object target, string name) where T : Delegate => (T)Bind(typeof(T), target, name);

        static T SFn<T>(Type type, string name) where T : Delegate
        {
            var ps = typeof(T).GetMethod("Invoke").GetParameters().Select(p => p.ParameterType).ToArray();
            var m = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, ps, null)
                    ?? throw new MissingMethodException(type.FullName, name);
            return (T)Delegate.CreateDelegate(typeof(T), m);
        }
    }
}
