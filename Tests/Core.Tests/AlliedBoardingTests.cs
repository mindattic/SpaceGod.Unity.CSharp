using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>Allied factions on one side boarding a hull together, and settling the prize (U.SPC-T392).</summary>
    [TestFixture]
    public class AlliedBoardingTests
    {
        static readonly Faction Blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true, MarineQuality = 1.2 };
        static readonly Faction Green = new Faction("Green", "GRN", new Rgb(0, 1, 0)) { IsMilitary = true, MarineQuality = 0.8 };
        static readonly Faction Red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsPirate = true, MarineQuality = 1.1 };

        static Interior Freighter() => new ShipCombat(CombatSpec.Of(ShipClass.Freighter), ShipClass.Freighter).Interior;

        [Test]
        public void AnAllyOnTheSameSideJoinsTheBoardingAndFightsAsOneParty()
        {
            var interior = Freighter();
            var rng = new Random(1);
            var hold = interior.Of(CompartmentKind.Hold);
            Assert.That(interior.Board(Blue, 1.2, 10, hold, true, 0, rng, 0), Is.True);
            Assert.That(interior.Board(Green, 0.8, 30, hold, true, 1, rng, 0), Is.True, "an ally on side 0 joins");
            Assert.That(interior.Boarders, Is.EqualTo(40).Within(1e-9));
            Assert.That(interior.Boarder, Is.EqualTo(Blue), "the first aboard leads");
            Assert.That(interior.Joint, Is.True);
            Assert.That(interior.IsParty(Green), Is.True);
            Assert.That(interior.BoarderQuality, Is.EqualTo((10 * 1.2 + 30 * 0.8) / 40).Within(1e-9));
            Assert.That(interior.BoarderNames, Is.EqualTo("Blue and Green"));
        }

        [Test]
        public void AFactionOnTheOtherSideOrWithNoSideIsStillTurnedBack()
        {
            var interior = Freighter();
            var rng = new Random(1);
            var hold = interior.Of(CompartmentKind.Hold);
            interior.Board(Blue, 1.2, 10, hold, true, 0, rng, 0);
            Assert.That(interior.Board(Red, 1.1, 10, hold, true, 1, rng, 1), Is.False);
            Assert.That(interior.Board(Green, 0.8, 10, hold, true, 1, rng), Is.False, "no side given: no joining");
            Assert.That(interior.Parties.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheRecordIsClearedWhenTheBoardersAreGone()
        {
            var interior = Freighter();
            var rng = new Random(1);
            interior.Board(Blue, 1.2, 10, interior.Of(CompartmentKind.Hold), true, 0, rng, 0);
            interior.Board(Green, 0.8, 10, interior.Of(CompartmentKind.Hold), true, 0, rng, 0);
            interior.BoardersGone();
            Assert.That(interior.Parties, Is.Empty);
            Assert.That(interior.BoarderSide, Is.EqualTo(-1));
        }

        static List<BoardingParty> Parties(double blueStrength, double greenStrength) => new List<BoardingParty>
        {
            new BoardingParty { Faction = Blue, Landed = blueStrength, Strength = blueStrength, Order = 0 },
            new BoardingParty { Faction = Green, Landed = greenStrength, Strength = greenStrength, Order = 1 },
        };

        [Test]
        public void TheLargestClaimantThatCanPayKeepsTheHullAndTheHoldIsShared()
        {
            var deal = PrizeSettlement.Negotiate(Parties(25, 75), 1000, 400, 100, f => 1e6);
            Assert.That(deal.Keeper, Is.EqualTo(Green));
            Assert.That(deal.Of(Blue).Share, Is.EqualTo(0.25).Within(1e-9));
            Assert.That(deal.Of(Blue).CargoTonnes, Is.EqualTo(25).Within(1e-9));
            Assert.That(deal.Of(Green).CargoTonnes, Is.EqualTo(75).Within(1e-9));
            Assert.That(deal.Of(Blue).HullPaid, Is.EqualTo(250).Within(1e-9), "bought out at its share of the prize's worth");
            Assert.That(deal.BuyOut, Is.EqualTo(250).Within(1e-9));
        }

        [Test]
        public void AClaimantThatCannotPayPassesTheHullToTheNext()
        {
            var deal = PrizeSettlement.Negotiate(Parties(25, 75), 1000, 400, 0, f => f == Green ? 100 : 1e6);
            Assert.That(deal.Keeper, Is.EqualTo(Blue));
            Assert.That(deal.Of(Green).HullPaid, Is.EqualTo(750).Within(1e-9));
        }

        [Test]
        public void WhenNobodyCanPayTheHullGoesToTheBreakersAndThePriceIsShared()
        {
            var deal = PrizeSettlement.Negotiate(Parties(50, 50), 1000, 400, 0, f => 0);
            Assert.That(deal.SoldToBreakers, Is.True);
            Assert.That(deal.Of(Blue).HullPaid, Is.EqualTo(200).Within(1e-9));
            Assert.That(deal.Of(Green).HullPaid, Is.EqualTo(200).Within(1e-9));
        }

        [Test]
        public void ATieGoesToTheFirstAboard()
        {
            var deal = PrizeSettlement.Negotiate(Parties(50, 50), 1000, 400, 0, f => 1e6);
            Assert.That(deal.Keeper, Is.EqualTo(Blue));
        }
    }
}
