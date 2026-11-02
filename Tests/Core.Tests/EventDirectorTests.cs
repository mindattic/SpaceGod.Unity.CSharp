using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The dynamic event system (U.SPC-T44): <see cref="EventDirector"/> offers adventures only from real
    /// sim state near the player, never twice at once, within its cap and radius, and settles each one --
    /// completed (and paid) when the player sees it through, failed when ignored past its expiry or when the
    /// situation resolves without them.
    /// </summary>
    [TestFixture]
    public class EventDirectorTests
    {
        const double Jd = 2461324.0;

        Faction traders, navy, pirates, hunters;
        Lane lane;
        List<Ship> ships;
        List<Wreck> wrecks;
        Dictionary<Ship, DVec3> where;
        double laneDanger;
        EventSources src;
        EventDirector director;
        Ship player;

        [SetUp]
        public void SetUp()
        {
            traders = new Faction("Free Traders", "MV", new Rgb(1, 1, 1));
            navy = new Faction("Navy", "NS", new Rgb(0, 0, 1)) { IsMilitary = true };
            pirates = new Faction("Black Banner", "", new Rgb(1, 0, 0)) { IsPirate = true };
            hunters = new Faction("Belt Consortium", "BCS", new Rgb(1, 0.5f, 0)) { IsMilitary = true };
            // Patrons pay out of their own treasuries (U.SPC-T391), so fund them.
            traders.Treasury = pirates.Treasury = hunters.Treasury = 1e9;
            lane = new Lane { Id = 7, A = 3, B = 4, Name = "Earth-Mars" };
            ships = new List<Ship>();
            wrecks = new List<Wreck>();
            where = new Dictionary<Ship, DVec3>();
            laneDanger = 0;
            src = new EventSources
            {
                Ships = ships,
                Wrecks = wrecks,
                ShipHelio = s => where.TryGetValue(s, out var p) ? p : DVec3.Zero,
                WreckHelio = w => w.Pos,
                BodyHelio = b => new DVec3(b * 1e6, 0, 0),
                NearestBody = p => 3,
                BodyName = b => "Body" + b,
                LaneDanger = l => laneDanger,
                PirateHunters = hunters,
            };
            director = new EventDirector(seed: 11) { AnomalyRatePerDay = 0 };
            player = new Ship { Id = 999, Name = "Player", Spec = ShipClassSpec.Of(ShipClass.Cruiser), Faction = navy };
        }

        Ship Add(ShipClass cls, Faction f, DVec3 at, ShipRole role = ShipRole.Trader)
        {
            var s = new Ship { Id = ships.Count, Name = "Ship " + ships.Count, Spec = ShipClassSpec.Of(cls), Faction = f, Role = role, Phase = ShipPhase.Parked };
            ships.Add(s);
            where[s] = at;
            return s;
        }

        static readonly DVec3 Near = new DVec3(1e6, 0, 0);
        static readonly DVec3 Here = DVec3.Zero;
        static readonly DVec3 Far = new DVec3(5 * Astro.AuKm, 0, 0);

        Battle Fight(Ship s)
        {
            var b = new Battle(1, 3, "Battle of Body3", DVec3.Zero, Jd, 1);
            s.Battle = b;
            return b;
        }

        void Tick(double jd, Ship pilot = null, DVec3? at = null) => director.Update(jd, src, at ?? Here, pilot);

        // ---------------------------------------------------------------- spawning rules

        [Test]
        public void AMerchantUnderAttackNearbySendsADistressCall()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            Fight(s);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Distress));
            Assert.That(op.UnderAttack, Is.True);
            Assert.That(op.Ship, Is.SameAs(s));
            Assert.That(op.Hook, Does.Contain(s.Name));
            Assert.That(op.ExpiresJd, Is.GreaterThan(Jd));
            Assert.That(op.HelioKm, Is.EqualTo(Near));
            Assert.That(op.MarkerLabel, Is.EqualTo("DISTRESS"));
        }

        /// <summary>U.SPC-T57: a merchant whose tanks ran dry drifts and calls for help; a pilot who comes
        /// alongside pumps some of their own reaction mass across and sees it through.</summary>
        [Test]
        public void AShipDriftingWithDryTanksCallsForHelpAndAPilotCanRefuelIt()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.OutOfPropellant = true;
            s.Propellant = 0;
            s.PhaseEndJd = Jd + 2;
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Distress));
            Assert.That(op.DryTanks, Is.True);
            Assert.That(op.UnderAttack, Is.False);
            Assert.That(op.Title, Does.Contain("Dry tanks"));
            Assert.That(op.ExpiresJd, Is.EqualTo(Jd + 2), "open until the tanker gets there");

            double before = player.Propellant;
            Tick(Jd + 0.01, player, Near);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            double moved = s.PropellantCapacity * director.DryTanksTransfer;
            Assert.That(s.Propellant, Is.EqualTo(moved).Within(1e-9));
            Assert.That(player.Propellant, Is.EqualTo(before - moved).Within(1e-9));
            Assert.That(s.PhaseEndJd, Is.EqualTo(Jd + 0.01).Within(1e-9), "with that in its tanks it does not wait for the tanker");
            Assert.That(op.Outcome, Does.Contain("reaction mass"));
        }

        [Test]
        public void ADryShipsCallLapsesWhenItsTankerGetsThereFirst()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.OutOfPropellant = true;
            s.Propellant = 0;
            s.PhaseEndJd = Jd + 2;
            Tick(Jd);
            var op = Expect.Single(director.Active);
            s.OutOfPropellant = false;
            s.Propellant = s.PropellantCapacity;
            Tick(Jd + 0.5);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Failed));
            Assert.That(op.Outcome, Does.Contain("tanker"));
        }

        [Test]
        public void NothingIsOfferedBeyondTheSpawnRadius()
        {
            var s = Add(ShipClass.Freighter, traders, Far);
            Fight(s);
            var d = Add(ShipClass.Freighter, traders, Far);
            d.Derelict = true;
            Tick(Jd);
            Assert.That(director.Active, Is.Empty);
        }

        [Test]
        public void ShipsNotInTroubleOfferNothing()
        {
            Add(ShipClass.Freighter, traders, Near);
            Add(ShipClass.Cruiser, navy, Near, ShipRole.Patrol);
            Tick(Jd);
            Assert.That(director.Active, Is.Empty);
        }

        [Test]
        public void WarshipsInBattleAndPiratesDoNotCallForHelp()
        {
            Fight(Add(ShipClass.Cruiser, navy, Near, ShipRole.Patrol));
            var raider = Add(ShipClass.Gunship, pirates, Near, ShipRole.Patrol);
            Fight(raider);
            director.BountyRadiusKm = 0; // keep the raider's bounty out of this
            Tick(Jd);
            Assert.That(director.Active, Is.Empty);
        }

        [Test]
        public void AShipAdriftAfterInterdictionCallsForAssistance()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Phase = ShipPhase.Adrift;
            s.LaneLeg = new LaneHop(lane, 3, 4);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Distress));
            Assert.That(op.UnderAttack, Is.False);
            Assert.That(op.Hook, Does.Contain("interdiction"));
        }

        [Test]
        public void ADerelictIsOfferedUntilNoLaterThanItBreaksUp()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Derelict = true;
            s.DerelictSinceJd = Jd - 23;
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Derelict));
            Assert.That(op.ExpiresJd, Is.EqualTo(Jd + 2).Within(1e-9), "a derelict burns out 25 days after it was left");
        }

        [Test]
        public void AWreckIsOfferedWithItsSalvageButAPlatformsOrAnExpiredOneIsNot()
        {
            var wreck = new Wreck { Id = 1, Name = "Ship X", Ship = new Ship(), Pos = Near, CreatedJd = Jd, LifetimeDays = 21 };
            wreck.Salvage.Add(new SalvageStack(Good.Electronics, 500));
            wrecks.Add(wreck);
            wrecks.Add(new Wreck { Id = 2, Name = "Platform", Ship = null, Pos = Near, CreatedJd = Jd, LifetimeDays = 21 });
            wrecks.Add(new Wreck { Id = 3, Name = "Old", Ship = new Ship(), Pos = Near, CreatedJd = Jd - 30, LifetimeDays = 21 });
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Wreck));
            Assert.That(op.Wreck, Is.SameAs(wreck));
            Assert.That(op.Hook, Does.Contain("electronics"), "the hook says what its hold still reads");
            Assert.That(op.Reward.CargoCrates, Is.Zero, "the cargo is the player's to take, not a reward");
            Assert.That(op.ExpiresJd, Is.LessThanOrEqualTo(wreck.CreatedJd + wreck.LifetimeDays));
        }

        [Test]
        public void RaidersLurkingAreReportedOnlyOnADangerousLane()
        {
            director.BountyRadiusKm = 0;
            var r = Add(ShipClass.Gunship, pirates, Near, ShipRole.Patrol);
            r.Phase = ShipPhase.Lurk;
            r.LaneLeg = new LaneHop(lane, 3, 4);
            r.PhaseEndJd = Jd + 1;

            laneDanger = director.AmbushDanger - 0.5;
            Tick(Jd);
            Assert.That(director.Active, Is.Empty, "a quiet lane: no warning");

            laneDanger = director.AmbushDanger + 0.5;
            Tick(Jd + 1);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.PirateAmbush));
            Assert.That(op.Lane, Is.SameAs(lane));
            Assert.That(op.Patron, Is.SameAs(hunters));
        }

        [Test]
        public void AMerchantOnADangerousLaneAsksForAnEscort()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Phase = ShipPhase.Lane;
            s.LaneLeg = new LaneHop(lane, 3, 4);
            s.PhaseEndJd = Jd + 1.5;

            laneDanger = director.EscortDanger - 0.1;
            Tick(Jd);
            Assert.That(director.Active, Is.Empty);

            laneDanger = director.EscortDanger + 1;
            Tick(Jd + 0.5);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.ConvoyEscort));
            Assert.That(op.ExpiresJd, Is.GreaterThanOrEqualTo(s.PhaseEndJd));
        }

        [Test]
        public void NoEscortIsAskedForAtTheEndOfTheRideOrByAWarship()
        {
            laneDanger = 10;
            var late = Add(ShipClass.Freighter, traders, Near);
            late.Phase = ShipPhase.Lane;
            late.LaneLeg = new LaneHop(lane, 3, 4);
            late.PhaseEndJd = Jd + director.MinEscortDays / 2;
            var warship = Add(ShipClass.Cruiser, navy, Near, ShipRole.Patrol);
            warship.Phase = ShipPhase.Lane;
            warship.LaneLeg = new LaneHop(lane, 3, 4);
            warship.PhaseEndJd = Jd + 2;
            Tick(Jd);
            Assert.That(director.Active, Is.Empty);
        }

        [Test]
        public void ABountyNamesARealRaiderCaptainFurtherAfieldThanOtherEvents()
        {
            var r = Add(ShipClass.Destroyer, pirates, new DVec3(Astro.AuKm, 0, 0), ShipRole.Patrol);
            Add(ShipClass.Destroyer, pirates, new DVec3(1.2 * Astro.AuKm, 0, 0), ShipRole.Patrol);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Bounty));
            Assert.That(op.Ship, Is.SameAs(r), "the nearest captain");
            Assert.That(op.Captain, Is.EqualTo(PirateCaptains.NameFor(r)));
            Assert.That(op.Captain, Does.StartWith("Captain "));
            Assert.That(op.Hook, Does.Contain(op.Captain));

            Tick(Jd + 1);
            Assert.That(director.Active.Count(o => o.Kind == OpportunityKind.Bounty), Is.EqualTo(1), "one bounty posted at a time");
        }

        [Test]
        public void ACaptainsNameIsTheSameEveryTime()
        {
            var r = new Ship { Id = 42, Name = "Lightless" };
            Assert.That(PirateCaptains.NameFor(r), Is.EqualTo(PirateCaptains.NameFor(new Ship { Id = 42, Name = "Lightless" })));
        }

        [Test]
        public void TheSameSourceIsNeverOfferedTwiceAtOnce()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            Tick(Jd);
            Tick(Jd + 0.5);
            Tick(Jd + 1);
            Assert.That(director.Active, Has.Exactly(1).Items);
        }

        [Test]
        public void ASourceIsRestedAfterItsOpportunityEnds()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Phase = ShipPhase.Adrift;
            Tick(Jd);
            Assert.That(director.Active, Has.Exactly(1).Items);
            s.Phase = ShipPhase.Lane; // got going again on its own: the call fails
            Tick(Jd + 0.1);
            Assert.That(director.Active, Is.Empty);
            s.Phase = ShipPhase.Adrift; // pulled out again straight away
            Tick(Jd + 0.2);
            Assert.That(director.Active, Is.Empty, "rested");
            Tick(Jd + 2);
            Assert.That(director.Active, Has.Exactly(1).Items, "offered again once rested");
        }

        /// <summary>The private rest book, by reflection: it is never exposed, but a source whose rest has long
        /// passed should not still be sitting in it over a long game (U.SPC-T321).</summary>
        static System.Collections.Generic.IDictionary<string, double> RestBook(EventDirector d) =>
            (System.Collections.Generic.IDictionary<string, double>)typeof(EventDirector)
                .GetField("restUntil", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(d);

        [Test]
        public void ARestedSourcesBookEntryIsDroppedOnceItsRestIsOverNotKeptForever()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Phase = ShipPhase.Adrift;
            Tick(Jd);
            Assert.That(director.Active, Has.Exactly(1).Items);
            s.Phase = ShipPhase.Lane; // got going again on its own: the call fails and the source goes to rest
            Tick(Jd + 0.1);
            Assert.That(director.Active, Is.Empty);
            Assert.That(RestBook(director).Count, Is.GreaterThan(0), "the source never went to rest");
            Tick(Jd + 5); // well past a day's rest: another scan should drop the dead entry, not just ignore it
            Assert.That(RestBook(director).Count, Is.EqualTo(0), "a rested source's book entry was never dropped once its rest was over");
        }

        [Test]
        public void TheCapHoldsAndLivesAtStakeComeFirst()
        {
            director.MaxActive = 2;
            for (int i = 0; i < 3; i++)
            {
                var d = Add(ShipClass.Freighter, traders, Near * (i + 1));
                d.Derelict = true;
                d.DerelictSinceJd = Jd;
            }
            var victim = Add(ShipClass.Freighter, traders, Near * 5);
            Fight(victim);
            Tick(Jd);
            Assert.That(director.Active, Has.Exactly(2).Items);
            Assert.That(director.Active.Any(o => o.Kind == OpportunityKind.Distress && o.Ship == victim), Is.True, "a distress call beats salvage, though further");
        }

        [Test]
        public void NewSourcesWaitForTheNextScan()
        {
            director.ScanIntervalDays = 0.1;
            Tick(Jd);
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            Tick(Jd + 0.05);
            Assert.That(director.Active, Is.Empty);
            Tick(Jd + 0.1);
            Assert.That(director.Active, Has.Exactly(1).Items);
        }

        [Test]
        public void AnomaliesAreRareRollsAnchoredNearThePlayer()
        {
            director.AnomalyRatePerDay = 0;
            for (int i = 0; i < 50; i++) Tick(Jd + i * 0.1);
            Assert.That(director.Active, Is.Empty, "rate 0: never");

            director.AnomalyRatePerDay = 1e6;
            Tick(Jd + 10);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Anomaly));
            Assert.That(op.Body, Is.EqualTo(3));
            Assert.That((op.HelioKm - Here).Length, Is.LessThanOrEqualTo(director.SpawnRadiusKm));
            Assert.That(op.HelioKm, Is.EqualTo(src.BodyHelio(3) + op.OffsetKm), "fixed in its body's frame");

            Tick(Jd + 11);
            Assert.That(director.Active.Count(o => o.Kind == OpportunityKind.Anomaly), Is.EqualTo(1), "one at a time");
        }

        // ---------------------------------------------------------------- expiry and outcomes

        [Test]
        public void AnIgnoredOpportunityFailsAtItsExpiry()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            var resolved = new List<Opportunity>();
            director.Resolved += resolved.Add;
            Tick(Jd);
            var op = Expect.Single(director.Active);

            Tick(op.ExpiresJd - 0.01);
            Assert.That(op.IsActive, Is.True);
            Tick(op.ExpiresJd);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Failed));
            Assert.That(op.Outcome, Does.StartWith("Expired"));
            Assert.That(director.Active, Is.Empty);
            Assert.That(director.History, Does.Contain(op));
            Assert.That(resolved, Is.EqualTo(new[] { op }));
        }

        [Test]
        public void AnObserverCannotCollectOnlyAPilotedShipArriving()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Phase = ShipPhase.Adrift;
            Tick(Jd, pilot: null, at: Near);
            var op = Expect.Single(director.Active);
            Assert.That(op.Engaged, Is.False);

            Tick(Jd + 0.1, pilot: player, at: Near + new DVec3(0, director.ArriveKm * 2, 0));
            Assert.That(op.IsActive, Is.True, "not close enough");
            Tick(Jd + 0.2, pilot: player, at: Near + new DVec3(0, director.ArriveKm / 2, 0));
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
        }

        [Test]
        public void ADerelictIsSeenThroughByLatchingOnAndWorkingItNotByFlyingPast()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Freighter), ShipClass.Freighter);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            s.Combat.Interior.Abandon(0, 0, 1);
            s.Salvage.Add(new SalvageStack(Good.Machinery, 5000));
            player.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser);
            double before = navy.Treasury;
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Hook, Does.Contain("machinery"));

            Tick(Jd + 0.1, player, Near);
            Assert.That(op.IsActive && op.Engaged, Is.True, "on the scene, but nothing done yet");

            var session = SalvageSession.Latch(player, s);
            int crates = session.SalvageCargo();
            session.CastOff();
            Assert.That(director.HulkWorked(session, Jd + 0.11), Is.SameAs(op));
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(op.Outcome, Does.Contain("took " + crates + " crates"));
            Assert.That(navy.Treasury - before, Is.EqualTo(op.Reward.Credits));
            Assert.That(player.PersonalHold.Items.Count(i => i.Good == Good.Machinery), Is.EqualTo(crates).And.GreaterThan(0));
        }

        [Test]
        public void SurvivorsBroughtOutOfADerelictEarnTheirOwnersThanks()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Freighter), ShipClass.Freighter);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            s.Combat.Interior.Abandon(6, 0, 1);
            player.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            double plain = op.Reward.Credits;
            Assert.That(op.Hook, Does.Contain("movement aboard"));

            var session = SalvageSession.Latch(player, s); // nobody hostile: the survivors are found at once
            session.CastOff();
            director.HulkWorked(session, Jd + 0.1);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(op.Outcome, Does.Contain("rescued 6 survivors"));
            Assert.That(op.Reward.Credits, Is.EqualTo(plain + 6 * director.SurvivorRescueCredits));
            Assert.That(director.ReputationWith(traders), Is.EqualTo(director.SurvivorRescueReputation));
        }

        [Test]
        public void OnlyTheSurvivorsThanksComeOutOfTheirOwnersTreasuryNotTheRegistrysAward()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Freighter), ShipClass.Freighter);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            s.Combat.Interior.Abandon(6, 0, 1);
            player.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            double plain = op.Reward.Credits;
            traders.Treasury = 20000;
            double navyBefore = navy.Treasury;

            var session = SalvageSession.Latch(player, s);
            session.CastOff();
            director.HulkWorked(session, Jd + 0.1);
            Assert.That(traders.Treasury, Is.Zero, "the owners pay what they hold of the thanks");
            Assert.That(op.Reward.Credits, Is.EqualTo(plain + 20000), "the registry's award is paid in full whatever the owners hold");
            Assert.That(navy.Treasury - navyBefore, Is.EqualTo(plain + 20000));
        }

        [Test]
        public void LatchingOnAndLeavingEmptyHandedSeesNothingThrough()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            s.Combat = new ShipCombat(CombatSpec.Of(ShipClass.Freighter), ShipClass.Freighter);
            s.Derelict = true;
            s.DerelictSinceJd = Jd;
            s.Combat.Interior.Abandon(0, 0, 1);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            var session = SalvageSession.Latch(player, s);
            session.CastOff();
            Assert.That(director.HulkWorked(session, Jd + 0.1), Is.Null);
            Assert.That(op.IsActive, Is.True);
        }

        [Test]
        public void ADistressCallAnsweredPaysAndRaisesStandingIgnoredItFails()
        {
            var helped = Add(ShipClass.Freighter, traders, Near);
            var b1 = Fight(helped);
            var alone = Add(ShipClass.Courier, traders, Near * 2);
            Fight(alone);
            Tick(Jd);
            Assert.That(director.Active, Has.Exactly(2).Items);
            var helpedOp = director.Active.Single(o => o.Ship == helped);
            var aloneOp = director.Active.Single(o => o.Ship == alone);

            Tick(Jd + 0.01, player, Near);  // on the scene of the first fight only
            Assert.That(helpedOp.Engaged, Is.True);
            helped.Battle = null; alone.Battle = null; // both fights end, both ships alive
            Tick(Jd + 0.02, player, Near);

            Assert.That(helpedOp.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(director.ReputationWith(traders), Is.EqualTo(helpedOp.Reward.Reputation));
            Assert.That(aloneOp.State, Is.EqualTo(OpportunityState.Failed));
        }

        [Test]
        public void ABrokePatronPaysOnlyWhatItHoldsAndThePlayerGetsOnlyThat()
        {
            var helped = Add(ShipClass.Freighter, traders, Near);
            Fight(helped);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            double asked = op.Reward.Credits;
            Assert.That(asked, Is.GreaterThan(1000));
            traders.Treasury = 1000;
            Tick(Jd + 0.01, player, Near);
            double navyBefore = navy.Treasury;
            helped.Battle = null;
            Tick(Jd + 0.02, player, Near);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(op.Reward.Credits, Is.EqualTo(1000));
            Assert.That(traders.Treasury, Is.Zero);
            Assert.That(navy.Treasury - navyBefore, Is.EqualTo(1000));
        }

        [Test]
        public void AFundedPatronIsDebitedTheFullReward()
        {
            var helped = Add(ShipClass.Freighter, traders, Near);
            Fight(helped);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            traders.Treasury = 1e9;
            Tick(Jd + 0.01, player, Near);
            double navyBefore = navy.Treasury;
            helped.Battle = null;
            Tick(Jd + 0.02, player, Near);
            Assert.That(traders.Treasury, Is.EqualTo(1e9 - op.Reward.Credits));
            Assert.That(navy.Treasury - navyBefore, Is.EqualTo(op.Reward.Credits));
        }

        [Test]
        public void TheRewardGoesToWhoeverCameNotWhoeverFliesWhenItResolves()
        {
            var helped = Add(ShipClass.Freighter, traders, Near);
            Fight(helped);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Tick(Jd + 0.01, player, Near); // the navy pilot comes
            double navyBefore = navy.Treasury, piratesBefore = pirates.Treasury;
            var raider = new Ship { Id = 998, Name = "Sandbox raider", Spec = ShipClassSpec.Of(ShipClass.Destroyer), Faction = pirates };
            helped.Battle = null; // resolves while the player is flying a pirate hull far away
            Tick(Jd + 0.02, raider, Far);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(navy.Treasury - navyBefore, Is.EqualTo(op.Reward.Credits));
            Assert.That(pirates.Treasury, Is.EqualTo(piratesBefore), "paid to the faction of whatever hull was flown at the time");
        }

        [Test]
        public void NobodyCollectsABountyFromInsideTheRaidersOwnBand()
        {
            var r = Add(ShipClass.Destroyer, pirates, new DVec3(Astro.AuKm, 0, 0), ShipRole.Patrol);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Bounty));
            // The player takes over one of the captain's own escorts, flying beside the flagship. Taking
            // the helm clears an escort's Leader at once (see EnterPiloting), so it is the band id it was
            // mustered in under -- not Leader -- that still says it was this flagship's (U.SPC-T363).
            r.BandId = r.Id;
            var escort = new Ship { Id = 997, Name = "Escort", Spec = ShipClassSpec.Of(ShipClass.Destroyer), Faction = pirates, BandId = r.BandId };
            Tick(Jd + 0.01, escort, new DVec3(Astro.AuKm, 0, 0));
            Assert.That(op.Engaged, Is.False);
        }

        [Test]
        public void AHullFromAnUnrelatedBandOfTheSameClanStillCollectsTheBounty()
        {
            var r = Add(ShipClass.Destroyer, pirates, new DVec3(Astro.AuKm, 0, 0), ShipRole.Patrol);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Bounty));
            // Flying a raider of the same clan, but from an entirely different band: not on this one, so
            // busting it still counts (U.SPC-T363 -- the old check compared the whole clan's Faction).
            var otherBand = new Ship { Id = 996, Name = "Other raider", Spec = ShipClassSpec.Of(ShipClass.Destroyer), Faction = pirates };
            Tick(Jd + 0.01, otherBand, new DVec3(Astro.AuKm, 0, 0));
            Assert.That(op.Engaged, Is.True);
        }

        [Test]
        public void AnAmbushIsNotBrokenByOneOfItsOwnBandButIsByAnotherBandsHull()
        {
            director.BountyRadiusKm = 0;
            var r = Add(ShipClass.Gunship, pirates, Near, ShipRole.Patrol);
            r.Phase = ShipPhase.Lurk;
            r.LaneLeg = new LaneHop(lane, 3, 4);
            r.PhaseEndJd = Jd + 1;
            r.BandId = r.Id;
            laneDanger = director.AmbushDanger + 0.5;
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.PirateAmbush));

            var escort = new Ship { Id = 998, Name = "Escort", Spec = ShipClassSpec.Of(ShipClass.Gunship), Faction = pirates, BandId = r.BandId };
            Tick(Jd + 0.01, escort, Near);
            Assert.That(op.Engaged, Is.False, "one of this very band's own hulls doesn't count");

            var otherBand = new Ship { Id = 994, Name = "Other raider", Spec = ShipClassSpec.Of(ShipClass.Gunship), Faction = pirates };
            Tick(Jd + 0.02, otherBand, Near);
            Assert.That(op.Engaged, Is.True, "an unrelated raider of the same clan does");
        }

        [Test]
        public void TakingTheHelmOfTheShipInTroubleIsNotSeeingItThrough()
        {
            var adrift = Add(ShipClass.Freighter, traders, Near);
            adrift.Phase = ShipPhase.Adrift;
            Tick(Jd);
            var op = Expect.Single(director.Active);
            // The player takes that very ship: its spot is the player's own.
            Tick(Jd + 0.01, adrift, Near);
            Assert.That(op.Engaged, Is.False);
            Assert.That(op.State, Is.Not.EqualTo(OpportunityState.Completed), "paid for taking the helm of the ship it was about");
        }

        [Test]
        public void AFightSeenThroughIsPaidEvenAfterThePlayerLetsGoOfTheHelm()
        {
            var helped = Add(ShipClass.Freighter, traders, Near);
            Fight(helped);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            double before = navy.Treasury;
            Tick(Jd + 0.01, player, Near); // on the scene
            Assert.That(op.Engaged, Is.True);
            helped.Battle = null;          // the fight ends after the player has released the helm
            Tick(Jd + 0.02, pilot: null, at: Far);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(navy.Treasury - before, Is.EqualTo(op.Reward.Credits), "the outcome said paid, but no credits arrived");
        }

        [Test]
        public void ADistressCallFailsWhenTheShipIsLost()
        {
            var s = Add(ShipClass.Freighter, traders, Near);
            Fight(s);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Tick(Jd + 0.01, player, Near);
            s.Destroyed = true;
            Tick(Jd + 0.02, player, Near);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Failed));
            Assert.That(op.Outcome, Does.Contain("destroyed"));
            Assert.That(director.ReputationWith(traders), Is.Zero);
        }

        [Test]
        public void AnEscortIsPaidOnlyIfThePlayerJoinedItBeforeItCleared()
        {
            laneDanger = 5;
            var escorted = Add(ShipClass.Freighter, traders, Near);
            var unescorted = Add(ShipClass.Freighter, traders, Near * 20);
            foreach (var s in new[] { escorted, unescorted })
            {
                s.Phase = ShipPhase.Lane;
                s.LaneLeg = new LaneHop(lane, 3, 4);
                s.PhaseEndJd = Jd + 1;
            }
            Tick(Jd);
            Assert.That(director.Active, Has.Exactly(2).Items);
            var a = director.Active.Single(o => o.Ship == escorted);
            var b = director.Active.Single(o => o.Ship == unescorted);
            Tick(Jd + 0.2, player, Near);
            escorted.Phase = unescorted.Phase = ShipPhase.Transit; // both off the far ramp
            Tick(Jd + 1, player, Near * 50);
            Assert.That(a.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(b.State, Is.EqualTo(OpportunityState.Failed));
        }

        [Test]
        public void ABountyIsCollectedOnlyByAPlayerWhoWasThere()
        {
            var r = Add(ShipClass.Destroyer, pirates, Near, ShipRole.Patrol);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            Assert.That(op.Kind, Is.EqualTo(OpportunityKind.Bounty));
            Tick(Jd + 0.1, player, Near);
            r.Destroyed = true;
            Tick(Jd + 0.2, player, Near);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(director.ReputationWith(hunters), Is.GreaterThan(0));
        }

        [Test]
        public void SalvagingAWreckEmptiesWhatWasTaken()
        {
            var wreck = new Wreck { Id = 1, Name = "Ship X", Faction = traders, Ship = new Ship { Spec = ShipClassSpec.Of(ShipClass.Freighter) }, Pos = Near, CreatedJd = Jd, LifetimeDays = 21 };
            double crate = CargoFootprint.Of(Good.Copper).Tonnes;
            wreck.Salvage.Add(new SalvageStack(Good.Copper, crate)); // exactly one crate's worth
            wrecks.Add(wreck);
            Tick(Jd);
            Tick(Jd + 0.1, player, Near);
            Assert.That(director.Active.Single().IsActive, Is.True, "flying past salvages nothing");
            var session = SalvageSession.Latch(player, wreck);
            Assert.That(session.SalvageCargo(), Is.EqualTo(1));
            session.CastOff();
            director.HulkWorked(session, Jd + 0.2);
            Assert.That(director.History.Single().State, Is.EqualTo(OpportunityState.Completed));
            Assert.That(wreck.Salvage, Is.Empty);
        }

        [Test]
        public void AWreckClearedAwayFailsItsOpportunity()
        {
            var wreck = new Wreck { Id = 1, Name = "Ship X", Ship = new Ship(), Pos = Near, CreatedJd = Jd, LifetimeDays = 21 };
            wrecks.Add(wreck);
            Tick(Jd);
            var op = Expect.Single(director.Active);
            wrecks.Clear();
            Tick(Jd + 0.1);
            Assert.That(op.State, Is.EqualTo(OpportunityState.Failed));
        }

        // ---------------------------------------------------------------- the live system

        [Test]
        public void InTheLiveSolSystemEveryOpportunityComesFromRealSimState()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(Jd);
            var w = World.CreateDefault(eph, Jd);
            var d = w.Adventures;
            d.AnomalyRatePerDay = 0;
            d.SpawnRadiusKm = 60 * Astro.AuKm; // the whole system is "near", so plenty is offered
            d.MaxActive = 50;
            var spawned = new List<Opportunity>();
            d.Spawned += op =>
            {
                spawned.Add(op);
                var s = op.Ship;
                switch (op.Kind)
                {
                    case OpportunityKind.Distress:
                        Assert.That(s.Battle != null || s.Phase == ShipPhase.Adrift || s.OutOfPropellant, Is.True, $"{s.Name} is really in trouble");
                        break;
                    case OpportunityKind.Derelict: Assert.That(s.Derelict, Is.True); break;
                    case OpportunityKind.Wreck: Assert.That(w.War.Wrecks.Items, Does.Contain(op.Wreck)); break;
                    case OpportunityKind.PirateAmbush: Assert.That(s.Phase, Is.EqualTo(ShipPhase.Lurk)); break;
                    case OpportunityKind.ConvoyEscort: Assert.That(s.Phase, Is.EqualTo(ShipPhase.Lane)); break;
                    case OpportunityKind.Bounty: Assert.That(s.Faction.IsPirate && !s.Destroyed, Is.True); break;
                }
            };
            var earth = w.Catalog["Earth"].Index;
            for (double jd = Jd; jd < Jd + 20; jd += 0.01)
            {
                eph.Update(jd);
                w.Update(jd);
                w.UpdateAdventures(jd, eph.Helio(earth));
            }
            Assert.That(spawned, Is.Not.Empty);
            Assert.That(spawned.Any(o => o.Kind == OpportunityKind.Bounty), Is.True, "the Black Banner's captains are posted");
            Assert.That(w.Events.Any(e => e.Kind == WorldEventKind.Opportunity), Is.True, "offers reach the ticker");
            TestContext.Out.WriteLine(string.Join("\n", spawned.GroupBy(o => o.Kind).Select(g => g.Key + ": " + g.Count())));
        }
    }
}
