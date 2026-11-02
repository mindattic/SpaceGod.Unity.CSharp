using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// U.SPC-T61: law, crime and reputation. Each faction's jurisdiction is its bodies, the systems whose lanes it
    /// keeps and those lanes; attacking lawful ships, piracy, smuggling, trespass and fleeing a scan are crimes,
    /// recorded only if a ship or station in sensor range saw them; a recorded crime raises notoriety, lowers
    /// standing and posts a bounty in that jurisdiction; patrols scan, hail and interdict the player's wanted ship;
    /// standing gates docking, prices and the contracts a faction offers.
    /// </summary>
    [TestFixture]
    public class LawTests
    {
        const double StartJd = 2461324.0;

        static World CreateWorld()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            return World.CreateDefault(eph, StartJd);
        }

        static int Body(World w, string name) => w.Catalog[name].Index;

        static Ship Hull(ShipClass c, Faction f, int id, string name = null) =>
            new Ship { Id = id, Name = name ?? c + "" + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c) };

        /// <summary>A piloted courier of the independents, holding still at <paramref name="body"/>'s centre plus nothing.</summary>
        static Ship Pilot(World w, ShipClass cls = ShipClass.Courier, Faction f = null)
        {
            var ship = w.Fleet.AddShip("Lawbreaker", cls, f ?? w.Independents, ShipRole.Free);
            ship.Orbit = new ParkingOrbit { Body = Body(w, "Earth") };
            ship.Phase = ShipPhase.Parked;
            w.PilotedShip = ship;
            return ship;
        }

        /// <summary>A navy patrol flagship parked at a body's centre (so its position is the body's).</summary>
        static Ship Patrol(World w, Faction f, int body)
        {
            var ship = w.Fleet.AddShip("Picket " + f.Prefix, ShipClass.Destroyer, f, ShipRole.Patrol);
            ship.Orbit = new ParkingOrbit { Body = body };
            ship.Phase = ShipPhase.Parked;
            ship.PhaseStartJd = StartJd;
            return ship;
        }

        /// <summary>Takes every other warship out of the picture, so only the patrols a test places hail the player.</summary>
        static void ClearNavies(World w)
        {
            foreach (var s in w.Fleet.Ships)
                if (s.Spec.IsMilitary) s.Destroyed = true;
            w.War.Battles.Clear();
        }

        static readonly DVec3 Deep = new DVec3(0, 0, 40 * Astro.AuKm);

        // ---------------------------------------------------------------- jurisdiction and witnesses

        [Test]
        public void EachFactionsLawHoldsAtItsWorldsAndTheSystemsWhoseLanesItKeeps()
        {
            var w = CreateWorld();
            var law = w.Law;
            Assert.That(law.JurisdictionAt(Body(w, "Earth")), Is.EqualTo(w.Sol));
            Assert.That(law.JurisdictionAt(Body(w, "Ceres")), Is.EqualTo(w.Belt));
            Assert.That(law.JurisdictionAt(Body(w, "Titan")), Is.EqualTo(w.Reach));
            // Deimos holds only the pirates' haven, but Mars's lanes are the Federation's.
            Assert.That(law.JurisdictionAt(Body(w, "Deimos")), Is.EqualTo(w.Sol));
            // A hidden haven's rock out in the belt is nobody's.
            Assert.That(law.JurisdictionAt(Body(w, "Psyche")), Is.Null);
            var lane = w.Fleet.Lanes.Lanes.First(l => l.A == Body(w, "Earth") || l.B == Body(w, "Earth"));
            int from = lane.A == Body(w, "Earth") ? lane.A : lane.B;
            Assert.That(law.JurisdictionOf(new LaneHop(lane, from, lane.A == from ? lane.B : lane.A)), Is.EqualTo(w.Sol));
        }

        [Test]
        public void AStationSeesWhatHappensNearItButNobodySeesTheDeepDark()
        {
            var w = CreateWorld();
            var earth = w.Ephemeris.HelioAt(Body(w, "Earth"), StartJd);
            Assert.That(w.Law.FindWitness(earth + new DVec3(50000, 0, 0), StartJd), Is.Not.Null);
            Assert.That(w.Law.FindWitness(earth + Deep, StartJd), Is.Null);
        }

        // ---------------------------------------------------------------- crimes and their cost

        [Test]
        public void AnUnwitnessedCrimeIsNeverRecorded()
        {
            var w = CreateWorld();
            var crime = w.Law.Commit(w.Law.Player, CrimeKind.Assault, w.Sol, w.Belt, Body(w, "Earth"), StartJd, null);
            Assert.That(crime.Recorded, Is.False);
            Assert.That(w.Law.Player.Wanted, Is.False);
            Assert.That(w.Law.Player.Notoriety, Is.EqualTo(0));
            Assert.That(w.Law.Player.StandingWith(w.Sol), Is.EqualTo(0));
            Assert.That(w.Law.Player.Unwitnessed, Is.EqualTo(1));
        }

        [Test]
        public void AWitnessedCrimeRaisesNotorietyLowersStandingAndPostsABountyInThatJurisdiction()
        {
            var w = CreateWorld();
            var p = w.Law.Player;
            var (notoriety, standing, bounty) = Law.Severity(CrimeKind.Piracy);
            var crime = w.Law.Commit(p, CrimeKind.Piracy, w.Sol, w.Belt, Body(w, "Earth"), StartJd, "Earth Orbital");
            Assert.That(crime.Recorded, Is.True);
            Assert.That(crime.PostedBy, Is.EqualTo(w.Sol));
            Assert.That(p.Notoriety, Is.EqualTo(notoriety));
            Assert.That(p.StandingWith(w.Sol), Is.EqualTo(-standing));
            Assert.That(p.StandingWith(w.Belt), Is.EqualTo(-standing * Law.VictimStandingShare).Within(1e-9), "the victim's own faction thinks less of them too");
            Assert.That(p.BountyBy(w.Sol), Is.EqualTo(bounty));
            Assert.That(p.BountyBy(w.Belt), Is.EqualTo(0), "the bounty is posted where it happened");
            Assert.That(w.Events.Any(e => e.Kind == WorldEventKind.Law && e.Text.Contains(w.Sol.Name)), Is.True);
            // A second crime there adds to it.
            w.Law.Commit(p, CrimeKind.Assault, w.Sol, w.Belt, Body(w, "Earth"), StartJd, "Earth Orbital");
            Assert.That(p.BountyBy(w.Sol), Is.EqualTo(bounty + Law.Severity(CrimeKind.Assault).bounty));
            Assert.That(p.Crimes.Count, Is.EqualTo(2));
            // The player's standing is the same table the adventures pay into.
            Assert.That(w.Adventures.ReputationWith(w.Sol), Is.EqualTo(p.StandingWith(w.Sol)));
        }

        [Test]
        public void InLawlessSpaceTheVictimsOwnFactionPostsTheBounty()
        {
            var w = CreateWorld();
            var crime = w.Law.Commit(w.Law.Player, CrimeKind.Assault, null, w.Belt, Body(w, "Psyche"), StartJd, "the Wanderer");
            Assert.That(crime.PostedBy, Is.EqualTo(w.Belt));
            Assert.That(w.Law.Player.BountyBy(w.Belt), Is.GreaterThan(0));
        }

        [Test]
        public void NotorietyFadesAndAnUnrenewedBountyLapses()
        {
            var w = CreateWorld();
            var p = w.Law.Player;
            w.Law.Update(StartJd);
            w.Law.Commit(p, CrimeKind.Assault, w.Sol, w.Sol, Body(w, "Earth"), StartJd, "Earth Orbital");
            double n0 = p.Notoriety;
            w.Law.Update(StartJd + Law.NotorietyHalfLifeDays);
            Assert.That(p.Notoriety, Is.EqualTo(n0 / 2).Within(1e-6));
            Assert.That(p.WantedBy(w.Sol), Is.True);
            w.Law.Update(StartJd + Law.BountyDays + 1);
            Assert.That(p.WantedBy(w.Sol), Is.False);
        }

        // ---------------------------------------------------------------- crimes in battle

        [Test]
        public void RaidersWhoAttackAMerchantInFrontOfWitnessesAreWantedForPiracy()
        {
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            var b = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            var merchant = Hull(ShipClass.Freighter, w.Belt, 9001, "Ledger");
            var raider = Hull(ShipClass.Destroyer, w.Pirates, 9002, "Gutter Knife");
            b.AddShip(merchant, 0, new DVec3(4, 0, 0), -DVec3.UnitX);
            b.AddShip(raider, 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            w.Law.OnBattleEnded(b, StartJd);
            var rec = w.Law.FindRecord(raider);
            Assert.That(rec, Is.Not.Null);
            Assert.That(rec.Name, Is.EqualTo(PirateCaptains.NameFor(raider)));
            Assert.That(rec.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Piracy));
            Assert.That(rec.Crimes.Single().Witness, Is.EqualTo("the Ledger"), "the merchant lived to tell it");
            Assert.That(rec.BountyBy(w.Sol), Is.EqualTo(Law.Severity(CrimeKind.Piracy).bounty));
            Assert.That(w.Law.FindRecord(merchant), Is.Null, "fighting off pirates is no crime");
            // The bounty adventure names the law's price on the captain's head, paid on top of its own (U.SPC-T63).
            Assert.That(EventSources.Of(w, StartJd).PostedBounty(raider), Is.EqualTo(rec.TotalBounty));
        }

        [Test]
        public void ACrimeOverAHavenNobodyHasFoundDoesNotNameItsRock()
        {
            var w = CreateWorld();
            int psyche = Body(w, "Psyche");
            var b = new Battle(0, psyche, "Skirmish in the Belt", new DVec3(2000, 0, 0), StartJd, 1);
            b.AddShip(Hull(ShipClass.Freighter, w.Belt, 9001, "Ledger"), 0, new DVec3(4, 0, 0), -DVec3.UnitX);
            var raider = Hull(ShipClass.Destroyer, w.Pirates, 9002);
            b.AddShip(raider, 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.FindRecord(raider).Wanted, Is.True);
            Assert.That(w.Events.Where(e => e.Kind == WorldEventKind.Law).All(e => !e.Text.Contains("Psyche") && e.Body != psyche), Is.True);
        }

        [Test]
        public void InTheLiveSystemRaidersComeToBeWantedAndNoNavyAnswersForItsWar()
        {
            var w = CreateWorld();
            for (double jd = StartJd; jd <= StartJd + 120; jd += 0.05) w.Update(jd);
            var recorded = w.Law.Recent;
            Assert.That(recorded, Is.Not.Empty, "four months of raiding leave someone on record");
            foreach (var c in recorded)
            {
                Assert.That(c.Witness, Is.Not.Null);
                Assert.That(c.PostedBy, Is.Not.Null);
                Assert.That(c.PostedBy.IsPirate, Is.False);
                // Federation and Reach ships fighting each other are at war, not breaking the law.
                if (c.Offender.Captain != null && !c.Offender.Captain.Faction.IsPirate)
                    Assert.That(w.Diplomacy.Get(c.Offender.Captain.Faction, c.Victim), Is.Not.EqualTo(Relation.War));
            }
            Assert.That(w.Law.Captains.Values.Any(r => r.Captain.Faction.IsPirate && r.Crimes.Count > 0), Is.True);
            Assert.That(w.Law.Player.Crimes, Is.Empty, "nobody was flying");
        }

        [Test]
        public void ACrimeWithNoSurvivorAndNobodyInSensorRangeGoesUnrecorded()
        {
            var w = CreateWorld();
            var b = new Battle(0, Body(w, "Earth"), "Out in the dark", Deep, StartJd, 1);
            var merchant = b.AddShip(Hull(ShipClass.Freighter, w.Belt, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX);
            var raider = Hull(ShipClass.Destroyer, w.Pirates, 9002);
            b.AddShip(raider, 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            merchant.Dead = true;
            w.Law.OnBattleEnded(b, StartJd);
            var rec = w.Law.FindRecord(raider);
            Assert.That(rec.Wanted, Is.False);
            Assert.That(rec.Unwitnessed, Is.EqualTo(1));
        }

        [Test]
        public void FightingAnEnemyAtWarIsNoCrimeButAttackingAFactionAtPeaceIs()
        {
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            // The Federation and the Reach are at war.
            var war = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            var solShip = Hull(ShipClass.Cruiser, w.Sol, 9001);
            war.AddShip(solShip, 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            war.AddShip(Hull(ShipClass.Cruiser, w.Reach, 9002), 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            w.Law.OnBattleEnded(war, StartJd);
            Assert.That(w.Law.Player.Crimes, Is.Empty);

            // The Federation and the Belt are not: a player in a Federation hull firing on a Belt merchant is an outlaw.
            var peace = new Battle(1, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 2);
            peace.AddShip(Hull(ShipClass.Cruiser, w.Sol, 9003), 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            var defender = Hull(ShipClass.Freighter, w.Belt, 9004);
            peace.AddShip(defender, 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            w.Law.OnBattleEnded(peace, StartJd);
            Assert.That(w.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Assault));
            Assert.That(w.Law.FindRecord(defender), Is.Null, "returning fire is no crime");
            Assert.That(w.Law.Player.WantedBy(w.Sol), Is.True, "Earth is Federation space");
        }

        [Test]
        public void RansomingALawfulShipsCargoIsPiracyAndAPlayerFlyingARaiderIsAPirate()
        {
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            var b = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            b.AddShip(Hull(ShipClass.Courier, w.Independents, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            var victim = b.AddShip(Hull(ShipClass.Freighter, w.Belt, 9002), 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            victim.SurrenderOutcome = SurrenderTerms.RansomCargo;
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Piracy));

            var w2 = CreateWorld();
            var b2 = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            b2.AddShip(Hull(ShipClass.Gunship, w2.Pirates, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            b2.AddShip(Hull(ShipClass.Freighter, w2.Belt, 9002), 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            w2.Law.OnBattleEnded(b2, StartJd);
            Assert.That(w2.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Piracy));
        }

        [Test]
        public void AMerchantTakenByPiratesIsNotChargedToTheShipsDefendingIt()
        {
            // A hunter and a Belt merchant against raiders who board and take the merchant: the prize moves to
            // the raiders' side, but the hunter neither attacked nor robbed it.
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            var b = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            var hunter = Hull(ShipClass.Cruiser, w.Independents, 9001, "Long Arm");
            b.AddShip(hunter, 0, new DVec3(4, 0, 0), -DVec3.UnitX);
            var merchant = b.AddShip(Hull(ShipClass.Freighter, w.Belt, 9002, "Ballast"), 0, new DVec3(5, 0, 0), -DVec3.UnitX);
            var raider = Hull(ShipClass.Destroyer, w.Pirates, 9003);
            b.AddShip(raider, 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            b.AggressorSide = 0;
            merchant.TakenFromSide = 0;
            merchant.Side = 1;
            merchant.Captured = true;
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.FindRecord(hunter)?.Crimes.Count ?? 0, Is.EqualTo(0), "the hunter defended the merchant");
            Assert.That(w.Law.FindRecord(raider).Crimes.Single().Kind, Is.EqualTo(CrimeKind.Piracy));

            // Nor is a hunter's escort that the raiders took: it is their prize, not one of their fighters.
            var w3 = CreateWorld();
            var b3 = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            var raider3 = Hull(ShipClass.Destroyer, w3.Pirates, 9001);
            b3.AddShip(raider3, 0, new DVec3(-4, 0, 0), DVec3.UnitX);
            var hunter3 = Hull(ShipClass.Cruiser, w3.Independents, 9002, "Long Arm");
            b3.AddShip(hunter3, 1, new DVec3(4, 0, 0), -DVec3.UnitX);
            var escort = b3.AddShip(Hull(ShipClass.Destroyer, w3.Independents, 9003, "Due Process"), 1, new DVec3(5, 0, 0), -DVec3.UnitX);
            escort.Ship.Leader = hunter3;
            b3.AddShip(Hull(ShipClass.Freighter, w3.Reach, 9004, "Seam"), 1, new DVec3(6, 0, 0), -DVec3.UnitX);
            b3.AggressorSide = 0;
            escort.TakenFromSide = 1;
            escort.Side = 0;
            escort.Captured = true;
            w3.Law.OnBattleEnded(b3, StartJd);
            Assert.That(w3.Law.FindRecord(hunter3)?.Crimes.Count ?? 0, Is.EqualTo(0), "the hunter's taken escort attacked nobody");
            Assert.That(w3.Law.FindRecord(escort.Ship)?.Crimes.Count ?? 0, Is.EqualTo(0));

            // A lawful hull that boards and takes a ship it is at peace with has robbed it, prize crew and all.
            var w2 = CreateWorld();
            var b2 = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            b2.AddShip(Hull(ShipClass.Cruiser, w2.Independents, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            var prize = b2.AddShip(Hull(ShipClass.Freighter, w2.Belt, 9002), 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            b2.AggressorSide = 0;
            prize.TakenFromSide = 1;
            prize.Side = 0;
            prize.Captured = true;
            w2.Law.OnBattleEnded(b2, StartJd);
            Assert.That(w2.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Piracy));
        }

        [Test]
        public void AHunterWhoJoinsANavyAgainstRaidersIsNotChargedForTheNavysEnemiesOnTheirSide()
        {
            // The Reach starts it against raiders with Federation warships beside them (the Reach and the Federation
            // are at war); a hunter joins the Reach against the raiders, and nobody is hurt or robbed.
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            w.Diplomacy.Set(w.Reach, w.Sol, Relation.War);
            var b = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            b.AddShip(Hull(ShipClass.Cruiser, w.Reach, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX);
            var hunter = Hull(ShipClass.Cruiser, w.Independents, 9002, "Warrant");
            b.AddShip(hunter, 0, new DVec3(5, 0, 0), -DVec3.UnitX);
            b.AddShip(Hull(ShipClass.Destroyer, w.Pirates, 9003), 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            b.AddShip(Hull(ShipClass.Cruiser, w.Sol, 9004), 1, new DVec3(-5, 0, 0), DVec3.UnitX);
            b.AggressorSide = 0;
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.FindRecord(hunter)?.Crimes.Count ?? 0, Is.EqualTo(0), "the hunter attacked nobody it is at peace with");
        }

        [Test]
        public void HuntingAShipWantedInThatJurisdictionIsNoCrime()
        {
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            var outlaw = Hull(ShipClass.Courier, w.Belt, 9002, "Wanted Man");
            w.Law.Commit(w.Law.RecordOf(outlaw), CrimeKind.Assault, w.Sol, w.Sol, earth, StartJd, "Earth Orbital");
            var b = new Battle(0, earth, "Battle of Earth", new DVec3(20000, 0, 0), StartJd, 1);
            b.AddShip(Hull(ShipClass.Cruiser, w.Sol, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            b.AddShip(outlaw, 1, new DVec3(-4, 0, 0), DVec3.UnitX).Dead = true;
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.Player.Crimes, Is.Empty);
            Assert.That(w.Law.FindRecord(outlaw).Wanted, Is.False, "a dead captain's bounties lapse");
        }

        [Test]
        public void ThePatrolEnforcingTheLawCommitsNothingAndTheWantedPaysByLosingTheShip()
        {
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            w.Law.Commit(w.Law.Player, CrimeKind.Assault, w.Sol, w.Sol, earth, StartJd, "Earth Orbital");
            var b = new Battle(0, earth, "Interdiction off Earth", new DVec3(20000, 0, 0), StartJd, 1) { EnforcingSide = 1 };
            var flown = b.AddShip(Hull(ShipClass.Courier, w.Independents, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX);
            flown.PlayerControlled = true;
            var patrol = Hull(ShipClass.Destroyer, w.Sol, 9002);
            b.AddShip(patrol, 1, new DVec3(-4, 0, 0), DVec3.UnitX);
            // Taken: the capture hook lets go of the helm, but the law still knows whose ship it was.
            flown.Captured = true;
            flown.PlayerControlled = false;
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.FindRecord(patrol), Is.Null);
            Assert.That(w.Law.Player.Crimes.Count, Is.EqualTo(1), "no new crime: the stopped ship hurt nobody");
            Assert.That(w.Law.Player.WantedBy(w.Sol), Is.False, "the Federation has collected");

            // Resisting -- destroying the patrol -- is another crime.
            var w2 = CreateWorld();
            w2.Law.Commit(w2.Law.Player, CrimeKind.Assault, w2.Sol, w2.Sol, earth, StartJd, "Earth Orbital");
            var b2 = new Battle(0, earth, "Interdiction off Earth", new DVec3(20000, 0, 0), StartJd, 1) { EnforcingSide = 1 };
            b2.AddShip(Hull(ShipClass.Cruiser, w2.Independents, 9001), 0, new DVec3(4, 0, 0), -DVec3.UnitX).PlayerControlled = true;
            b2.AddShip(Hull(ShipClass.Destroyer, w2.Sol, 9002), 1, new DVec3(-4, 0, 0), DVec3.UnitX).Dead = true;
            w2.Law.OnBattleEnded(b2, StartJd);
            Assert.That(w2.Law.Player.Crimes.Count, Is.EqualTo(2));
        }

        // ---------------------------------------------------------------- standing gates docking, prices and contracts

        [Test]
        public void LowStandingOrABigBountyClosesAFactionsPorts()
        {
            var w = CreateWorld();
            var ship = Pilot(w);
            var earthPort = w.Economy.Settlements.First(s => s.Name == "Earth Orbital");
            Assert.That(w.Economy.CanTrade(ship, earthPort), Is.True);
            w.Law.Player.Standing[w.Sol] = Law.DockMinStanding - 1;
            Assert.That(w.Economy.CanTrade(ship, earthPort), Is.False);
            Assert.That(w.Economy.BuyIntoHold(ship, earthPort, Good.Food, 1, out _), Is.EqualTo(Economy.TradeOutcome.Refused));
            Assert.That(w.Economy.RefuelAt(ship, earthPort, out _, out _), Is.EqualTo(Economy.TradeOutcome.Refused));
            // Belt ports still deal.
            Assert.That(w.Economy.CanTrade(ship, w.Economy.Settlements.First(s => s.Name == "Ceres Hub")), Is.True);

            w.Law.Player.Standing[w.Sol] = 0;
            w.Law.Player.Bounty[w.Sol] = Law.DockRefusedBounty;
            Assert.That(w.Economy.CanTrade(ship, earthPort), Is.False);
            // Paying the fine opens them again.
            ship.Faction.Treasury = 1e9;
            Assert.That(w.Law.PayFine(ship, w.Sol, StartJd, out double paid), Is.True);
            Assert.That(paid, Is.EqualTo(Law.DockRefusedBounty));
            Assert.That(w.Economy.CanTrade(ship, earthPort), Is.True);
        }

        [Test]
        public void FineToOwnFactionIsDebitedAndBurned()
        {
            var w = CreateWorld();
            var ship = Pilot(w);
            var own = ship.Faction;
            own.Treasury = 1e9;
            w.Law.Player.Bounty[own] = 5000;
            Assert.That(w.Law.PayFine(ship, own, StartJd, out double paid), Is.True);
            Assert.That(paid, Is.EqualTo(5000));
            Assert.That(own.Treasury, Is.EqualTo(1e9 - 5000));
            Assert.That(w.Law.Player.BountyBy(own), Is.EqualTo(0));
        }

        [Test]
        public void StandingMovesPricesAtAFactionsMarkets()
        {
            var w = CreateWorld();
            var ship = Pilot(w);
            ship.Faction.Treasury = 1e12;
            var port = w.Economy.Settlements.First(s => s.Name == "Earth Orbital");
            double before = ship.Faction.Treasury;
            Assert.That(w.Economy.BuyIntoHold(ship, port, Good.Food, 1, out _), Is.EqualTo(Economy.TradeOutcome.Ok));
            double neutral = before - ship.Faction.Treasury;
            ship.PersonalHold.Remove(ship.PersonalHold.Items[0]);

            w.Law.Player.Standing[w.Sol] = -20;
            Assert.That(Law.PriceFactor(w.Law.Player, w.Sol), Is.EqualTo(1 + 0.3 * 20 / 50).Within(1e-9));
            before = ship.Faction.Treasury;
            w.Economy.BuyIntoHold(ship, port, Good.Food, 1, out _);
            double distrusted = before - ship.Faction.Treasury;
            Assert.That(distrusted / neutral, Is.EqualTo(1.12).Within(0.02));

            w.Law.Player.Standing[w.Sol] = 100;
            Assert.That(Law.PriceFactor(w.Law.Player, w.Sol), Is.EqualTo(0.9).Within(1e-9));
            Assert.That(Law.PriceFactor(w.Law.Player, w.Pirates), Is.EqualTo(1), "a haven deals at its own prices");
        }

        [Test]
        public void AFactionThatWantsThePlayerOffersThemNoContracts()
        {
            var p = new LawRecord("you", null);
            var navy = new Faction("Navy", "NS", default) { IsMilitary = true };
            Assert.That(Law.OffersContracts(p, navy), Is.True);
            p.Bounty[navy] = 1000;
            Assert.That(Law.OffersContracts(p, navy), Is.False);
            p.Bounty.Clear();
            p.Standing[navy] = Law.ContractMinStanding - 1;
            Assert.That(Law.OffersContracts(p, navy), Is.False);

            // The event system asks before it offers a patron's work.
            var traders = new Faction("Free Traders", "MV", default);
            var merchant = new Ship { Id = 1, Name = "Ledger", Spec = ShipClassSpec.Of(ShipClass.Freighter), Faction = traders, Role = ShipRole.Trader, Phase = ShipPhase.Parked };
            merchant.Battle = new Battle(1, 3, "Battle of Body3", DVec3.Zero, StartJd, 1);
            var src = new EventSources { Ships = new[] { merchant }, ShipHelio = s => new DVec3(1e6, 0, 0), WillHire = f => f != traders };
            var director = new EventDirector(seed: 3) { AnomalyRatePerDay = 0 };
            director.Update(StartJd, src, DVec3.Zero, null);
            Assert.That(director.Active, Is.Empty);
            src.WillHire = f => true;
            director.Update(StartJd + 1, src, DVec3.Zero, null);
            Assert.That(director.Active.Single().Kind, Is.EqualTo(OpportunityKind.Distress));
        }

        // ---------------------------------------------------------------- contraband

        [Test]
        public void GoodsBoughtAtAHavenAreStolenAndNoLawfulMarketTakesThem()
        {
            var w = CreateWorld();
            var ship = Pilot(w, ShipClass.Gunship, w.Pirates);
            ship.Faction.Treasury = 1e12;
            var haven = w.Economy.Settlements.First(s => s.Name == "Blackwater");
            haven.Stock[(int)Good.Food] += 100;
            Assert.That(w.Economy.BuyIntoHold(ship, haven, Good.Food, 1, out _), Is.EqualTo(Economy.TradeOutcome.Ok));
            Assert.That(ship.PersonalHold.Items.Single().Stolen, Is.True);
            Assert.That(Law.ContrabandIn(ship, w.Sol).Count, Is.EqualTo(1));

            var lawful = Pilot(w, ShipClass.Freighter);
            lawful.PersonalHold.AddFirstFit(Good.Food).Stolen = true;
            var port = w.Economy.Settlements.First(s => s.Name == "Earth Orbital");
            Assert.That(w.Economy.SellFromHold(lawful, port, Good.Food, 1, out _), Is.EqualTo(Economy.TradeOutcome.Contraband));
            lawful.PersonalHold.AddFirstFit(Good.Food);
            Assert.That(w.Economy.SellFromHold(lawful, port, Good.Food, 2, out int sold), Is.EqualTo(Economy.TradeOutcome.Ok));
            Assert.That(sold, Is.EqualTo(1), "only the honest crate sells");
            Assert.That(lawful.PersonalHold.Items.Single().Stolen, Is.True);
            // A faction may ban a good outright.
            w.Sol.Contraband.Add(Good.Munitions);
            lawful.PersonalHold.AddFirstFit(Good.Munitions);
            Assert.That(Law.ContrabandIn(lawful, w.Sol).Count, Is.EqualTo(2));
            Assert.That(Law.ContrabandIn(lawful, w.Belt).Count, Is.EqualTo(1));
        }

        [Test]
        public void StolenGoodsSoldOnABlackMarketAreUntaxedAndPaidFromTheOwnersTreasury()
        {
            var w = CreateWorld();
            var ship = Pilot(w, ShipClass.Freighter);
            var port = w.Economy.Settlements.First(s => s.Name == "Earth Orbital");
            port.Lawless = true;
            ship.PersonalHold.AddFirstFit(Good.Food).Stolen = true;
            double owner = port.Owner.Treasury, mine = ship.Faction.Treasury;
            Assert.That(w.Economy.SellFromHold(ship, port, Good.Food, 1, out int sold), Is.EqualTo(Economy.TradeOutcome.Ok));
            Assert.That(sold, Is.EqualTo(1));
            double paid = ship.Faction.Treasury - mine;
            Assert.That(paid, Is.GreaterThan(0));
            Assert.That(port.Owner.Treasury, Is.EqualTo(owner - paid).Within(1e-6), "the owner pays the full value; no port tax");
        }

        // ---------------------------------------------------------------- patrols

        [Test]
        public void APatrolScansAShipInItsSpaceAndConfiscatesContraband()
        {
            var w = CreateWorld();
            ClearNavies(w);
            int earth = Body(w, "Earth");
            var ship = Pilot(w);
            var picket = Patrol(w, w.Sol, earth);
            // A Belt patrol at Earth is out of its own space: it does not stop anyone here.
            Patrol(w, w.Belt, earth);
            var offset = new DVec3(5000, 0, 0);
            var crate = ship.PersonalHold.AddFirstFit(Good.Platinum);
            crate.Stolen = true;
            ship.PersonalHold.AddFirstFit(Good.Food);

            w.Law.UpdatePilot(ship, earth, offset, DVec3.UnitX, StartJd);
            var hail = w.Law.Hail;
            Assert.That(hail, Is.Not.Null);
            Assert.That(hail.Patrol, Is.EqualTo(picket));
            Assert.That(hail.Interdiction, Is.False);
            w.Law.UpdatePilot(ship, earth, offset, DVec3.UnitX, StartJd + 0.5 * w.Law.ScanSeconds / Astro.SecondsPerDay);
            Assert.That(w.Law.Hail, Is.SameAs(hail), "still scanning");
            w.Law.UpdatePilot(ship, earth, offset, DVec3.UnitX, StartJd + 1.1 * w.Law.ScanSeconds / Astro.SecondsPerDay);
            Assert.That(w.Law.Hail, Is.Null);
            Assert.That(ship.PersonalHold.Items.Single().Good, Is.EqualTo(Good.Food), "the stolen platinum is gone");
            Assert.That(w.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Smuggling));
            Assert.That(w.Law.Player.WantedBy(w.Sol), Is.True);
            Assert.That(w.Law.TakeNotices().Any(n => n.Contains("contraband")), Is.True);

            // Not scanned again for a while.
            w.Law.UpdatePilot(ship, earth, offset, DVec3.UnitX, StartJd + 0.01);
            Assert.That(w.Law.Hail, Is.Null);
        }

        [Test]
        public void RunningFromAScanIsACrime()
        {
            var w = CreateWorld();
            ClearNavies(w);
            int earth = Body(w, "Earth");
            var ship = Pilot(w);
            Patrol(w, w.Sol, earth);
            w.Law.UpdatePilot(ship, earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd);
            Assert.That(w.Law.Hail, Is.Not.Null);
            w.Law.UpdatePilot(ship, earth, new DVec3(w.Law.PatrolHoldKm * 1.5, 0, 0), DVec3.UnitX, StartJd + 1.0 / Astro.SecondsPerDay);
            Assert.That(w.Law.Hail, Is.Null);
            Assert.That(w.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.FleeingScan));
        }

        [Test]
        public void AWantedShipIsHailedToPayUpOrBeInterdicted()
        {
            var w = CreateWorld();
            ClearNavies(w);
            int earth = Body(w, "Earth");
            var ship = Pilot(w);
            Patrol(w, w.Sol, earth);
            w.Law.Commit(w.Law.Player, CrimeKind.Assault, w.Sol, w.Sol, earth, StartJd, "Earth Orbital");
            var offset = new DVec3(5000, 0, 0);

            // Paying the fine settles it.
            w.Law.UpdatePilot(ship, earth, offset, DVec3.UnitX, StartJd);
            Assert.That(w.Law.Hail.Interdiction, Is.True);
            Assert.That(w.Law.Hail.Fine, Is.EqualTo(Law.Severity(CrimeKind.Assault).bounty));
            ship.Faction.Treasury = 1e9;
            double treasury = w.Sol.Treasury;
            Assert.That(w.Law.PayFine(ship, w.Sol, StartJd, out double paid), Is.True);
            Assert.That(w.Sol.Treasury, Is.EqualTo(treasury + paid));
            Assert.That(w.Law.Hail, Is.Null);
            Assert.That(w.Law.Player.Wanted, Is.False);

            // Ignored, the hail becomes an interdiction: a fight with the patrol enforcing the law.
            var w2 = CreateWorld();
            ClearNavies(w2);
            var ship2 = Pilot(w2);
            var picket = Patrol(w2, w2.Sol, earth);
            w2.Law.Commit(w2.Law.Player, CrimeKind.Assault, w2.Sol, w2.Sol, earth, StartJd, "Earth Orbital");
            w2.Law.UpdatePilot(ship2, earth, offset, DVec3.UnitX, StartJd);
            Assert.That(w2.Law.Hail, Is.Not.Null);
            w2.Law.UpdatePilot(ship2, earth, offset, DVec3.UnitX, StartJd + 1.1 * w2.Law.HailSeconds / Astro.SecondsPerDay);
            Assert.That(ship2.Battle, Is.Not.Null);
            Assert.That(picket.Battle, Is.SameAs(ship2.Battle));
            Assert.That(ship2.Battle.EnforcingSide, Is.EqualTo(ship2.Battle.UnitOf(picket).Side));
            Assert.That(ship2.Battle.UnitOf(ship2).PlayerControlled, Is.True);
        }

        [Test]
        public void FlyingIntoAPortsSpaceWhileBarredIsTrespass()
        {
            var w = CreateWorld();
            ClearNavies(w);
            int earth = Body(w, "Earth");
            var ship = Pilot(w);
            w.Law.Player.Standing[w.Sol] = Law.DockMinStanding - 5;
            w.Law.UpdatePilot(ship, earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd);
            Assert.That(w.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Trespass));
            Assert.That(w.Law.Player.Crimes.Single().Witness, Is.EqualTo("Earth Orbital"));
            // Charged once a day, not every frame.
            w.Law.UpdatePilot(ship, earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd + 0.1);
            Assert.That(w.Law.Player.Crimes.Count, Is.EqualTo(1));
            // Far out, it is nobody's port.
            var w2 = CreateWorld();
            var ship2 = Pilot(w2);
            w2.Law.Player.Standing[w2.Sol] = Law.DockMinStanding - 5;
            w2.Law.UpdatePilot(ship2, earth, new DVec3(1e9, 0, 0), DVec3.UnitX, StartJd);
            Assert.That(w2.Law.Player.Crimes, Is.Empty);
        }

        [Test]
        public void ThePlayerCanOpenFireOnALawfulShipAndAnswersForItWhenTheFightEnds()
        {
            var w = CreateWorld();
            int earth = Body(w, "Earth");
            w.War.Battles.Clear();
            var ship = Pilot(w, ShipClass.Cruiser);
            var target = w.Fleet.AddShip("Honest Toil", ShipClass.Freighter, w.Belt, ShipRole.Trader);
            target.Orbit = new ParkingOrbit { Body = earth };
            target.Phase = ShipPhase.Parked;
            Assert.That(w.Diplomacy.Hostile(ship.Faction, target.Faction), Is.False);
            var b = w.War.TryStartPlayerAttack(ship, target, earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd);
            Assert.That(b, Is.Not.Null);
            var mine = b.UnitOf(ship);
            var theirs = b.UnitOf(target);
            Assert.That(mine.PlayerControlled, Is.True);
            Assert.That(theirs.Side, Is.EqualTo(1 - mine.Side));
            // Earth Orbital's platforms defend Federation space, not a Belt hull.
            Assert.That(b.Units.Any(u => u.Kind == UnitKind.Platform), Is.False);
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.Player.Crimes.Single().Kind, Is.EqualTo(CrimeKind.Assault));
            Assert.That(w.Law.Player.WantedBy(w.Sol), Is.True);
            Assert.That(w.Law.Player.StandingWith(w.Belt), Is.LessThan(0));
        }

        [Test]
        public void ThePlayerCanOpenFireOnALoneEnemyMerchantWithNobodyArmedThereToStartAFight()
        {
            var w = CreateWorld();
            ClearNavies(w);
            int earth = Body(w, "Earth");
            var ship = Pilot(w, ShipClass.Cruiser, w.Sol);
            var target = w.Fleet.AddShip("Far Trader", ShipClass.Freighter, w.Reach, ShipRole.Trader);
            target.Orbit = new ParkingOrbit { Body = earth };
            target.Phase = ShipPhase.Parked;
            w.Diplomacy.Set(w.Sol, w.Reach, Relation.War);
            Assert.That(w.War.TryStartPlayerBattle(ship, earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd), Is.Null, "no warship or platform of its own to start a fight");
            var b = w.War.TryStartPlayerAttack(ship, target, earth, new DVec3(5000, 0, 0), DVec3.UnitX, StartJd);
            Assert.That(b, Is.Not.Null);
            Assert.That(b.UnitOf(target).Side, Is.EqualTo(1 - b.UnitOf(ship).Side));
            // An enemy at war is no lawful victim.
            w.Law.OnBattleEnded(b, StartJd);
            Assert.That(w.Law.Player.Crimes, Is.Empty);
        }

        // ---------------------------------------------------------------- the books over a long game (U.SPC-T321)

        [Test]
        public void ACaptainsRecordWithNothingOwedIsDroppedOnceItsShipIsGoneForGood()
        {
            var w = CreateWorld();
            var ship = Hull(ShipClass.Destroyer, w.Pirates, 501, "Gutter Knife");
            w.Law.RecordOf(ship);
            Assert.That(w.Law.Captains.ContainsKey(ship), Is.True, "opening the record never booked it");
            ship.Destroyed = true;
            w.Law.Update(StartJd);
            w.Law.Update(StartJd + 1);
            Assert.That(w.Law.Captains.ContainsKey(ship), Is.False, "a dead captain's clean record was never dropped from the books");
        }

        [Test]
        public void AWantedCaptainsRecordStaysOnTheBooksEvenIfItsShipIsGone()
        {
            var w = CreateWorld();
            int mars = Body(w, "Mars");
            var ship = Hull(ShipClass.Destroyer, w.Pirates, 502, "Black Ledger");
            var rec = w.Law.RecordOf(ship);
            w.Law.Commit(rec, CrimeKind.Piracy, w.Sol, w.Belt, mars, StartJd, "Ares Prime");
            ship.Destroyed = true;
            w.Law.Update(StartJd);
            w.Law.Update(StartJd + 1);
            Assert.That(w.Law.Captains.ContainsKey(ship), Is.True, "a bounty still owed was dropped off the books with its ship");
        }
    }
}
