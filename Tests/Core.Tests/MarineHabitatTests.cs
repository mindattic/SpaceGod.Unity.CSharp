using System;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Marine habitats (U.SPC-T465): marines live and train in a habitat fitted into a hull's internal space, so a ship's
    /// marine complement is what its habitats quarter, not a flat figure per class; only hulls big enough for one carry
    /// marines; the habitat is the barracks compartment defenders muster from; drilling in it raises the marines' quality;
    /// and what a habitat has ready is what assault teams are drawn from.
    /// </summary>
    [TestFixture]
    public class MarineHabitatTests
    {
        static readonly Faction Navy = new Faction("Navy", "NS", new Rgb(0, 0, 1)) { IsMilitary = true };
        static readonly Faction Red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsPirate = true, MarineQuality = 1.1 };

        static FacilityKind Habitat => FacilityKinds.Of(FacilityKinds.MarineHabitat);

        [Test]
        public void EveryStockDesignKeepsItsMarinesInTheHabitatsItIsDrawnWith()
        {
            var expected = new (ShipClass cls, double marines)[]
            {
                (ShipClass.Carrier, 320), (ShipClass.Battleship, 260), (ShipClass.Cruiser, 140), (ShipClass.Destroyer, 50),
                (ShipClass.Freighter, 4),
                // A boarding shuttle's team rides in its troop bay (U.SPC-T486).
                (ShipClass.AssaultShuttle, 24),
                // The command cruiser (U.SPC-T477) quarters a small marine guard for the flag.
                (ShipClass.CommandCruiser, 120),
                // The gate breaker (U.SPC-T478) quarters the boarding teams it takes a gate with.
                (ShipClass.GateBreaker, 340),
            };
            foreach (var (cls, marines) in expected)
                Assert.That(CombatSpec.Of(cls).Marines, Is.EqualTo(marines), cls.ToString());
            Assert.That(CombatSpec.Platform.Marines, Is.EqualTo(60));
            Assert.That(HullFrames.OrbitalStation.Marines, Is.EqualTo(180));

            foreach (ShipClass c in Enum.GetValues(typeof(ShipClass)))
            {
                var design = CombatSpec.Of(c);
                Assert.That(design.Marines, Is.EqualTo(FacilityLayouts.Complement(c, design)), c + ": the complement is its facilities' berths");
                bool listed = expected.Any(e => e.cls == c);
                if (!listed) Assert.That(design.Marines, Is.EqualTo(0), c + " has no habitat and carries no marines");
                // Every warship's marines live in a habitat; a boarding shuttle's team rides in its troop bay (U.SPC-T486).
                bool habitat = FacilityLayouts.For(c).Any(s => s.Kind == FacilityKinds.MarineHabitat);
                Assert.That(habitat, Is.EqualTo(listed && c != ShipClass.AssaultShuttle), c.ToString());
            }
        }

        [Test]
        public void TheHabitatIsARegisteredFacilityOnlyHullsBigEnoughMayCarry()
        {
            var k = Habitat;
            Assert.That(k, Is.Not.Null);
            Assert.That(k.Fittable, Is.True);
            Assert.That(k.Size, Is.EqualTo(1));
            Assert.That(k.Compartment, Is.EqualTo(CompartmentKind.Barracks));
            Assert.That(k.Berths, Is.EqualTo(MarineHabitats.BerthsPerScale));
            Assert.That(k.Bill.Length, Is.GreaterThan(0));

            foreach (var c in new[] { ShipClass.Destroyer, ShipClass.Cruiser, ShipClass.Battleship, ShipClass.Carrier, ShipClass.Freighter, ShipClass.LaneTender })
                Assert.That(MarineHabitats.CanCarry(HullFrames.ForClass(c)), Is.True, c.ToString());
            foreach (var c in new[] { ShipClass.Fighter, ShipClass.Interceptor, ShipClass.Bomber, ShipClass.Gunship, ShipClass.Courier })
            {
                Assert.That(MarineHabitats.CanCarry(HullFrames.ForClass(c)), Is.False, c + " is too small for a habitat");
                Assert.That(new ConstructionPlan(HullFrames.ForClass(c)).FitFacility(k), Is.EqualTo(ConstructionOutcome.WontFit), c.ToString());
            }
            Assert.That(MarineHabitats.CanCarry(HullFrames.Of("satellite/weapons")), Is.False);
            Assert.That(MarineHabitats.CanCarry(HullFrames.Of("station/orbital-station")), Is.True);
        }

        [Test]
        public void ASmallHullRepelsBoardersWithItsCrewAlone()
        {
            var fighter = new ShipCombat(CombatSpec.Of(ShipClass.Fighter), ShipClass.Fighter);
            Assert.That(fighter.Interior.HasBarracks, Is.False);
            Assert.That(fighter.Interior.Marines, Is.EqualTo(0));
            Assert.That(fighter.Interior.Crew, Is.EqualTo(CombatSpec.Of(ShipClass.Fighter).Crew).Within(1e-9));
        }

        [Test]
        public void DefendersMusterFromTheBarracksAndTheCrewWorkTheRestOfTheShip()
        {
            var spec = CombatSpec.Of(ShipClass.Destroyer);
            var interior = new ShipCombat(spec, ShipClass.Destroyer).Interior;
            var barracks = interior.Barracks.Single();
            Assert.That(barracks.Name, Is.EqualTo("Marine barracks"));
            Assert.That(barracks.Marines, Is.EqualTo(50).Within(1e-9));
            Assert.That(barracks.Crew, Is.EqualTo(0), "the ship's company does not live in the barracks");
            int crewSpaces = interior.Compartments.Count - 1;
            foreach (var c in interior.Compartments.Where(c => c != barracks))
            {
                Assert.That(c.Crew, Is.EqualTo(spec.Crew / crewSpaces).Within(1e-9), "crew spread as before the habitat");
                Assert.That(c.Marines, Is.EqualTo(0));
            }
            Assert.That(MarineHabitats.Ready(interior), Is.EqualTo(50).Within(1e-9));
            // Marines coming back aboard go home to their barracks.
            interior.TakeMarines(20);
            interior.Embark(0, 20);
            Assert.That(barracks.Marines, Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void FittingAHabitatGivesAHullMarinesAndStrippingOneTakesThemAway()
        {
            // A frigate is drawn with no marines; a habitat quarters fifty a unit of hull scale.
            var plan = new ConstructionPlan(HullFrames.ForClass(ShipClass.Frigate));
            Assert.That(plan.Build().Marines, Is.EqualTo(0));
            double free = plan.FreeSpace;
            Assert.That(plan.FitFacility(Habitat), Is.EqualTo(ConstructionOutcome.Ok));
            Assert.That(plan.FreeSpace, Is.EqualTo(free - 1), "it takes a section of the free space");
            double berths = MarineHabitats.BerthsPerScale * plan.Loadout.Scale;
            Assert.That(plan.Build().Marines, Is.EqualTo(berths).Within(1e-9));
            Assert.That(MarineHabitats.Complement(plan.Loadout), Is.EqualTo(berths).Within(1e-9));
            Assert.That(MarineHabitats.Count(plan.Loadout), Is.EqualTo(1));
            var ship = new Ship { Id = 1, Name = "FF", Spec = ShipClassSpec.Of(ShipClass.Frigate), Faction = Navy };
            Assert.That(plan.Apply(ship), Is.True);
            Assert.That(ship.Combat.Interior.HasBarracks, Is.True);
            Assert.That(MarineHabitats.Ready(ship), Is.EqualTo(berths).Within(1e-9));

            // A destroyer that strips its own habitat for plate repels boarders with its crew alone.
            var dd = new ConstructionPlan(HullFrames.ForClass(ShipClass.Destroyer));
            int index = dd.Facilities.ToList().FindIndex(f => f.Kind == FacilityKinds.MarineHabitat);
            Assert.That(dd.RemoveFacility(index), Is.EqualTo(ConstructionOutcome.Ok));
            Assert.That(dd.FreeSpace, Is.EqualTo(FacilityLayouts.SpareSpace(ShipClass.Destroyer) + 1));
            Assert.That(dd.Build().Marines, Is.EqualTo(0));
            var stripped = dd.BuildCombat().Interior;
            Assert.That(stripped.HasBarracks, Is.False);
            Assert.That(stripped.Marines, Is.EqualTo(0));
        }

        [Test]
        public void MarinesDrillInTheirHabitatAndGetBetterOverTime()
        {
            var interior = new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser).Interior;
            Assert.That(interior.Training, Is.EqualTo(0), "a new hull's marines are raw");
            Assert.That(interior.DrillFactor, Is.EqualTo(1), "and fight as marines always did");
            double days = MarineHabitats.DrillDays.Value;
            interior.Drill(days / 2);
            Assert.That(interior.Training, Is.EqualTo(0.5).Within(1e-9));
            interior.Drill(days);
            Assert.That(interior.Training, Is.EqualTo(1), "fully trained, no further");
            Assert.That(interior.DrillFactor, Is.EqualTo(1 + MarineHabitats.DrillBonus.Value).Within(1e-9));

            // No habitat, no drill: a boarding shuttle's team rides in its troop bay, a fighter has none.
            var shuttle = new ShipCombat(CombatSpec.Of(ShipClass.AssaultShuttle), ShipClass.AssaultShuttle).Interior;
            Assert.That(shuttle.Marines, Is.EqualTo(24).Within(1e-9));
            shuttle.Drill(days);
            Assert.That(shuttle.Training, Is.EqualTo(0));

            // Nor while the ship is being boarded.
            var boarded = new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser).Interior;
            boarded.Board(Red, 1.1, 30, boarded.Of(CompartmentKind.Bow), true, 0, new Random(1));
            boarded.Drill(days);
            Assert.That(boarded.Training, Is.EqualTo(0));
        }

        [Test]
        public void RawRecruitsDiluteTheComplementsDrill()
        {
            var interior = new ShipCombat(CombatSpec.Of(ShipClass.Destroyer), ShipClass.Destroyer).Interior;
            interior.Training = 1;
            interior.TakeMarines(25);
            interior.Muster(interior.Crew, 50);
            Assert.That(interior.Marines, Is.EqualTo(50).Within(1e-9));
            Assert.That(interior.Training, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(interior.Barracks.Single().Marines, Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void TrainedMarinesRepelBoardersBetterThanRawOnes()
        {
            // The same landing against the same cruiser, its marines raw and then fully drilled: both repel it, but the
            // drilled ones kill boarders faster and so lose fewer of their own.
            double Lost(double training)
            {
                var interior = new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser).Interior;
                interior.Training = training;
                var rng = new Random(7);
                interior.Board(Red, 1.1, 60, interior.Of(CompartmentKind.Bow), true, 0, rng);
                for (int s = 0; s < 3600 && interior.Boarder != null; s++) interior.Step(1, s, rng);
                Assert.That(interior.Captured, Is.False);
                return interior.MarinesLost + interior.CrewLost;
            }
            Assert.That(Lost(1), Is.LessThan(Lost(0)), "fully drilled defenders lose fewer of their own");
        }

        [Test]
        public void AHabitatsReadyMarinesAreWhatAssaultTeamsAreDrawnFrom()
        {
            var interior = new ShipCombat(CombatSpec.Of(ShipClass.Battleship), ShipClass.Battleship).Interior;
            Assert.That(MarineHabitats.Ready(interior), Is.EqualTo(260).Within(1e-9));
            Assert.That(MarineHabitats.TeamsReady(interior, 70), Is.EqualTo(3));
            Assert.That(MarineHabitats.Draw(interior, 70), Is.EqualTo(70).Within(1e-9));
            Assert.That(MarineHabitats.Ready(interior), Is.EqualTo(190).Within(1e-9));
            Assert.That(interior.Marines, Is.EqualTo(190).Within(1e-9));
            Assert.That(MarineHabitats.Draw(interior, 1000), Is.EqualTo(190).Within(1e-9), "no more than it has");

            // While boarders are aboard, every marine is needed at home.
            var boarded = new ShipCombat(CombatSpec.Of(ShipClass.Battleship), ShipClass.Battleship).Interior;
            boarded.Board(Red, 1.1, 30, boarded.Of(CompartmentKind.Bow), true, 0, new Random(1));
            Assert.That(MarineHabitats.Ready(boarded), Is.EqualTo(0));
            Assert.That(MarineHabitats.Draw(boarded, 10), Is.EqualTo(0));
        }

        [Test]
        public void AYardFittedHabitatKeepsTheDrillOfTheMarinesAlreadyAboard()
        {
            var economy = new Economy(new Diplomacy(), 0);
            var yard = economy.Add("Yard", body: 3, SettlementKind.Station, Navy, 0);
            yard.Facilities.Add(new Facility(Recipe.Of(IndustryKind.Shipyard), 1));
            foreach (var g in new[] { Good.Alloys, Good.Electronics, Good.Machinery, Good.Munitions }) yard.Stock[(int)g] = 1e6;
            yard.RefreshPrices();
            Navy.Treasury = 1e12;
            var ship = new Ship { Id = 2, Name = "DD", Spec = ShipClassSpec.Of(ShipClass.Destroyer), Faction = Navy, Combat = new ShipCombat(CombatSpec.Of(ShipClass.Destroyer), ShipClass.Destroyer) };
            ship.Combat.Interior.Training = 1;
            Assert.That(Outfitting.FitFacility(economy, ship, yard, Habitat, out _), Is.EqualTo(OutfitOutcome.Ok));
            var inside = ship.Combat.Interior;
            Assert.That(ship.Combat.Spec.Marines, Is.EqualTo(100).Within(1e-9), "a second destroyer habitat, fifty more berths");
            Assert.That(inside.Barracks.Count(), Is.EqualTo(2));
            Assert.That(inside.Marines, Is.EqualTo(100).Within(1e-9));
            Assert.That(inside.Training, Is.EqualTo(0.5).Within(1e-9), "half the complement are raw recruits");

            var copy = ObjectGraph.Clone(ship);
            Assert.That(copy.Combat.Interior.Training, Is.EqualTo(0.5).Within(1e-9), "the drill survives a save");
            Assert.That(copy.Combat.Spec.Marines, Is.EqualTo(100).Within(1e-9));
        }

        [Test]
        public void TheDrillTunablesAreInTheSettingsMenu()
        {
            Assert.That(SettingsRegistry.Default.Find("war.marineDrillDays"), Is.SameAs(MarineHabitats.DrillDays));
            Assert.That(SettingsRegistry.Default.Find("war.marineDrillBonus"), Is.SameAs(MarineHabitats.DrillBonus));
        }
    }
}
