using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Covers U.SPC-T33: the AI trader logistics in <see cref="Economy"/> -- <see cref="Economy.PlanTrade"/>,
    /// <see cref="Economy.OnArrive"/>, <see cref="Economy.Release"/>, <see cref="Economy.RecountInbound"/>,
    /// <see cref="Economy.Carries"/>, <see cref="Economy.CapacityOf"/> and <see cref="Economy.CargoText"/>.
    /// Until now these were only exercised indirectly, over years of simulated time, by
    /// <see cref="EconomyTests.TwoYearsOfTradeKeepEveryoneFedAndTradersInProfit"/> and
    /// <see cref="EconomyTests.TradersOnlyDockWhereTheyAreWelcome"/>.
    /// </summary>
    [TestFixture]
    public class TradeLogisticsTests
    {
        static Faction MakeFaction(string name, double treasury = 1_000_000_000, bool pirate = false) =>
            new Faction(name, name.Substring(0, Math.Min(3, name.Length)).ToUpperInvariant(), default)
            {
                Treasury = treasury,
                IsPirate = pirate,
            };

        static Settlement MakePort(Economy economy, string name, int body, Faction owner) =>
            economy.Add(name, body, SettlementKind.Station, owner, population: 0);

        /// <summary>Sets a good's market state directly (bypassing production) and refreshes its price.</summary>
        static void SetGood(Settlement s, Good g, double stock = 0, double demand = 0, double inbound = 0)
        {
            s.Stock[(int)g] = stock;
            s.Demand[(int)g] = demand;
            s.Inbound[(int)g] = inbound;
            s.RefreshPrices();
        }

        static Ship MakeShip(Faction faction, ShipClass cls) =>
            new Ship { Spec = ShipClassSpec.Of(cls), Faction = faction, CapacityTonnes = Economy.CapacityOf(cls) };

        static Ship LoadedShip(Faction faction, ShipClass cls, Good good, double tonnes, double cost, Settlement destination = null, double bonus = 0) =>
            new Ship
            {
                Spec = ShipClassSpec.Of(cls),
                Faction = faction,
                CapacityTonnes = Economy.CapacityOf(cls),
                HasCargo = true,
                CargoGood = good,
                CargoTonnes = tonnes,
                CargoCost = cost,
                CargoDestination = destination,
                CargoBonus = bonus,
            };

        // ---------------------------------------------------------------- Carries

        [TestCase(Good.NickelIron, true)]
        [TestCase(Good.Copper, true)]
        [TestCase(Good.Platinum, true)]
        [TestCase(Good.Water, true)]
        [TestCase(Good.Nitrogen, true)]
        [TestCase(Good.Hydrocarbons, true)]
        [TestCase(Good.HeliumThree, false)] // volatile, but reserved for couriers, not miners
        [TestCase(Good.Food, false)]
        [TestCase(Good.Electronics, false)]
        [TestCase(Good.Munitions, false)]
        public void CarriesForAMinerIsMineralsPlusWaterNitrogenAndHydrocarbonsOnly(Good good, bool expected) =>
            Assert.That(Economy.Carries(ShipClass.Miner, good), Is.EqualTo(expected));

        [TestCase(Good.MedicalSupplies, true)]
        [TestCase(Good.Electronics, true)]
        [TestCase(Good.Platinum, true)]
        [TestCase(Good.RareEarths, true)]
        [TestCase(Good.HeliumThree, true)]
        [TestCase(Good.NickelIron, false)]
        [TestCase(Good.Food, false)]
        [TestCase(Good.Water, false)]
        [TestCase(Good.Munitions, false)]
        [TestCase(Good.Machinery, false)]
        public void CarriesForACourierIsOnlyTheNamedHighValueGoods(Good good, bool expected) =>
            Assert.That(Economy.Carries(ShipClass.Courier, good), Is.EqualTo(expected));

        [TestCase(Good.Food)]
        [TestCase(Good.NickelIron)]
        [TestCase(Good.Electronics)]
        [TestCase(Good.Munitions)]
        public void CarriesForAFreighterIsTrueForEveryGood(Good good) =>
            Assert.That(Economy.Carries(ShipClass.Freighter, good), Is.True);

        [TestCase(ShipClass.Carrier)]
        [TestCase(ShipClass.Battleship)]
        [TestCase(ShipClass.Cruiser)]
        [TestCase(ShipClass.Destroyer)]
        [TestCase(ShipClass.Gunship)]
        [TestCase(ShipClass.Fighter)]
        public void CarriesIsFalseForEveryGoodOnWarshipsAndCombatCraft(ShipClass cls) =>
            Expect.All(Enum.GetValues(typeof(Good)).Cast<Good>(), g => Assert.That(Economy.Carries(cls, g), Is.False));

        // ---------------------------------------------------------------- CapacityOf

        [TestCase(ShipClass.Freighter, 20000)]
        [TestCase(ShipClass.Miner, 30000)]
        [TestCase(ShipClass.Courier, 400)]
        [TestCase(ShipClass.Carrier, 0)]
        [TestCase(ShipClass.Battleship, 0)]
        [TestCase(ShipClass.Fighter, 0)]
        public void CapacityOfReturnsEachHullsHoldSize(ShipClass cls, double expected) =>
            Assert.That(Economy.CapacityOf(cls), Is.EqualTo(expected));

        // ---------------------------------------------------------------- CargoText

        [Test]
        public void CargoTextIsNullWhenTheShipIsNotCarryingAnything()
        {
            var ship = new Ship { HasCargo = false };
            Assert.That(Economy.CargoText(ship), Is.Null);
        }

        [Test]
        public void CargoTextNamesTheGoodTonnageAndDestinationWhenBooked()
        {
            var destination = new Settlement { Name = "Ceres Station" };
            var ship = new Ship { HasCargo = true, CargoGood = Good.Electronics, CargoTonnes = 2500, CargoDestination = destination };

            Assert.That(Economy.CargoText(ship), Is.EqualTo("2,500 t Electronics for Ceres Station"));
        }

        [Test]
        public void CargoTextOmitsTheDestinationWhenNoneIsBooked()
        {
            var ship = new Ship { HasCargo = true, CargoGood = Good.Electronics, CargoTonnes = 2500, CargoDestination = null };

            Assert.That(Economy.CargoText(ship), Is.EqualTo("2,500 t Electronics"));
        }

        // ---------------------------------------------------------------- Release

        [Test]
        public void ReleaseReducesTheBookedDestinationsInboundByTheCargoTonnes()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var destination = MakePort(economy, "Dest", 1, MakeFaction("Owner"));
            destination.Inbound[(int)Good.Food] = 800;
            var ship = new Ship { HasCargo = true, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = destination };

            economy.Release(ship);

            Assert.That(destination.Inbound[(int)Good.Food], Is.EqualTo(300).Within(1e-6));
        }

        [Test]
        public void ReleaseClampsToZeroRatherThanGoingNegative()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var destination = MakePort(economy, "Dest", 1, MakeFaction("Owner"));
            destination.Inbound[(int)Good.Food] = 200;
            var ship = new Ship { HasCargo = true, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = destination };

            economy.Release(ship);

            Assert.That(destination.Inbound[(int)Good.Food], Is.EqualTo(0));
        }

        [Test]
        public void ReleaseIsANoOpWhenTheShipIsNotActuallyCarryingCargo()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var destination = MakePort(economy, "Dest", 1, MakeFaction("Owner"));
            destination.Inbound[(int)Good.Food] = 800;
            var ship = new Ship { HasCargo = false, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = destination };

            economy.Release(ship);

            Assert.That(destination.Inbound[(int)Good.Food], Is.EqualTo(800));
        }

        [Test]
        public void ReleaseIsANoOpWhenNothingIsBooked()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var ship = new Ship { HasCargo = true, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = null };

            Assert.DoesNotThrow(() => economy.Release(ship));
        }

        // ---------------------------------------------------------------- RecountInbound

        [Test]
        public void RecountInboundClearsStaleBookingsThatNoShipStillCarries()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var destination = MakePort(economy, "Dest", 1, MakeFaction("Owner"));
            destination.Inbound[(int)Good.Food] = 999;

            economy.RecountInbound(Array.Empty<Ship>());

            Assert.That(destination.Inbound[(int)Good.Food], Is.EqualTo(0));
        }

        [Test]
        public void RecountInboundSumsTonnesFromEveryShipBoundForTheSameGoodAndDestination()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var faction = MakeFaction("Trader");
            var destination = MakePort(economy, "Dest", 1, MakeFaction("Owner"));
            var shipA = new Ship { Faction = faction, HasCargo = true, CargoGood = Good.Food, CargoTonnes = 300, CargoDestination = destination };
            var shipB = new Ship { Faction = faction, HasCargo = true, CargoGood = Good.Food, CargoTonnes = 450, CargoDestination = destination };

            economy.RecountInbound(new[] { shipA, shipB });

            Assert.That(destination.Inbound[(int)Good.Food], Is.EqualTo(750).Within(1e-6));
        }

        [Test]
        public void RecountInboundIgnoresDestroyedShipsAndShipsWithoutABookedDestination()
        {
            var economy = new Economy(new Diplomacy(), startJd: 0);
            var faction = MakeFaction("Trader");
            var destination = MakePort(economy, "Dest", 1, MakeFaction("Owner"));
            var destroyed = new Ship { Faction = faction, HasCargo = true, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = destination, Destroyed = true };
            var noCargo = new Ship { Faction = faction, HasCargo = false, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = destination };
            var noDestination = new Ship { Faction = faction, HasCargo = true, CargoGood = Good.Food, CargoTonnes = 500, CargoDestination = null };

            economy.RecountInbound(new[] { destroyed, noCargo, noDestination });

            Assert.That(destination.Inbound[(int)Good.Food], Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- PlanTrade

        [Test]
        public void PlanTradeOnlyLoadsGoodsTheHullIsFittedToCarry()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var origin = MakePort(economy, "Origin", 1, owner);
            var destination = MakePort(economy, "Destination", 2, owner);
            // Electronics is worth far more than nickel-iron ore, so an unfiltered trader would always
            // prefer it; a Miner can only carry the ore (Economy.Carries), proving the filter is applied.
            SetGood(origin, Good.NickelIron, stock: 1_000_000, demand: 0);
            SetGood(origin, Good.Electronics, stock: 1_000_000, demand: 0);
            SetGood(destination, Good.NickelIron, demand: 5000);
            SetGood(destination, Good.Electronics, demand: 5000);
            var miner = MakeShip(MakeFaction("Trader"), ShipClass.Miner);

            int to = economy.PlanTrade(miner, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(2));
            Assert.That(miner.HasCargo, Is.True);
            Assert.That(miner.CargoGood, Is.EqualTo(Good.NickelIron));
        }

        [Test]
        public void PlanTradeSkipsOriginPortsDiplomacyForbidsEvenWhenNothingElseIsProfitable()
        {
            var diplomacy = new Diplomacy();
            var economy = new Economy(diplomacy, startJd: 1000);
            var haven = MakePort(economy, "Haven", 1, MakeFaction("Raiders", pirate: true));
            var buyer = MakePort(economy, "Buyer", 2, MakeFaction("Market"));
            SetGood(haven, Good.NickelIron, stock: 1_000_000, demand: 0);
            SetGood(buyer, Good.NickelIron, demand: 5000);
            var freighter = MakeShip(MakeFaction("LawfulTrader"), ShipClass.Freighter);

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(-1));
            Assert.That(freighter.HasCargo, Is.False);
        }

        [Test]
        public void PlanTradeSkipsDestinationsDiplomacyForbidsEvenWhenTheyWouldPayBest()
        {
            var diplomacy = new Diplomacy();
            var economy = new Economy(diplomacy, startJd: 1000);
            var trader = MakeFaction("Trader");
            var enemyOwner = MakeFaction("Enemy");
            diplomacy.Set(trader, enemyOwner, Relation.War);
            var origin = MakePort(economy, "Origin", 1, MakeFaction("Dock"));
            var forbiddenDestination = MakePort(economy, "EnemyMarket", 2, enemyOwner);
            var allowedDestination = MakePort(economy, "FriendlyMarket", 3, MakeFaction("Friend"));
            SetGood(origin, Good.NickelIron, stock: 1_000_000, demand: 0);
            // The forbidden market is starved (far scarcer, so it would pay much more) -- it must still be skipped.
            SetGood(forbiddenDestination, Good.NickelIron, stock: 0, demand: 5000);
            SetGood(allowedDestination, Good.NickelIron, stock: 90000, demand: 5000);
            var freighter = MakeShip(trader, ShipClass.Freighter);

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(3));
            Assert.That(freighter.CargoDestination, Is.EqualTo(allowedDestination));
        }

        [Test]
        public void PlanTradeBooksInboundAtTheChosenDestinationAndLoadsTheShip()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var origin = MakePort(economy, "Origin", 1, owner);
            var destination = MakePort(economy, "Destination", 2, owner);
            SetGood(origin, Good.NickelIron, stock: 1_000_000, demand: 0);
            SetGood(destination, Good.NickelIron, demand: 5000);
            var freighter = MakeShip(MakeFaction("Trader"), ShipClass.Freighter);
            double inboundBefore = destination.Inbound[(int)Good.NickelIron];

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(2));
            Assert.That(freighter.HasCargo, Is.True);
            Assert.That(freighter.CargoGood, Is.EqualTo(Good.NickelIron));
            Assert.That(freighter.CargoTonnes > 0, Is.True);
            Assert.That(freighter.CargoDestination, Is.EqualTo(destination));
            Assert.That(destination.Inbound[(int)Good.NickelIron], Is.EqualTo(inboundBefore + freighter.CargoTonnes).Within(1e-6));
            Assert.That(freighter.Cargo, Is.EqualTo(Economy.CargoText(freighter)));
            Assert.That(freighter.CargoCost > 0, Is.True);
        }

        [Test]
        public void PlanTradeRepositionsEmptyToWhereTheTradesAreWhenNothingPaysHere()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var empty = MakePort(economy, "Empty", 1, owner);       // nothing to sell here
            var supplier = MakePort(economy, "Supplier", 2, owner); // has the goods
            var buyer = MakePort(economy, "Buyer", 3, owner);       // wants them
            SetGood(supplier, Good.NickelIron, stock: 1_000_000, demand: 0);
            SetGood(buyer, Good.NickelIron, demand: 5000);
            var freighter = MakeShip(MakeFaction("Trader"), ShipClass.Freighter);

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(2)); // reposition to the supplier, not straight to the buyer
            Assert.That(freighter.HasCargo, Is.False);
            Assert.That(freighter.Cargo, Is.EqualTo("Empty, repositioning to " + supplier.Name));
        }

        [Test]
        public void PlanTradeReturnsNegativeOneWhenNothingIsWorthDoingAnywhere()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            MakePort(economy, "Origin", 1, owner); // no stock, no demand anywhere
            MakePort(economy, "Other", 2, owner);
            var freighter = MakeShip(MakeFaction("Trader"), ShipClass.Freighter);

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(-1));
            Assert.That(freighter.HasCargo, Is.False);
            Assert.That(freighter.Cargo, Is.Null);
        }

        [Test]
        public void PlanTradeDiscountsDestinationsByDangerAndCanExcludeAnUnsafeOneEntirely()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var origin = MakePort(economy, "Origin", 1, owner);
            var dangerous = MakePort(economy, "Dangerous", 2, owner);
            var safe = MakePort(economy, "Safe", 3, owner);
            SetGood(origin, Good.NickelIron, stock: 1_000_000, demand: 0);
            // Identical trades at both destinations: only the danger discount should tell them apart.
            SetGood(dangerous, Good.NickelIron, stock: 90000, demand: 5000);
            SetGood(safe, Good.NickelIron, stock: 90000, demand: 5000);
            economy.Danger = body => body == dangerous.Body ? 0.9 : 0;
            var freighter = MakeShip(MakeFaction("Trader"), ShipClass.Freighter);

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(3));
            Assert.That(freighter.CargoDestination, Is.EqualTo(safe));
        }

        [Test]
        public void PlanTradePaysAnEssentialShortageContractBonusAtTheDestination()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var origin = MakePort(economy, "Origin", 1, MakeFaction("Owner"));
            var destOwner = MakeFaction("DestOwner", treasury: 10_000_000);
            var destination = MakePort(economy, "Destination", 2, destOwner);
            SetGood(origin, Good.Food, stock: 1_000_000, demand: 0);
            SetGood(destination, Good.Food, stock: 0, demand: 1000); // well under 70% of target: a shortage
            var freighter = MakeShip(MakeFaction("Trader"), ShipClass.Freighter);

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(2));
            double expectedBonus = 600000 + freighter.CargoTonnes * GoodInfo.Of(Good.Food).BasePrice * 1.5;
            Assert.That(freighter.CargoBonus, Is.EqualTo(expectedBonus).Within(1e-2));
        }

        [Test]
        public void PlanTradeWithCargoAlreadyLoadedReroutesToWhereItSellsBestAndRebooksInbound()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var oldDestination = MakePort(economy, "OldDestination", 1, owner); // nobody here wants it anymore
            var worse = MakePort(economy, "Worse", 2, owner);
            var better = MakePort(economy, "Better", 3, owner);
            SetGood(worse, Good.NickelIron, stock: 1_000_000, demand: 100);   // glutted: floor price
            SetGood(better, Good.NickelIron, stock: 0, demand: 50000);       // scarce: high price
            var freighter = MakeShip(MakeFaction("Trader"), ShipClass.Freighter);
            freighter.HasCargo = true;
            freighter.CargoGood = Good.NickelIron;
            freighter.CargoTonnes = 200;
            freighter.CargoCost = 10000;
            freighter.CargoBonus = 777; // stale bonus from the previous leg -- must be cleared, not carried forward
            freighter.CargoDestination = oldDestination;
            oldDestination.Inbound[(int)Good.NickelIron] = 200; // booked from before

            int to = economy.PlanTrade(freighter, here: 1, jd: 1000);

            Assert.That(to, Is.EqualTo(3)); // the scarcer, higher-paying market
            Assert.That(freighter.CargoDestination, Is.EqualTo(better));
            Assert.That(oldDestination.Inbound[(int)Good.NickelIron], Is.EqualTo(0).Within(1e-6)); // released
            Assert.That(better.Inbound[(int)Good.NickelIron], Is.EqualTo(200).Within(1e-6));      // rebooked
            Assert.That(freighter.CargoBonus, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- OnArrive

        [Test]
        public void OnArriveSellsAtTheHighestWillingMarketAtTheBody()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var cheapBuyer = MakePort(economy, "Cheap", 5, owner);
            var richBuyer = MakePort(economy, "Rich", 5, owner);
            SetGood(cheapBuyer, Good.NickelIron, stock: 95000, demand: 5000); // near target: modest price
            SetGood(richBuyer, Good.NickelIron, stock: 0, demand: 50000);     // scarce: high price
            var ship = LoadedShip(MakeFaction("Trader"), ShipClass.Freighter, Good.NickelIron, tonnes: 200, cost: 100);

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(ship.HasCargo, Is.False);
            Assert.That(richBuyer.Stock[(int)Good.NickelIron], Is.EqualTo(200).Within(1e-6));
            Assert.That(cheapBuyer.Stock[(int)Good.NickelIron], Is.EqualTo(95000).Within(1e-6));
        }

        [Test]
        public void OnArrivePrefersTheBookedDestinationWithinA5PercentMargin()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var booked = MakePort(economy, "Booked", 5, owner);
            var other = MakePort(economy, "Other", 5, owner);
            SetGood(booked, Good.Food, stock: 4000, demand: 1000);
            SetGood(other, Good.Food, stock: 3800, demand: 1000); // pays a little more raw, but within 5%
            const double tonnes = 100;
            double bookedRaw = booked.SaleValue(Good.Food, tonnes);
            double otherRaw = other.SaleValue(Good.Food, tonnes);
            Assert.That(otherRaw > bookedRaw && otherRaw < bookedRaw * 1.05, Is.True, "setup must keep the raw gap inside the 5% preference band");
            var ship = LoadedShip(MakeFaction("Trader"), ShipClass.Freighter, Good.Food, tonnes, cost: 100, booked);

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(booked.Stock[(int)Good.Food] - 4000, Is.EqualTo(tonnes).Within(1e-6));
            Assert.That(other.Stock[(int)Good.Food] - 3800, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void OnArriveLosesTheBookedDestinationWhenAnotherMarketPaysMoreThan5PercentBetter()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var booked = MakePort(economy, "Booked", 5, owner);
            var other = MakePort(economy, "Other", 5, owner);
            SetGood(booked, Good.Food, stock: 4000, demand: 1000);
            SetGood(other, Good.Food, stock: 500, demand: 1000); // much scarcer: far more than 5% better
            const double tonnes = 100;
            double bookedRaw = booked.SaleValue(Good.Food, tonnes);
            double otherRaw = other.SaleValue(Good.Food, tonnes);
            Assert.That(otherRaw > bookedRaw * 1.05, Is.True, "setup must put the raw gap outside the 5% preference band");
            var ship = LoadedShip(MakeFaction("Trader"), ShipClass.Freighter, Good.Food, tonnes, cost: 100, booked);

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(other.Stock[(int)Good.Food] - 500, Is.EqualTo(tonnes).Within(1e-6));
            Assert.That(booked.Stock[(int)Good.Food] - 4000, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void OnArriveAppliesPortTaxToTheSettlementOwnerOnSale()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var portOwner = MakeFaction("PortOwner");
            var traderFaction = MakeFaction("Trader");
            var buyer = MakePort(economy, "Buyer", 5, portOwner);
            SetGood(buyer, Good.NickelIron, stock: 1000, demand: 500);
            const double tonnes = 200;
            double expectedValue = buyer.SaleValue(Good.NickelIron, tonnes);
            double expectedTax = expectedValue * Economy.PortTax;
            double ownerTreasuryBefore = portOwner.Treasury;
            var ship = LoadedShip(traderFaction, ShipClass.Freighter, Good.NickelIron, tonnes, cost: 100);

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(portOwner.Treasury, Is.EqualTo(ownerTreasuryBefore + expectedTax).Within(1e-4));
        }

        [Test]
        public void OnArriveSettlesBonusCreditsAndTreasuryAndClearsCargoWhenSellingAtTheBookedDestination()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var portOwner = MakeFaction("PortOwner", treasury: 10_000_000);
            var traderFaction = MakeFaction("Trader");
            var destination = MakePort(economy, "Dest", 5, portOwner);
            SetGood(destination, Good.Food, stock: 4000, demand: 1000);
            const double tonnes = 500, cargoCost = 20000, bonus = 50000;
            var ship = LoadedShip(traderFaction, ShipClass.Freighter, Good.Food, tonnes, cargoCost, destination, bonus);
            double rawValue = destination.SaleValue(Good.Food, tonnes);
            double bestValue = rawValue; // the booked-destination preference ranks markets; it isn't paid out
            double tax = bestValue * Economy.PortTax;
            double finalValue = bestValue + bonus;
            double expectedProfit = finalValue - tax - cargoCost;
            double stockBefore = destination.Stock[(int)Good.Food];
            double ownerTreasuryBefore = portOwner.Treasury;
            double traderTreasuryBefore = traderFaction.Treasury;

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(destination.Stock[(int)Good.Food], Is.EqualTo(stockBefore + tonnes).Within(1e-6));
            Assert.That(portOwner.Treasury, Is.EqualTo(ownerTreasuryBefore + tax - bonus).Within(1e-4));
            Assert.That(traderFaction.Treasury, Is.EqualTo(traderTreasuryBefore + finalValue - tax).Within(1e-4));
            Assert.That(ship.Credits, Is.EqualTo(expectedProfit).Within(1e-4));
            Assert.That(ship.HasCargo, Is.False);
            Assert.That(ship.CargoTonnes, Is.EqualTo(0));
            Assert.That(ship.CargoCost, Is.EqualTo(0));
            Assert.That(ship.CargoBonus, Is.EqualTo(0));
            Assert.That(ship.CargoDestination, Is.Null);
            Assert.That(ship.Cargo, Is.Null);
        }

        [Test]
        public void OnArriveCarriesTheCargoOnWhenNobodyAtTheBodyWillBuyButReleasesTheOldBooking()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var owner = MakeFaction("Owner");
            var bookedDestination = MakePort(economy, "Booked", 9, owner);
            SetGood(bookedDestination, Good.Food, demand: 1000); // wants it, but the ship isn't there
            var here = MakePort(economy, "NoBuyersHere", 5, owner);
            SetGood(here, Good.Food, demand: 0); // nobody here wants Food
            var ship = LoadedShip(MakeFaction("Trader"), ShipClass.Freighter, Good.Food, tonnes: 500, cost: 1000, bookedDestination);
            bookedDestination.Inbound[(int)Good.Food] = 500;

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(ship.HasCargo, Is.True);
            Assert.That(ship.CargoTonnes, Is.EqualTo(500));
            Assert.That(ship.CargoDestination, Is.Null);
            Assert.That(bookedDestination.Inbound[(int)Good.Food], Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void OnArriveSkipsAPortDiplomacyForbidsEvenIfItWouldPayBest()
        {
            var diplomacy = new Diplomacy();
            var economy = new Economy(diplomacy, startJd: 1000);
            var traderFaction = MakeFaction("Trader");
            var enemyOwner = MakeFaction("Enemy");
            diplomacy.Set(traderFaction, enemyOwner, Relation.War);
            var forbidden = MakePort(economy, "Forbidden", 5, enemyOwner);
            var allowed = MakePort(economy, "Allowed", 5, MakeFaction("Friend"));
            SetGood(forbidden, Good.NickelIron, stock: 0, demand: 50000);     // would pay far more
            SetGood(allowed, Good.NickelIron, stock: 95000, demand: 5000);   // pays modestly
            var ship = LoadedShip(traderFaction, ShipClass.Freighter, Good.NickelIron, tonnes: 200, cost: 100);

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(ship.HasCargo, Is.False);
            Assert.That(allowed.Stock[(int)Good.NickelIron], Is.EqualTo(95200).Within(1e-6));
            Assert.That(forbidden.Stock[(int)Good.NickelIron], Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void OnArriveIsANoOpWhenTheShipHasNoCargo()
        {
            var economy = new Economy(new Diplomacy(), startJd: 1000);
            var port = MakePort(economy, "Port", 5, MakeFaction("Owner"));
            SetGood(port, Good.NickelIron, stock: 1000, demand: 500);
            var ship = MakeShip(MakeFaction("Trader"), ShipClass.Freighter); // HasCargo = false

            economy.OnArrive(ship, body: 5, jd: 1000);

            Assert.That(port.Stock[(int)Good.NickelIron], Is.EqualTo(1000));
            Assert.That(ship.HasCargo, Is.False);
        }
    }
}
