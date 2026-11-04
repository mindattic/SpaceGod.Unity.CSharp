using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.UI
{
    public sealed partial class Hud
    {
        // ---------------------------------------------------------------- the trade desk (U.SPC-T426)

        enum TradeTab { Runs, News, Charters, Insurance, Warehouses }

        TradeTab tradeTab;
        Vector2 tradeScroll;

        /// <summary>The port the desk deals with: the selected world's biggest discovered port, else the one the piloted ship is alongside.</summary>
        Settlement TradePortInView()
        {
            Settlement port = null;
            var sel = app.Selected;
            if (sel.IsBody)
                foreach (var s in app.Economy.At(sel.Body))
                    if (s.Discovered && (port == null || s.Population > port.Population)) port = s;
            if (port == null && app.IsPiloting)
                foreach (var s in app.Economy.Settlements)
                    if (s.Discovered && app.NearPort(s)) return s;
            return port;
        }

        /// <summary>
        /// The trade desk (backslash): the trader's rank and what the house is worth, and five pages: the runs the player's price
        /// intelligence adds up to (with a broker's price sheet to buy at the port in view, and a house freighter to send
        /// on the best), the trade news, the trading companies' charter offers and the house's hulls out on charter, the
        /// underwriters' quotes and policies, and the house's warehouses with their stock to store or sell. Alongside a
        /// lawful port the back door shows too: what in the hold the open market will not take, and what it pays.
        /// </summary>
        void DrawTradeDesk()
        {
            var world = app.World;
            var desk = world.Trade;
            if (desk == null) return;
            var h = world.Holdings;
            double jd = app.Clock.Jd;
            var port = TradePortInView();
            var flown = world.PilotedShip;
            var purse = flown != null ? flown.Faction : h.House;

            const float w = 860, rowH = 20, listH = 300;
            bool backDoor = flown != null && port != null && app.NearPort(port) && Smuggling.HasBackDoor(port) && HasBackDoorCargo(desk, flown, port);
            float h0 = 112 + listH + (backDoor ? 26 + 3 * rowH : 0);
            var r = Panel(new Rect((Screen.width - w) * 0.5f, 100, w, h0));
            double worth = desk.NetWorth(purse != h.House ? purse : null);
            string next = double.IsNaN(desk.NextRankWorth) ? "" : "  ·  next rank at " + Credits(desk.NextRankWorth);
            GUI.Label(new Rect(r.x + 12, r.y + 6, w - 60, 22), "TRADE DESK  ·  " + TradeDesk.RankName(desk.Rank).ToUpperInvariant() + "  ·  worth " + Credits(worth) + next, title);
            if (GUI.Button(new Rect(r.xMax - 34, r.y + 6, 24, 22), "X", button)) app.ShowTrade = false;
            var old = GUI.contentColor;

            // The pages.
            float x = r.x + 12, y = r.y + 32;
            foreach (TradeTab t in Enum.GetValues(typeof(TradeTab)))
            {
                if (GUI.Button(new Rect(x, y, 118, 22), t.ToString(), t == tradeTab ? buttonActive : button)) tradeTab = t;
                x += 122;
            }
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x + 8, y + 2, r.xMax - x - 20, 18), "account " + Credits(h.Balance) + (purse != h.House ? "  ·  purse " + Credits(purse.Treasury) : "") + "   F12 to close", small);

            y += 28;
            var view = new Rect(r.x + 8, y, w - 16, listH);
            switch (tradeTab)
            {
                case TradeTab.Runs: DrawTradeRuns(desk, port, purse, view, rowH, jd); break;
                case TradeTab.News: DrawTradeNews(desk, view, rowH, jd); break;
                case TradeTab.Charters: DrawTradeCharters(desk, view, rowH, jd); break;
                case TradeTab.Insurance: DrawTradeInsurance(desk, view, rowH, jd); break;
                case TradeTab.Warehouses: DrawTradeWarehouses(desk, view, rowH, jd); break;
            }

            // Career totals.
            GUI.contentColor = Dim;
            float by = view.yMax + 6;
            GUI.Label(new Rect(r.x + 12, by, w - 24, 18),
                "Charters served " + desk.ChartersServed + " (" + Credits(desk.HireEarned) + " hire)  ·  premiums " + Credits(desk.PremiumsPaid) +
                ", claims " + Credits(desk.ClaimsPaid) + "  ·  back-door sales " + desk.Smuggled + " (" + Credits(desk.SmugglingTakings) + "), caught " + desk.TimesCaught +
                "  ·  price sheets bought " + desk.ReportsBought, small);

            if (backDoor) DrawBackDoor(desk, flown, port, new Rect(r.x + 12, by + 24, w - 24, 3 * rowH + 22), rowH, jd);
            GUI.contentColor = old;
        }

        static bool HasBackDoorCargo(TradeDesk desk, Ship ship, Settlement port)
        {
            foreach (var item in ship.PersonalHold.Items)
                if (desk.Smuggling.ReasonFor(ship, port, item) != BackDoorReason.None) return true;
            return false;
        }

        /// <summary>The runs the desk's prices add up to for the flown hold (or a house freighter's), with a price sheet to buy.</summary>
        void DrawTradeRuns(TradeDesk desk, Settlement port, Faction purse, Rect view, float rowH, double jd)
        {
            var intel = desk.Intel;
            var flown = app.World.PilotedShip;
            var crates = flown?.PersonalHold;
            double hold = crates != null ? 0 : Economy.CapacityOf(ShipClass.Freighter);
            var leads = intel.Leads(hold, jd, 14, flown?.Faction, crates);
            if (crates != null) foreach (var lead in leads) hold = Math.Max(hold, lead.Tonnes);

            float x = view.x + 4, y = view.y;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x, y, 520, rowH), "Prices held for " + intel.Count + " markets  ·  " + (crates != null ? "runs for your free hold (up to " + Tonnes(hold) + ")" : "runs for a hold of " + Tonnes(hold)) + " (prices half-trusted after " +
                TradeRules.IntelHalfLife.Value.ToString("0", CultureInfo.InvariantCulture) + " d)", small);
            if (port != null && intel.HasBroker(port))
            {
                GUI.contentColor = Color.white;
                if (GUI.Button(new Rect(view.xMax - 290, y, 286, rowH - 2), "Broker's price sheet at " + port.Name + "  " + Credits(intel.ReportFee), button))
                {
                    int n = intel.BuyReport(port, purse, jd);
                    app.ShowToast(n >= 0 ? "The broker at " + port.Name + " sells you the prices of " + n + " markets" : "Not enough credits for the broker");
                }
            }
            y += rowH + 2;
            if (leads.Count == 0)
            {
                GUI.contentColor = Dim;
                GUI.Label(new Rect(x, y, view.width - 8, rowH * 2), "No runs yet. Prices come from markets you have seen in port, your captains in port, and brokers.", small);
                return;
            }
            var idle = IdleFreighter();
            foreach (var lead in leads)
            {
                GUI.contentColor = lead.BackDoor ? new Color(1f, 0.72f, 0.4f) : lead.Risk >= 0.35 ? new Color(1f, 0.85f, 0.5f) : Color.white;
                string line = GoodInfo.Of(lead.Good).Name + ":  " + lead.From.Name + " " + lead.BuyPrice.ToString("N0", CultureInfo.InvariantCulture) +
                              "  ->  " + lead.To.Name + " " + lead.SellPrice.ToString("N0", CultureInfo.InvariantCulture) + "   " + Tonnes(lead.Tonnes) +
                              "  ·  " + Credits(lead.Profit) + "  ·  " + MarketIntel.AgeText(lead.AgeDays) +
                              (lead.Risk >= 0.05 ? "  ·  risk " + Percent(lead.Risk) : "") + (lead.BackDoor ? "  ·  back door" : "");
                GUI.Label(new Rect(x, y, view.width - 140, rowH), line, small);
                if (idle != null && !lead.BackDoor && GUI.Button(new Rect(view.xMax - 132, y, 128, rowH - 2), "Send " + idle.Name, button) &&
                    !CoopSends("route", K(app.World.Holdings.Ships, idle), lead.From.Body, lead.To.Body)) // US-T43: an entry in co-op
                    app.ShowToast(app.World.Holdings.Order(idle, StandingOrder.TradeRoute(lead.From.Body, lead.To.Body))
                        ? "The " + idle.Name + " runs " + lead.From.Name + " - " + lead.To.Name
                        : "The " + idle.Name + " cannot take that route");
                y += rowH;
                if (y > view.yMax - rowH) break;
            }
        }

        /// <summary>A crewed house hull with a hold, holding in port and not on charter: the one a run is offered to.</summary>
        OwnedShip IdleFreighter()
        {
            foreach (var o in app.World.Holdings.Ships)
                if (!o.Lost && o.Captain != null && !o.OnCharter && o.Orders.Kind == StandingOrderKind.Hold && o.Ship != app.World.PilotedShip &&
                    Economy.CapacityOf(o.Ship.Spec.Class) > 0)
                    return o;
            return null;
        }

        void DrawTradeNews(TradeDesk desk, Rect view, float rowH, double jd)
        {
            var tips = desk.Tips(jd, 14);
            float x = view.x + 4, y = view.y;
            if (tips.Count == 0)
            {
                GUI.contentColor = Dim;
                GUI.Label(new Rect(x, y, view.width - 8, rowH), "Quiet markets: no shocks, shortages or risks to what you own.", small);
                return;
            }
            foreach (var t in tips)
            {
                GUI.contentColor = t.Weight >= 4 ? new Color(1f, 0.72f, 0.4f) : Color.white;
                GUI.Label(new Rect(x, y, view.width - 8, rowH), Astro.ToDateTime(t.Jd).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "  " + t.Text, small);
                y += rowH;
            }
        }

        void DrawTradeCharters(TradeDesk desk, Rect view, float rowH, double jd)
        {
            var charters = desk.Charters;
            float x = view.x + 4, y = view.y;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x, y, view.width - 8, rowH), "OFFERS  (a hull goes with its crew, trades for the charterer and comes back to its orders; recalling it early costs " +
                Charters.RecallPenaltyDays.ToString("0", CultureInfo.InvariantCulture) + " days' hire)", small);
            y += rowH;
            if (charters.Offers.Count == 0)
            {
                GUI.Label(new Rect(x, y, view.width - 8, rowH), "No company is chartering just now.", small);
                y += rowH;
            }
            foreach (var offer in new List<CharterOffer>(charters.Offers))
            {
                var candidate = charters.Candidate(offer, jd);
                GUI.contentColor = Color.white;
                GUI.Label(new Rect(x, y, view.width - 200, rowH), offer.Title + "  ·  " + Credits(offer.Total) + " in all  ·  up " +
                    Math.Max(0, offer.ExpiresJd - jd).ToString("0", CultureInfo.InvariantCulture) + " d more", small);
                if (candidate != null && GUI.Button(new Rect(view.xMax - 192, y, 188, rowH - 2), "Charter the " + candidate.Name, button) &&
                    !CoopSends("charter", K(charters.Offers, offer))) // US-T43: an entry in co-op
                    app.ShowToast(charters.Take(offer, candidate, jd) != null ? "The " + candidate.Name + " goes out on charter to " + offer.Charterer.Name : "No longer on offer");
                y += rowH;
            }
            y += 4;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x, y, view.width - 8, rowH), "OUT ON CHARTER", small);
            y += rowH;
            foreach (var c in new List<Charter>(charters.Active))
            {
                GUI.contentColor = new Color(0.6f, 0.9f, 1f);
                GUI.Label(new Rect(x, y, view.width - 120, rowH), "The " + c.Ship.Name + " to " + c.Charterer.Name + "  ·  " + Credits(c.RatePerDay) + " a day  ·  " +
                    c.DaysLeft(jd).ToString("0", CultureInfo.InvariantCulture) + " d left  ·  hire paid " + Credits(c.HirePaid) + "  ·  " + c.Ship.Ship.Cargo, small);
                if (GUI.Button(new Rect(view.xMax - 112, y, 108, rowH - 2), "Recall", button) && !CoopSends("recall", K(charters.Active, c))) // US-T43
                    app.ShowToast(charters.Recall(c, jd) ? "The " + c.Ship.Name + " is recalled" : "The account cannot pay the recall");
                y += rowH;
                if (y > view.yMax - rowH) return;
            }
        }

        void DrawTradeInsurance(TradeDesk desk, Rect view, float rowH, double jd)
        {
            var ins = desk.Insurance;
            var h = app.World.Holdings;
            float x = view.x + 4, y = view.y;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x, y, view.width - 8, rowH), "Underwriters pay " + Percent(TradeRules.InsuranceCover.Value) + " of a hull lost, or a warehouse seized, for a premium a day that climbs with the risk", small);
            y += rowH;
            void Row(string name, InsurancePolicy policy, (double cover, double premium, double risk) q, Func<InsurancePolicy> insure, string coopKey) // US-T43: coopKey, what it insures for a co-op entry
            {
                GUI.contentColor = policy != null ? new Color(0.55f, 1f, 0.6f) : q.risk >= TradeInsurance.Uninsurable ? new Color(1f, 0.55f, 0.45f) : Color.white;
                string state = policy != null ? "insured for " + Credits(policy.Cover) + " at " + Credits(policy.PremiumPerDay) + " a day"
                    : q.risk >= TradeInsurance.Uninsurable ? "uninsurable here (risk " + Percent(q.risk) + ")"
                    : "quote: " + Credits(q.cover) + " for " + Credits(q.premium) + " a day (risk " + Percent(q.risk) + ")";
                GUI.Label(new Rect(x, y, view.width - 120, rowH), name + "  ·  " + state, small);
                if (policy != null)
                {
                    if (GUI.Button(new Rect(view.xMax - 112, y, 108, rowH - 2), "Cancel", button) && !CoopSends("uninsure", coopKey)) ins.Cancel(policy, jd); // US-T43
                }
                else if (q.risk < TradeInsurance.Uninsurable && GUI.Button(new Rect(view.xMax - 112, y, 108, rowH - 2), "Insure", button) && !CoopSends("insure", coopKey)) // US-T43
                    app.ShowToast(insure() != null ? name + " is insured" : "The underwriters will not write it");
                y += rowH;
            }
            foreach (var o in h.Ships)
            {
                if (o.Lost || y > view.yMax - rowH) continue;
                var owned = o;
                Row("The " + o.Name, ins.Of(o), ins.Quote(o), () => ins.Insure(owned, jd), "s " + K(h.Ships, o)); // US-T43: the key
            }
            foreach (var p in h.Properties)
            {
                if (p.Lost || p.Kind != PropertyKind.Warehouse || y > view.yMax - rowH) continue;
                var prop = p;
                Row("The " + p.Name, ins.Of(p), ins.Quote(p), () => ins.Insure(prop, jd), "p " + K(h.Properties, p)); // US-T43: the key
            }
            if (y == view.y + rowH)
            {
                GUI.contentColor = Dim;
                GUI.Label(new Rect(x, y, view.width - 8, rowH), "Nothing to insure yet: buy a hull or a warehouse (F10).", small);
            }
        }

        void DrawTradeWarehouses(TradeDesk desk, Rect view, float rowH, double jd)
        {
            var h = app.World.Holdings;
            var houses = new List<Property>();
            foreach (var p in h.OfKind(PropertyKind.Warehouse))
                if (!p.Lost) houses.Add(p);
            float x = view.x + 4;
            if (houses.Count == 0)
            {
                GUI.contentColor = Dim;
                GUI.Label(new Rect(x, view.y, view.width - 8, rowH), "No warehouses yet: buy one at a port (F10). Store goods where they are cheap and sell them when the price is up.", small);
                return;
            }
            int lines = 0;
            foreach (var p in houses) lines += 1 + GoodInfo.Count;
            var content = new Rect(0, 0, view.width - 20, lines * rowH);
            tradeScroll = GUI.BeginScrollView(view, tradeScroll, content);
            float y = 0;
            foreach (var p in houses)
            {
                var s = p.Settlement;
                var risk = desk.Assess(s);
                GUI.contentColor = risk.Total >= 0.35 ? new Color(1f, 0.72f, 0.4f) : Accent;
                GUI.Label(new Rect(4, y, content.width - 8, rowH), p.Name + "  ·  " + Tonnes(p.StoredTonnes) + " of " + Tonnes(p.CapacityTonnes) + "  ·  " + risk.Summary +
                    (desk.Insurance.Of(p) != null ? "  ·  insured" : "  ·  uninsured"), small);
                y += rowH;
                for (int g = 0; g < GoodInfo.Count; g++)
                {
                    var good = (Good)g;
                    double ratio = s.Price[g] / GoodInfo.Of(good).BasePrice;
                    GUI.contentColor = ratio > 1.5 ? new Color(1f, 0.7f, 0.5f) : ratio < 0.7 ? new Color(0.55f, 0.85f, 1f) : Color.white;
                    GUI.Label(new Rect(16, y, 380, rowH), GoodInfo.Of(good).Name + "  ·  stored " + Tonnes(p.Stock[g]) + "  ·  " +
                        s.Price[g].ToString("N0", CultureInfo.InvariantCulture) + " a t (x" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ")", small);
                    if (GUI.Button(new Rect(content.width - 220, y, 104, rowH - 2), "Store 1 kt", button))
                    {
                        double t = h.StoreFromMarket(p, good, 1000, jd);
                        app.ShowToast(t > 0 ? "Stored " + Tonnes(t) + " in the " + p.Name : s.Name + " has none to spare (or the warehouse is full)");
                    }
                    if (p.Stock[g] > 0.01 && GUI.Button(new Rect(content.width - 110, y, 104, rowH - 2), "Sell all", button))
                    {
                        double cr = h.SellFromStore(p, good, p.Stock[g], jd);
                        app.ShowToast(cr > 0 ? "Sold out of the " + p.Name + " for " + Credits(cr) : s.Name + " does not buy it");
                    }
                    y += rowH;
                }
            }
            GUI.EndScrollView();
        }

        /// <summary>What in the flown hold the open market here will not take, what the back door pays, and customs' odds.</summary>
        void DrawBackDoor(TradeDesk desk, Ship ship, Settlement port, Rect r, float rowH, double jd)
        {
            var sm = desk.Smuggling;
            GUI.contentColor = new Color(1f, 0.72f, 0.4f);
            GUI.Label(new Rect(r.x, r.y, r.width, rowH), "BACK DOOR  ·  " + port.Name + "  (off the books: no port tax, but customs may be waiting)", small);
            float y = r.y + rowH + 2;
            var seen = new HashSet<Good>();
            int rows = 0;
            foreach (var item in new List<CargoItem>(ship.PersonalHold.Items))
            {
                if (rows >= 3 || !seen.Add(item.Good)) continue;
                var reason = sm.ReasonFor(ship, port, item.Good);
                if (reason == BackDoorReason.None) continue;
                int crates = 0;
                foreach (var it in ship.PersonalHold.Items)
                    if (it.Good == item.Good && sm.ReasonFor(ship, port, it) != BackDoorReason.None) crates++;
                var good = item.Good;
                GUI.contentColor = Color.white;
                GUI.Label(new Rect(r.x, y, r.width - 120, rowH), crates + " crate" + (crates == 1 ? "" : "s") + " of " + GoodInfo.Of(good).Name.ToLowerInvariant() +
                    " (" + Smuggling.Describe(reason) + ")  ·  pays " + Smuggling.PriceFor(port, good, reason).ToString("N0", CultureInfo.InvariantCulture) +
                    " a t  ·  customs " + Percent(sm.CatchChance(port, reason)), small);
                if (GUI.Button(new Rect(r.xMax - 112, y, 108, rowH - 2), "Sell " + Math.Min(crates, tradeCrates), button))
                {
                    var outcome = sm.SellBackDoor(ship, port, good, Math.Min(crates, tradeCrates), jd, out int sold, out double credits);
                    app.ShowToast(outcome switch
                    {
                        Smuggling.Outcome.Sold => "Sold " + sold + " crate" + (sold == 1 ? "" : "s") + " out the back door for " + Credits(credits),
                        Smuggling.Outcome.Caught => "Customs! The crates are seized and you are charged with smuggling",
                        _ => "Nothing here for the back door",
                    });
                }
                y += rowH;
                rows++;
            }
        }
    }
}
