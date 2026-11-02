using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The lane interdictor (U.SPC-T479): the interdiction pulse comes from a hull carrying a field projector, not from any
    /// raider lying beside a lane. The projector is heavy equipment, a facility fitted into a hull big enough for it and its
    /// reactor load, not a ship class (U.SPC-T507). Raider bands fit one for their lane ambushes and navies fit one in a lane
    /// picket, and ships its pulse throws out of the lane drop into the battle beside it as before.
    /// </summary>
    // Not run beside other fixtures: one test changes the picket chance for a moment, a static the save code walks.
    [TestFixture, NonParallelizable]
    public class LaneInterdictorTests
    {
        const double StartJd = 2461324.0;
        const double Second = 1.0 / Astro.SecondsPerDay;

        sealed class Setup
        {
            public World W;
            public Ephemeris Eph;
            public Lane Lane;
            public LaneHop Hop;
            public DVec3 Ramp;
            public int Earth, Mars;
        }

        /// <summary>Default Sol with every ship out of the picture, and Earth's ramp onto the Earth-Mars lane.</summary>
        static Setup Create()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            foreach (var s in w.Fleet.Ships) s.Destroyed = true;
            w.War.Battles.Clear();
            var lanes = w.Fleet.Lanes;
            int earth = eph.Catalog["Earth"].Index, mars = eph.Catalog["Mars"].Index;
            var lane = lanes.Lanes.First(l => (l.A == earth && l.B == mars) || (l.A == mars && l.B == earth));
            var ramp = lanes.RampHelio(lane, earth, StartJd);
            var hop = lanes.OnRampAt(ramp, StartJd).Value;
            return new Setup { W = w, Eph = eph, Lane = lane, Hop = hop, Ramp = ramp, Earth = earth, Mars = mars };
        }

        static Ship Hull(World w, string name, ShipClass c, Faction f, ShipRole role = ShipRole.Patrol)
        {
            var s = w.Fleet.AddShip(name, c, f, role);
            s.Combat = new ShipCombat(CombatSpec.Of(c), c);
            return s;
        }

        /// <summary>A group led by a hull of <paramref name="leaderClass"/> lying in wait halfway along the lane, with escorts of <paramref name="escorts"/>.</summary>
        static Ship Lurk(Setup t, string name, ShipClass leaderClass, Faction f, params ShipClass[] escorts)
        {
            var leader = Hull(t.W, name, leaderClass, f);
            for (int i = 0; i < escorts.Length; i++)
            {
                var e = Hull(t.W, name + " " + (i + 2), escorts[i], f, ShipRole.Escort);
                t.W.Fleet.AddEscort(leader, e, Fleet.SlotFor(i));
            }
            t.W.Fleet.JoinLane(leader, t.Hop, t.Ramp, StartJd);
            leader.Phase = ShipPhase.Lurk;
            leader.LaneHold = 0.5;
            leader.LaneHoldSide = t.W.Fleet.OffLane(leader, StartJd, 0.5, 30000, 1);
            leader.PhaseStartJd = StartJd;
            leader.PhaseEndJd = StartJd + 5;
            return leader;
        }

        /// <summary><paramref name="s"/> fitted out as a lane interdictor: a field projector in its free space.</summary>
        static Ship Fitted(Ship s)
        {
            Assert.That(LaneInterdictors.Fit(s), Is.True, s.Name + " took no projector");
            return s;
        }

        /// <summary>The escort of <paramref name="leader"/> of class <paramref name="c"/>, fitted with a projector.</summary>
        static Ship FitEscort(Setup t, Ship leader, ShipClass c = ShipClass.Destroyer) =>
            Fitted(t.W.Fleet.Ships.First(s => s.Leader == leader && s.Spec.Class == c));

        static Ship Ride(Setup t, string name, ShipClass c, Faction f)
        {
            var s = Hull(t.W, name, c, f);
            t.W.Fleet.JoinLane(s, t.Hop, t.Ramp, StartJd);
            return s;
        }

        /// <summary>Steps the war along until the ride is over or <paramref name="caught"/> is in a fight; whether it was caught.</summary>
        static bool RideOut(Setup t, Ship caught, double step = 30 * Second)
        {
            double end = caught.PhaseEndJd;
            for (double jd = StartJd; jd <= end; jd += step)
            {
                t.Eph.Update(jd);
                t.W.War.Update(jd);
                if (caught.Battle != null) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- heavy equipment, not a class

        static readonly Faction Navy = new Faction("Navy", "NS", new Rgb(0, 0, 1)) { IsMilitary = true };

        static Ship Bare(ShipClass c, int id = 1)
        {
            Navy.Treasury = 1e12;
            return new Ship { Id = id, Name = c.ToString() + id, Spec = ShipClassSpec.Of(c), Faction = Navy, Combat = new ShipCombat(CombatSpec.Of(c), c) };
        }

        [Test]
        public void TheProjectorIsHeavyEquipmentNotAShipClass()
        {
            Assert.That(System.Enum.GetNames(typeof(ShipClass)), Has.No.Member("Interdictor"), "no lane interdictor class");
            Assert.That(HullFrames.Catalogue.Select(f => f.Name), Has.No.Member("Lane interdictor"), "no lane interdictor hull frame");
            var kind = LaneInterdictors.Kind;
            Assert.That(kind, Is.Not.Null);
            Assert.That(kind.Id, Is.EqualTo(FacilityKinds.FieldProjector));
            Assert.That(kind.Fittable, Is.True, "a yard fits and strips it");
            Assert.That(kind.Size, Is.GreaterThanOrEqualTo(2), "the projector and the reactor that feeds it");
            Assert.That(kind.Bill, Is.Not.Empty);

            // Any hull big enough to carry it and its reactor load may be fitted out as an interdictor ...
            foreach (var c in new[] { ShipClass.Destroyer, ShipClass.Cruiser, ShipClass.Freighter, ShipClass.CommandCruiser, ShipClass.Battleship, ShipClass.Carrier })
            {
                Assert.That(LaneInterdictors.CanCarry(c), Is.True, c + " is big enough");
                Assert.That(HullFrames.ForClass(c).CanCarry(kind), Is.True, c + "'s frame takes it");
            }
            // ... and nothing smaller.
            foreach (var c in new[] { ShipClass.Frigate, ShipClass.Corvette, ShipClass.Fighter, ShipClass.Gunship, ShipClass.Courier, ShipClass.GatePicket, ShipClass.LaneTender, ShipClass.OreTug })
                Assert.That(HullFrames.ForClass(c).CanCarry(kind), Is.False, c + " is too small for its reactor load");

            // No stock design carries one: a hull is fitted out as an interdictor by choice.
            foreach (ShipClass c in System.Enum.GetValues(typeof(ShipClass)))
                Assert.That(FacilityLayouts.CountOf(FacilityLayouts.For(c), FacilityKinds.FieldProjector), Is.Zero, c.ToString());
            Assert.That(LaneInterdictors.Carries(Bare(ShipClass.Destroyer)), Is.False);
        }

        [Test]
        public void AYardFitsAndStripsTheProjectorAndTheHullBecomesAnInterdictorAndBackAgain()
        {
            var economy = new Economy(new Diplomacy(), 0);
            var yard = economy.Add("Yard", body: 3, SettlementKind.Station, Navy, 0);
            yard.Facilities.Add(new Facility(Recipe.Of(IndustryKind.Shipyard), 2));
            foreach (var g in new[] { Good.Alloys, Good.Electronics, Good.Machinery, Good.HeliumThree })
                yard.Stock[(int)g] = 1e6;
            yard.RefreshPrices();

            foreach (var c in new[] { ShipClass.Destroyer, ShipClass.Cruiser })
            {
                var ship = Bare(c);
                var wing = new[] { ship };
                Assert.That(LaneInterdictors.Fielded(ship, wing), Is.False);
                double before = Navy.Treasury;
                Assert.That(Outfitting.FitFacility(economy, ship, yard, LaneInterdictors.Kind, out var quote), Is.EqualTo(OutfitOutcome.Ok), c + " fitted");
                Assert.That(quote.Net, Is.GreaterThan(0));
                Assert.That(Navy.Treasury, Is.LessThan(before), "the yard's bill");
                Assert.That(LaneInterdictors.Carries(ship), Is.True);
                Assert.That(LaneInterdictors.EmitterOf(ship, wing), Is.SameAs(ship), "the " + c + " is now the group's interdictor");

                int at = Outfitting.LoadoutOf(ship).Facilities.FindIndex(f => f.Kind == FacilityKinds.FieldProjector);
                Assert.That(Outfitting.StripFacility(economy, ship, yard, at, out _), Is.EqualTo(OutfitOutcome.Ok), c + " stripped");
                Assert.That(LaneInterdictors.Carries(ship), Is.False);
                Assert.That(LaneInterdictors.Fielded(ship, wing), Is.False, "stripped, it pulses no more");
            }

            // A frigate has neither the room nor the reactor for one.
            Assert.That(Outfitting.FitFacility(economy, Bare(ShipClass.Frigate, 2), yard, LaneInterdictors.Kind, out _), Is.EqualTo(OutfitOutcome.WontFit));
        }

        // ---------------------------------------------------------------- the pulse comes from the interdictor

        [Test]
        public void ARaiderBandWithoutAnInterdictorPullsNobodyOutOfTheLane()
        {
            var t = Create();
            var raider = Lurk(t, "Shade", ShipClass.Destroyer, t.W.Pirates, ShipClass.Destroyer);
            var mark = Ride(t, "Fat Purse", ShipClass.Freighter, t.W.Independents);
            Assert.That(LaneInterdictors.Fielded(raider, t.W.Fleet.Ships), Is.False);
            Assert.That(RideOut(t, mark), Is.False, "thrown out of the lane by a band with no projector");
            Assert.That(raider.Battle, Is.Null);
        }

        [Test]
        public void TheBandsInterdictorPulsesAndItsMarkDropsIntoTheFightBesideTheLane()
        {
            var t = Create();
            var raider = Lurk(t, "Shade", ShipClass.Destroyer, t.W.Pirates, ShipClass.Destroyer);
            var emitter = FitEscort(t, raider);
            Assert.That(LaneInterdictors.EmitterOf(raider, t.W.Fleet.Ships), Is.SameAs(emitter));
            var mark = Ride(t, "Fat Purse", ShipClass.Freighter, t.W.Independents);
            Assert.That(RideOut(t, mark), Is.True, "the band's interdictor pulled it out");

            var b = mark.Battle;
            Assert.That(b.Site, Is.Not.Null, "fought beside the lane, at no body");
            Assert.That(b.Site.Hop.Lane, Is.SameAs(t.Lane));
            Assert.That(b.EnforcingSide, Is.EqualTo(-1), "a raiders' ambush, not the law");
            Assert.That(mark.Phase, Is.EqualTo(ShipPhase.Adrift));
            Assert.That(b.UnitOf(emitter), Is.Not.Null, "the interdictor fights with its band");
            var pulse = b.Fx.Where(f => f.Kind == FxKind.Pulse).ToList();
            Assert.That(pulse, Is.Not.Empty);
            Assert.That(pulse[0].Unit, Is.SameAs(b.UnitOf(emitter)), "the pulse comes from the interdictor, not the band's leader");
        }

        [Test]
        public void LosingItsInterdictorEndsTheBandsLaneAmbush()
        {
            var t = Create();
            var raider = Lurk(t, "Shade", ShipClass.Destroyer, t.W.Pirates, ShipClass.Destroyer);
            FitEscort(t, raider);
            foreach (var s in t.W.Fleet.Ships.Where(s => s.Leader == raider)) s.Destroyed = true;
            var mark = Ride(t, "Fat Purse", ShipClass.Freighter, t.W.Independents);
            Assert.That(RideOut(t, mark), Is.False);
        }

        [Test]
        public void EveryRaiderBandSailsWithAnInterdictor()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            var bands = w.Fleet.Ships.Where(s => !s.Destroyed && !s.IsEscort && s.Faction == w.Pirates && s.Spec.IsMilitary && !s.Runner).ToList();
            Assert.That(bands, Is.Not.Empty);
            foreach (var band in bands)
            {
                Assert.That(LaneInterdictors.Fielded(band, w.Fleet.Ships), Is.True, band.Name + " has no interdictor");
                // Fitted to a hull of the band, not a class of its own (U.SPC-T507).
                var emitter = LaneInterdictors.EmitterOf(band, w.Fleet.Ships);
                Assert.That(LaneInterdictors.CanCarry(emitter.Spec.Class), Is.True);
                Assert.That(Outfitting.LoadoutOf(emitter).CountFacility(FacilityKinds.FieldProjector), Is.EqualTo(1));
            }
        }

        // ---------------------------------------------------------------- navies field it

        [Test]
        public void ANavysLanePicketPullsOutRaidersAndLetsLawfulTrafficRideOn()
        {
            var t = Create();
            var picket = Lurk(t, "Vigil", ShipClass.Destroyer, t.W.Sol, ShipClass.Destroyer);
            FitEscort(t, picket);
            var raider = Ride(t, "Gutter Knife", ShipClass.Gunship, t.W.Pirates);
            var trader = Ride(t, "Honest Margin", ShipClass.Freighter, t.W.Independents);
            Assert.That(t.W.War.Targets(picket, raider), Is.True);
            Assert.That(t.W.War.Targets(picket, trader), Is.False, "lawful traffic is no business of the picket's");

            Assert.That(RideOut(t, raider), Is.True, "the picket pulled the raider out");
            var b = raider.Battle;
            Assert.That(b.Site, Is.Not.Null, "fought beside the lane");
            Assert.That(b.EnforcingSide, Is.EqualTo(b.UnitOf(picket).Side), "the navy enforces the law on the lane");
            Assert.That(b.UnitOf(raider).Side, Is.Not.EqualTo(b.UnitOf(picket).Side));
            Assert.That(trader.Battle, Is.Null, "the trader rode through");
            Assert.That(trader.Phase, Is.EqualTo(ShipPhase.Lane));
        }

        [Test]
        public void ARaidersPulseSparesOtherRaidersAndANavysSparesItsOwn()
        {
            var t = Create();
            var raider = Fitted(Lurk(t, "Shade", ShipClass.Destroyer, t.W.Pirates));
            var picket = Fitted(Hull(t.W, "Vigil", ShipClass.Destroyer, t.W.Sol));
            var mate = Hull(t.W, "Brother", ShipClass.Destroyer, t.W.Pirates);
            var own = Hull(t.W, "Sister", ShipClass.Destroyer, t.W.Sol);
            Assert.That(t.W.War.Targets(raider, mate), Is.False);
            Assert.That(t.W.War.Targets(raider, own), Is.True);
            Assert.That(t.W.War.Targets(picket, own), Is.False);
            Assert.That(t.W.War.Targets(picket, mate), Is.True);
        }

        [Test]
        public void ALanePicketWingCarriesAnInterdictorAndLiesInWaitOnALaneOfItsBeat()
        {
            // The lane picket wing fits a projector to one of its destroyers (U.SPC-T507).
            int picketWing = Enumerable.Range(0, 4).First(i => PatrolWings.FittingsOf(i).Any(f => f.facility == FacilityKinds.FieldProjector));
            foreach (var (member, _) in PatrolWings.FittingsOf(picketWing))
                Assert.That(LaneInterdictors.CanCarry(PatrolWings.Composition(picketWing)[member]), Is.True, "fitted to a hull that can carry it");
            var t = Create();
            // An older group, so the wing is not taken for the navy's home guard.
            Hull(t.W, "Old Guard", ShipClass.Destroyer, t.W.Sol);
            var wing = Hull(t.W, "Tripwire", ShipClass.Destroyer, t.W.Sol);
            wing.PatrolWing = true;
            wing.Route = new[] { t.Earth, t.Mars };
            var e = Fitted(Hull(t.W, "Tripwire Two", ShipClass.Destroyer, t.W.Sol, ShipRole.Escort));
            e.PatrolWing = true;
            t.W.Fleet.AddEscort(wing, e, Fleet.SlotFor(0));
            t.W.Fleet.Station(wing, t.Earth, StartJd);
            t.W.Fleet.Lanes.SetDanger(t.Lane, 5);
            double chance = LaneInterdictors.PicketChance.Value;
            try
            {
                LaneInterdictors.PicketChance.Set(1);
                int next = t.W.War.ChooseNext(wing, t.Earth, StartJd);
                var o = t.W.War.OrderOf(wing);
                Assert.That(o.Kind, Is.EqualTo(OrderKind.Ambush), "the picket wing lies in wait on the lane");
                Assert.That(next, Is.EqualTo(t.Mars), "bound along it for the next port of its beat");
                Assert.That(PatrolWings.OnBeat(wing, o.Body), Is.True);
                Assert.That(wing.LurkLane, Is.SameAs(t.Lane));
                Assert.That(wing.LurkAt, Is.InRange(0.3, 0.7));

                // A wing without one keeps to its rounds.
                e.Destroyed = true;
                t.W.War.ChooseNext(wing, t.Earth, StartJd);
                Assert.That(t.W.War.OrderOf(wing).Kind, Is.Not.EqualTo(OrderKind.Ambush));
                Assert.That(wing.LurkLane, Is.Null, "the stale picket is dropped");
            }
            finally
            {
                LaneInterdictors.PicketChance.Set(chance);
            }
        }

        [Test]
        public void ARaisedLanePicketWingFliesAFittedDestroyer()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            var pickets = w.Fleet.Ships.Where(s => !s.Destroyed && s.PatrolWing && !s.IsEscort && !s.Convoy && LaneInterdictors.Fielded(s, w.Fleet.Ships)).ToList();
            Assert.That(pickets, Is.Not.Empty, "a navy big enough for a lane picket wing flies one");
            foreach (var leader in pickets)
            {
                var emitter = LaneInterdictors.EmitterOf(leader, w.Fleet.Ships);
                Assert.That(emitter.Spec.Class, Is.EqualTo(ShipClass.Destroyer), leader.Name);
                Assert.That(emitter.Leader, Is.SameAs(leader), "a wingman carries it");
            }
        }

        [Test]
        public void ALaneSecurityBuildPlanFitsAProjectorToAHullOfEachGroup()
        {
            Assert.That(BuildPlan.All, Does.Contain(BuildPlan.LaneSecurity));
            Assert.That(BuildPlan.LaneSecurity.GroupFittings, Does.Contain(FacilityKinds.FieldProjector));
            Assert.That(BuildPlan.LaneSecurity.Cycle.Any(LaneInterdictors.CanCarry), Is.True, "it lays down hulls that can carry one");
            Assert.That(BuildPlan.Balanced.GroupFittings, Is.Empty);

            var t = Create();
            var yard = t.W.Economy.Settlements.First(s => s.Owner == t.W.Sol && s.HasShipyard);
            t.W.Polities.Command.SetBuild(t.W.Sol, BuildPlan.LaneSecurity);
            var first = t.W.War.LaunchWarship(yard, ShipClass.Destroyer, StartJd);
            Assert.That(LaneInterdictors.Carries(first), Is.True, "the group's first hull is its lane interdictor");
            // A second hull launched while the first is parked at the yard joins its group, which has its projector already.
            t.W.Fleet.Station(first, yard.Body, StartJd);
            var second = t.W.War.LaunchWarship(yard, ShipClass.Destroyer, StartJd);
            Assert.That(second.Leader, Is.SameAs(first), "it joins the group refitting there");
            Assert.That(LaneInterdictors.Carries(second), Is.False, "one projector to a group");
            Assert.That(LaneInterdictors.EmitterOf(first, t.W.Fleet.Ships), Is.SameAs(first));

            // Under another plan, a hull is launched as designed.
            t.W.Polities.Command.SetBuild(t.W.Sol, BuildPlan.Balanced);
            var plain = t.W.War.LaunchWarship(yard, ShipClass.Destroyer, StartJd);
            Assert.That(LaneInterdictors.Carries(plain), Is.False);
        }
    }
}
