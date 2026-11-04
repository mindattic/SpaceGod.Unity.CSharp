// US-T43: the HUD's actions that write the Core, in co-op (new file, a partial of the ported Hud; docs/MIGRATION.md "Co-op").
// The ported panels call in here only at lines marked US-T43: in single-player every call is false and the original goes on.
using System;
using System.Collections.Generic;
using Unity.SpaceGod.Coop;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.UI
{
    public sealed partial class Hud
    {
        /// <summary>
        /// A panel's button that writes the Core (a trade, a fit, a contract, the house's books...), in co-op: the action goes
        /// into the frames by what it acts on (an index each peer reads back the same: a settlement, an owned hull, a row of a
        /// board), and <see cref="CoopExecute"/> does at its frame, on every peer, what the button does. False in
        /// single-player and while carrying it out: the original goes on.
        /// </summary>
        bool CoopSends(string verb, params object[] args)
        {
            if (CoopPeer.Current == null) return false;
            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++) parts[i] = Convert.ToString(args[i], System.Globalization.CultureInfo.InvariantCulture);
            return app.CoopSends(verb, string.Join(" ", parts));
        }

        int K(Settlement s) => s == null ? -1 : app.Economy.Settlements.IndexOf(s);
        Settlement Port(string k) { int i = Wire.ParseI(k); var all = app.Economy.Settlements; return i >= 0 && i < all.Count ? all[i] : null; }
        static int K<T>(IEnumerable<T> list, T item) where T : class
        {
            int i = 0;
            foreach (var x in list)
            {
                if (ReferenceEquals(x, item)) return i;
                i++;
            }
            return -1;
        }

        static T At<T>(IEnumerable<T> list, string k) where T : class
        {
            int want = Wire.ParseI(k), i = 0;
            if (want < 0) return null;
            foreach (var x in list)
                if (i++ == want) return x;
            return null;
        }

        /// <summary>US-T47: the galaxy map's screen (made if it was never opened), for the smoke run's clicks.</summary>
        internal GalaxyMapScreen CoopMap => galaxyMap ??= new GalaxyMapScreen(app);

        long observedFrame = -1;
        Settlement observedAt;

        /// <summary>Prices seen in port go into the trade desk's intelligence (the market panel's Layout pass): in co-op once a
        /// frame per market, as an entry (true: sent, or already sent this frame).</summary>
        bool CoopObserve(Settlement market)
        {
            var peer = CoopPeer.Current;
            if (peer == null) return false;
            if (observedFrame == peer.Frame && observedAt == market) return true;
            observedFrame = peer.Frame;
            observedAt = market;
            return CoopSends("observe", K(market));
        }

        /// <summary>The outfitting panel's Fit/Strip, by the yard, the row and the option chosen.</summary>
        bool CoopSendsOutfit(Settlement yard, (ModuleKind? module, FacilityKind facility, string name) option) =>
            CoopSends("outfit", K(yard), outfitSlot, option.module.HasValue ? (int)option.module.Value : -1,
                option.facility != null ? K(FacilityKinds.Catalogue, option.facility) : -1);

        /// <summary>A frame's HUD action, on every peer: what the button does (the original's call, by the same objects read
        /// back from their keys). False for a verb the HUD does not know.</summary>
        internal bool CoopExecute(string verb, string[] a)
        {
            var world = app.World;
            double jd = app.Clock.Jd;
            switch (verb)
            {
                // ---- the market board
                case "buy":
                {
                    var market = Port(a[0]);
                    if (market == null || world.PilotedShip == null) return true;
                    int was = tradeCrates;
                    tradeCrates = Wire.ParseI(a[2]);
                    try { ClickBuy(market, (Good)Wire.ParseI(a[1])); }
                    finally { tradeCrates = was; }
                    return true;
                }
                case "sell":
                {
                    var market = Port(a[0]);
                    if (market == null || world.PilotedShip == null) return true;
                    int was = tradeCrates;
                    tradeCrates = Wire.ParseI(a[2]);
                    try { ClickSell(market, (Good)Wire.ParseI(a[1])); }
                    finally { tradeCrates = was; }
                    return true;
                }
                case "refuel":
                {
                    var market = Port(a[0]);
                    if (market != null && world.PilotedShip != null) ClickRefuel(market);
                    return true;
                }
                case "observe":
                {
                    var market = Port(a[0]);
                    if (market != null) world.Trade?.Intel.Observe(market, jd, "seen in port");
                    return true;
                }
                case "bounty":
                {
                    var market = Port(a[0]);
                    if (market != null && world.Law != null) PayBounty(market);
                    return true;
                }
                // ---- outfitting
                case "prisoners":
                {
                    var port = Port(a[0]);
                    var ship = world.PilotedShip;
                    if (port != null && ship != null) HandOverPrisoners(ship, port);
                    return true;
                }
                case "outfit":
                {
                    var yard = Port(a[0]);
                    var ship = world.PilotedShip;
                    if (yard == null || ship == null || ship.Combat == null) return true;
                    var loadout = Outfitting.LoadoutOf(ship);
                    int slot = Wire.ParseI(a[1]), module = Wire.ParseI(a[2]), facility = Wire.ParseI(a[3]);
                    (ModuleKind? module, FacilityKind facility, string name)? chosen = null;
                    foreach (var o in OutfitOptions(loadout, slot))
                        if ((o.module.HasValue ? (int)o.module.Value : -1) == module &&
                            (o.facility != null ? K(FacilityKinds.Catalogue, o.facility) : -1) == facility) { chosen = o; break; }
                    if (chosen == null) { app.ShowToast("Can't fit that here"); return true; }
                    int was = outfitSlot;
                    outfitSlot = slot;
                    try { OutfitApply(ship, yard, loadout, chosen.Value); }
                    finally { if (!app.CoopActingHere) outfitSlot = was; } // the row picked is the asker's panel's
                    return true;
                }
                // ---- the bounty board's brig
                case "deliver":
                case "ransom":
                {
                    var port = Port(a[0]);
                    var outlaws = world.Outlaws;
                    var ship = world.PilotedShip;
                    if (port == null || outlaws == null || ship == null) return true;
                    var o = At(outlaws.HeldAboard(ship), a[1]);
                    if (o == null) return true;
                    if (verb == "deliver") DeliverOutlaw(outlaws, o, port);
                    else RansomOutlaw(outlaws, o, port);
                    return true;
                }
                // ---- contracts
                case "ctake":
                {
                    var port = Port(a[0]);
                    var contracts = world.Contracts;
                    var ship = world.PilotedShip;
                    if (port == null || contracts == null || ship == null) return true;
                    var c = At(contracts.BoardAt(port), a[1]);
                    if (c != null) TakeContract(contracts, c, ship, jd);
                    return true;
                }
                case "cabandon":
                {
                    var contracts = world.Contracts;
                    var c = contracts != null ? At(contracts.PlayerActive(), a[0]) : null;
                    if (c != null && contracts.Abandon(c, jd)) app.ShowToast("Contract abandoned: " + c.Title);
                    return true;
                }
                // ---- the player's fleet
                case "deposit":
                case "withdraw":
                case "enlist":
                {
                    var h = world.Holdings;
                    var flown = world.PilotedShip;
                    var purse = flown != null && h != null && flown.Faction != h.House ? flown.Faction : null;
                    if (purse == null) return true;
                    if (verb == "deposit") app.ShowToast(h.Deposit(purse, 1e7, jd) ? "10 M cr into the house account" : "The purse cannot spare it");
                    else if (verb == "withdraw") app.ShowToast(h.Withdraw(purse, 1e7, jd) ? "10 M cr out to the " + purse.Name : "Not that much in the account");
                    else if (!h.Owns(flown)) app.ShowToast(h.Enlist(flown, jd) != null ? flown.Name + " flies your flag now" : "It cannot join the fleet");
                    return true;
                }
                case "hull":
                {
                    var h = world.Holdings;
                    var port = Port(a[0]);
                    if (h == null || port == null || !h.CanBuyAt(port)) return true;
                    var bought = h.Buy(port, (ShipClass)Wire.ParseI(a[1]), null, jd);
                    app.ShowToast(bought != null ? "Bought the " + bought.Name + " at " + port.Name : "Cannot afford it here");
                    return true;
                }
                case "warehouse":
                {
                    var h = world.Holdings;
                    var port = Port(a[0]);
                    if (h != null && port != null) app.ShowToast(h.BuyWarehouse(port, jd) != null ? "A warehouse at " + port.Name + " for " + Credits(Holdings.WarehousePrice) : "Not for sale");
                    return true;
                }
                case "share":
                {
                    var h = world.Holdings;
                    var port = Port(a[0]);
                    if (h != null && port != null) app.ShowToast(h.BuyShare(port, 0.05, jd) != null ? "5% of " + port.Name + " is yours" : "Not for sale");
                    return true;
                }
                case "claim":
                {
                    var h = world.Holdings;
                    int body = Wire.ParseI(a[0]);
                    if (h != null && body >= 0 && body < world.Catalog.Count)
                        app.ShowToast(h.StakeClaim(body, (Good)Wire.ParseI(a[1]), jd) != null ? "Claim staked on " + world.Catalog[body].Name : "Cannot stake that claim");
                    return true;
                }
                case "captain":
                {
                    var h = world.Holdings;
                    var o = h != null ? At(h.Ships, a[0]) : null;
                    if (o == null) return true;
                    if (o.Captain == null) app.ShowToast("Captain " + h.Hire(o, jd).Name + " takes the " + o.Name);
                    else h.Dismiss(o, jd);
                    return true;
                }
                case "orders":
                {
                    var h = world.Holdings;
                    var o = h != null ? At(h.Ships, a[0]) : null;
                    if (o == null || o.OnCharter) return true;
                    var next = NextOrders(o, app.CoopTarget(a.Length > 1 ? a[1].Replace('|', ' ') : "n"));
                    app.ShowToast(o.Name + ": " + next.Describe(world.Catalog));
                    return true;
                }
                // ---- the trade desk
                case "route":
                {
                    var h = world.Holdings;
                    var idle = h != null ? At(h.Ships, a[0]) : null;
                    if (idle == null) return true;
                    int from = Wire.ParseI(a[1]), to = Wire.ParseI(a[2]);
                    app.ShowToast(h.Order(idle, StandingOrder.TradeRoute(from, to))
                        ? "The " + idle.Name + " runs " + world.Catalog[from].Name + " - " + world.Catalog[to].Name
                        : "The " + idle.Name + " cannot take that route");
                    return true;
                }
                case "charter":
                {
                    var charters = world.Trade?.Charters;
                    var offer = charters != null ? At(charters.Offers, a[0]) : null;
                    var candidate = offer != null ? charters.Candidate(offer, jd) : null;
                    if (candidate != null)
                        app.ShowToast(charters.Take(offer, candidate, jd) != null ? "The " + candidate.Name + " goes out on charter to " + offer.Charterer.Name : "No longer on offer");
                    return true;
                }
                case "recall":
                {
                    var charters = world.Trade?.Charters;
                    var c = charters != null ? At(charters.Active, a[0]) : null;
                    if (c != null) app.ShowToast(charters.Recall(c, jd) ? "The " + c.Ship.Name + " is recalled" : "The account cannot pay the recall");
                    return true;
                }
                case "insure":
                case "uninsure":
                {
                    var ins = world.Trade?.Insurance;
                    var h = world.Holdings;
                    if (ins == null || h == null) return true;
                    bool ship = a[0] == "s";
                    var o = ship ? At(h.Ships, a[1]) : null;
                    var p = ship ? null : At(h.Properties, a[1]);
                    if (o == null && p == null) return true;
                    var policy = o != null ? ins.Of(o) : ins.Of(p);
                    string name = "The " + (o != null ? o.Name : p.Name);
                    if (verb == "uninsure") { if (policy != null) ins.Cancel(policy, jd); }
                    else if (policy == null) app.ShowToast((o != null ? ins.Insure(o, jd) : ins.Insure(p, jd)) != null ? name + " is insured" : "The underwriters will not write it");
                    return true;
                }
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- the original's actions, as their buttons do them

        /// <summary>The market board's Pay bounty (the original's call, moved here so a frame can carry it out too).</summary>
        void PayBounty(Settlement market)
        {
            var law = app.World.Law;
            app.ShowToast(law.PayFine(app.World.PilotedShip, market.Owner, app.Clock.Jd, out double paid)
                ? "Paid " + Credits(paid) + " to " + market.Owner.Name + ": the bounty is lifted"
                : "Not enough credits to pay off the bounty");
        }

        void HandOverPrisoners(Ship ship, Settlement port)
        {
            double paid = Outfitting.HandOverPrisoners(app.Economy, ship, port);
            app.ShowToast(paid > 0 ? "Prisoners handed over at " + port.Name + " for " + Credits(paid) : "Not welcome here");
        }

        void DeliverOutlaw(Outlaws outlaws, Outlaw o, Settlement port)
        {
            double paid = outlaws.Deliver(o, port, app.Clock.Jd);
            app.ShowToast(o.Fate == OutlawFate.Captured ? o.Name + " handed over at " + port.Name + (paid > 0 ? " for " + Credits(paid) : "") : "Not welcome here");
        }

        void RansomOutlaw(Outlaws outlaws, Outlaw o, Settlement port)
        {
            double paid = outlaws.Ransom(o, port, app.Clock.Jd);
            app.ShowToast(paid > 0 ? o.Name + " ransomed back for " + Credits(paid) : "The " + port.Owner.Name + " will not pay");
        }

        void TakeContract(Contracts contracts, Contract c, Ship ship, double jd)
        {
            var outcome = contracts.Take(c, ship, true, jd);
            app.ShowToast(outcome switch
            {
                Contracts.TakeOutcome.Ok => "Contract taken: " + c.Title + ", due in " + c.DeadlineDays.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " d",
                Contracts.TakeOutcome.NotOffered => c.Patron.Name + " offers you no contracts",
                Contracts.TakeOutcome.NoRoom => "No room aboard for that",
                Contracts.TakeOutcome.Unarmed => "That job wants an armed ship",
                Contracts.TakeOutcome.NotHere => "Fly alongside " + c.Board.Name + " to sign",
                _ => "No longer on offer",
            });
        }

        /// <summary>For the smoke run: what the market board's Buy button does for this good at this market.</summary>
        internal void CoopSmokeBuy(Settlement market, Good good) => ClickBuy(market, good);
    }
}
