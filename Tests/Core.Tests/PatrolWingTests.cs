using System;
using System.Collections.Generic;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Patrolled space and commanded combat (U.SPC-T371): every navy flies standing patrol wings of light and heavy fighters
    /// and destroyers round a beat of its ports, dozens of hulls in all, and a fight is opened only by a group commanded to
    /// fight there (its own ports, its beat, its order's objective), not by whatever hostile happens to be in reach.
    /// </summary>
    [TestFixture]
    public class PatrolWingTests
    {
        const double StartJd = 2461324.0;

        static World CreateWorld()
        {
            var eph = new Ephemeris(BodyCatalog.CreateSolSystem());
            eph.Update(StartJd);
            var w = World.CreateDefault(eph, StartJd);
            w.War.Battles.Clear();
            return w;
        }

        /// <summary>The wings' leaders (not the supply convoys that fly as wings do, U.SPC-T375, nor the corvettes, U.SPC-T476).</summary>
        static List<Ship> Wings(World w) => w.Fleet.Ships.Where(s => s.PatrolWing && !s.Destroyed && !s.IsEscort && !s.Convoy && !Corvettes.Stationed(s)).ToList();

        static HashSet<int> PortsOf(World w, Faction f) =>
            new HashSet<int>(w.Economy.Settlements.Where(s => s.Owner == f && s.Kind != SettlementKind.Haven).Select(s => s.Body));

        /// <summary>A visible world with nobody's port at it: open ground, nobody's to defend.</summary>
        static int OpenGround(World w)
        {
            var settled = new HashSet<int>(w.Economy.Settlements.Select(s => s.Body));
            foreach (int b in w.FreeShipPorts())
                if (!settled.Contains(b) && w.War.BattleAt(b) == null && !w.Fleet.Ships.Any(s => !s.Destroyed && s.Phase == ShipPhase.Parked && s.Orbit?.Body == b))
                    return b;
            Assert.Fail("no open ground in Sol");
            return -1;
        }

        static Ship Group(World w, Faction f, int body, string name)
        {
            var leader = w.Fleet.AddShip(name, ShipClass.Destroyer, f, ShipRole.Patrol);
            leader.Route = new[] { w.Catalog["Earth"].Index };
            var escort = w.Fleet.AddShip(name + " Two", ShipClass.Fighter, f, ShipRole.Escort);
            w.Fleet.AddEscort(leader, escort, Fleet.SlotFor(0));
            w.Fleet.Station(leader, body, StartJd);
            return leader;
        }

        [Test]
        public void SolIsPatrolledByDozensOfLightAndHeavyFightersAndWarships()
        {
            var w = CreateWorld();
            var all = w.Fleet.Ships.Where(s => s.PatrolWing && !s.Destroyed).ToList();
            Assert.That(all.Count, Is.GreaterThanOrEqualTo(24), "Sol's navies fly too few patrol hulls to fill the space between the worlds");
            Assert.That(all.Count(s => s.Spec.Class == ShipClass.Fighter), Is.GreaterThanOrEqualTo(12), "too few light fighters on patrol");
            Assert.That(all.Any(s => s.Spec.Class == ShipClass.Gunship), Is.True, "no heavy fighter on patrol");
            Assert.That(all.Any(s => s.Spec.Class == ShipClass.Destroyer), Is.True, "no destroyer leads a patrol wing");
            foreach (var s in all)
            {
                Assert.That(s.Faction.IsMilitary && !s.Faction.IsPirate, Is.True, $"{s.Name} flies patrol for {s.Faction.Name}, which has no navy");
                Assert.That(s.IsEscort ? s.Leader.PatrolWing : s.Route != null && s.Route.Length > 0, Is.True, $"{s.Name} is not on a wing's beat");
            }
            // The freighters the patrols watch over are there in their dozens too.
            Assert.That(w.Fleet.Ships.Count(s => !s.Destroyed && s.Spec.Class == ShipClass.Freighter), Is.GreaterThanOrEqualTo(24));
        }

        [Test]
        public void EachNavysWingsBetweenThemPatrolEveryOneOfItsPorts()
        {
            var w = CreateWorld();
            foreach (var f in w.Fleet.Factions.Where(f => f.IsMilitary && !f.IsPirate))
            {
                var ports = PortsOf(w, f);
                if (ports.Count == 0) continue;
                var wings = Wings(w).Where(s => s.Faction == f).ToList();
                Assert.That(wings.Count, Is.EqualTo(PatrolWings.WingsFor(PatrolWings.Ports(w, f).Count)), $"{f.Name} flies the wrong number of wings");
                var beats = new HashSet<int>(wings.SelectMany(s => s.Route));
                Assert.That(beats.IsSubsetOf(ports), Is.True, $"a {f.Name} wing's beat strays off its own ports");
                Assert.That(beats.IsSupersetOf(PatrolWings.Ports(w, f).Take(Math.Min(ports.Count, wings.Count * PatrolWings.BeatLength))), Is.True,
                    $"{f.Name}'s wings leave a port unpatrolled");
            }
        }

        [Test]
        public void WingsKeepToTheirBeatsAndAreNeverMergedSentAwayOrCountedAsTheBattleFleet()
        {
            var w = CreateWorld();
            int before = Wings(w).Count;
            for (int day = 1; day <= 20; day++) w.Update(StartJd + day);
            foreach (var s in w.Fleet.Ships.Where(s => !s.Destroyed && s.IsEscort))
                Assert.That(s.PatrolWing, Is.EqualTo(s.Leader.PatrolWing), $"{s.Name} was merged across a wing and a battle group");
            foreach (var leader in Wings(w))
            {
                Assert.That(leader.Voyage, Is.Null, $"{leader.Name} was sent through a gate");
                Assert.That(w.War.IsHomeGuard(leader), Is.False, $"{leader.Name} stands as a navy's home guard");
                var o = w.War.OrderOf(leader);
                if (o == null) continue;
                Assert.That(o.Kind, Is.AnyOf(OrderKind.Patrol, OrderKind.Defend, OrderKind.Hunt, OrderKind.Repair, OrderKind.Ambush), $"{leader.Name} was ordered to {o.Kind}");
                if (o.Kind != OrderKind.Repair)
                    Assert.That(PatrolWings.OnBeat(leader, o.Body), Is.True, $"{leader.Name} was sent off its beat to {w.Catalog[o.Body].Name}");
            }
            Assert.That(Wings(w).Count, Is.GreaterThan(before / 2), "the wings did not last three weeks");
        }

        [Test]
        public void AWingHearingOfAnEnemyAtAPortOnItsBeatGoesToDefendIt()
        {
            var w = CreateWorld();
            var wing = Wings(w).First(s => s.Faction == w.Sol && s.Route.Length >= 2);
            int here = wing.Route[0], threatened = wing.Route[1];
            Group(w, w.Reach, threatened, "Interloper");
            int next = w.War.ChooseNext(wing, here, StartJd);
            Assert.That(next, Is.EqualTo(threatened));
            Assert.That(w.War.OrderOf(wing).Kind, Is.EqualTo(OrderKind.Defend));
        }

        [Test]
        public void HostileGroupsMeetingOnOpenGroundFightOnlyIfOneIsOrderedToFightThere()
        {
            var w = CreateWorld();
            int open = OpenGround(w);
            int mars = w.Catalog["Mars"].Index;
            var sol = Group(w, w.Sol, open, "Passerby");
            var reach = Group(w, w.Reach, open, "Transient");
            Assert.That(w.Diplomacy.Hostile(w.Sol, w.Reach), Is.True);
            // Both bound elsewhere: neither is commanded to fight here, so they pass each other by.
            w.War.Orders[sol] = new GroupOrder { Kind = OrderKind.Patrol, Body = w.Catalog["Earth"].Index };
            w.War.Orders[reach] = new GroupOrder { Kind = OrderKind.Attack, Body = mars };
            Assert.That(w.War.Commanded(reach, open), Is.False);
            Assert.That(w.War.TryStartBattle(open, w.Reach, StartJd), Is.Null, "a group passing through picked a fight it was not ordered to");
            Assert.That(w.War.TryStartBattle(open, w.Sol, StartJd), Is.Null, "a group off its beat picked a fight it was not ordered to");
            w.War.OnArrived(reach, StartJd);
            Assert.That(reach.Battle, Is.Null, "arriving beside an enemy started a fight neither side was ordered to");
            // Ordered to take the ground: now it is a fight, and the enemy defends itself.
            w.War.Orders[reach] = new GroupOrder { Kind = OrderKind.Attack, Body = open };
            var b = w.War.TryStartBattle(open, w.Reach, StartJd);
            Assert.That(b, Is.Not.Null, "a group ordered to attack here did not engage");
            Assert.That(sol.Battle, Is.SameAs(b), "the group set upon was not swept into the fight to defend itself");
        }

        [Test]
        public void AGroupAlwaysDefendsItsOwnPortsAndItsBeat()
        {
            var w = CreateWorld();
            var leader = Wings(w).First(s => s.Faction == w.Sol);
            foreach (int b in leader.Route) Assert.That(w.War.Commanded(leader, b), Is.True, "a wing is not commanded to fight on its own beat");
            int open = OpenGround(w);
            Assert.That(w.War.Commanded(leader, open), Is.False, "a wing is commanded to fight off its beat");
            foreach (int p in PortsOf(w, w.Sol))
                Assert.That(w.War.Commanded(leader, p), Is.True, "a group may not defend its own navy's port");
        }

        [Test]
        public void ARaiderRunsThePlayerDownOnlyOnTheGroundItIsOrderedToRaid()
        {
            var w = CreateWorld();
            int earth = w.Catalog["Earth"].Index;
            var offset = new DVec3(60000, 0, 0);
            var pilot = w.Fleet.AddShip("Runner", ShipClass.Destroyer, w.Independents, ShipRole.Free);
            Fleet.PinAt(pilot, earth, offset, DVec3.UnitX, StartJd);
            w.PilotedShip = pilot;
            var raider = w.Fleet.AddShip("Gutter Knife", ShipClass.Destroyer, w.Pirates, ShipRole.Patrol);
            raider.Route = new[] { w.Catalog["Mars"].Index };
            w.Fleet.Station(raider, earth, StartJd);
            w.War.Orders[raider] = new GroupOrder { Kind = OrderKind.Raid, Body = w.Catalog["Mars"].Index };
            for (int i = 0; i < 100; i++)
                Assert.That(w.Pursuit.Consider(pilot, earth, offset, DVec3.Zero, StartJd).Any(g => g.Leader == raider), Is.False,
                    "a raider bound to raid Mars turned aside to run the player down at Earth");
            w.War.Orders[raider] = new GroupOrder { Kind = OrderKind.Raid, Body = earth };
            Pursuer chase = null;
            for (int i = 0; i < 100 && chase == null; i++)
                chase = w.Pursuit.Consider(pilot, earth, offset, DVec3.Zero, StartJd).FirstOrDefault(g => g.Leader == raider);
            Assert.That(chase, Is.Not.Null, "a raider working Earth never ran the player down there");
        }

        [Test]
        public void EveryWingsBeatIsUpToThreePortsAndTheWingsSpreadOverThemAll()
        {
            var ports = Enumerable.Range(10, 7).ToList();
            int wings = PatrolWings.WingsFor(ports.Count);
            Assert.That(wings, Is.EqualTo(PatrolWings.MaxWingsPerFaction));
            var covered = new HashSet<int>();
            for (int i = 0; i < wings; i++)
            {
                var beat = PatrolWings.Beat(ports, i, wings);
                Assert.That(beat.Length, Is.EqualTo(PatrolWings.BeatLength));
                Assert.That(beat.Distinct().Count(), Is.EqualTo(beat.Length));
                covered.UnionWith(beat);
            }
            Assert.That(covered.SetEquals(ports), Is.True, "the wings' beats leave a port out");
            Assert.That(PatrolWings.WingsFor(0), Is.EqualTo(0));
            Assert.That(PatrolWings.WingsFor(1), Is.EqualTo(0), "a navy with one port flies a wing with nowhere to patrol");
            Assert.That(PatrolWings.Beat(new List<int> { 5 }, 0, 1), Is.EqualTo(new[] { 5 }));
        }
    }
}
