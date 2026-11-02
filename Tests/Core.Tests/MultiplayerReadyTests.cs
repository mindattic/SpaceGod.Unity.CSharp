using System;
using System.Collections.Generic;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Multiplayer-ready groundwork (U.SPC-T410, docs/BIBLE.md U.SPC-LAW-1, per Unity.Multiplayer's multiplayer-ready
    /// standard): two players drive the game through one <see cref="LocalSession"/> with commands stamped by their ids, each
    /// flies only the craft they hold, a command from the wrong player is refused, and the command log serializes and
    /// replays to the same fight.
    /// </summary>
    [TestFixture]
    public class MultiplayerReadyTests
    {
        static double AngleDeg(DVec3 a, DVec3 b) => Math.Acos(Math.Max(-1, Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)))) * Astro.Rad2Deg;

        static Ship Hull(ShipClass c, Faction f, int id) => new Ship
        {
            Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c),
        };

        /// <summary>A carrier's squadrons 28 km off a battleship, with every gun silenced so nothing is shot down mid-test;
        /// the leads of squadrons 0 and 1 are the two seats.</summary>
        static (Battle b, BattleUnit a, BattleUnit c) TwoCraft()
        {
            var blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), 2461324.0, 5) { Launch = FighterAI.Launch, FighterStep = FighterAI.Step };
            var carrier = b.AddShip(Hull(ShipClass.Carrier, blue, 0), 0, new DVec3(14, 0, 0), -DVec3.UnitX);
            var enemy = b.AddShip(Hull(ShipClass.Battleship, red, 1), 1, new DVec3(-14, 0, 0), DVec3.UnitX);
            foreach (var u in b.Units)
            foreach (var s in u.Combat.Subsystems)
                if (s.Def.IsWeapon) s.Hp = 0;
            BattleUnit Lead(int squadron) => b.Units.First(u => u.Kind == UnitKind.Fighter && u.Fighter.Squadron == squadron && u.Fighter.Lead == null);
            return (b, Lead(0), Lead(1));
        }

        static void Run(LocalSession session, Battle b, int ticks)
        {
            for (int i = 0; i < ticks && !b.Over; i++)
            {
                session.Step();
                b.Advance(LocalSession.TickSeconds);
            }
        }

        [Test]
        public void TwoPlayersEachFlyTheirOwnCraftAndAWrongPlayersCommandIsRefused()
        {
            var (b, craftA, craftB) = TwoCraft();
            var session = LocalSession.Create(seed: 7, players: 2);
            var ann = session.Players[0];
            var bob = session.Players[1];
            Assert.That(session.LocalPlayer, Is.EqualTo(ann), "this machine plays the first seat by default");
            var refused = new List<CommandRefused>();
            session.EventRaised += e => { if (e is CommandRefused r) refused.Add(r); };

            int seatA = session.IdOf(Embodiables.FighterOf(b, craftA));
            int seatB = session.IdOf(Embodiables.FighterOf(b, craftB));
            Assert.That(seatA, Is.Not.EqualTo(seatB));
            Assert.That(session.IdOf(Embodiables.FighterOf(b, craftA)), Is.EqualTo(seatA), "a seat keeps its id");

            session.Submit(new TakeSeatCommand(ann, seatA));
            session.Submit(new TakeSeatCommand(bob, seatB));
            session.Step();
            Assert.That(session.State.SeatIdOf(ann), Is.EqualTo(seatA));
            Assert.That(session.State.SeatIdOf(bob), Is.EqualTo(seatB));
            Assert.That(session.State.HolderOf(seatA), Is.EqualTo(ann));
            Assert.That(b.Flown.ContainsKey(craftA) && b.Flown.ContainsKey(craftB), Is.True, "both craft are flown by hand");

            // The wrong player: Bob reaching for Ann's craft, a seat not in the session, nobody at all.
            session.Submit(new TakeSeatCommand(bob, seatA));
            session.Submit(new TakeSeatCommand(new PlayerId(5), seatA));
            session.Submit(new ReleaseSeatCommand(PlayerId.None));
            session.Step();
            Assert.That(refused.Count, Is.EqualTo(3));
            Assert.That(refused[0].Player, Is.EqualTo(bob));
            Assert.That(session.State.HolderOf(seatA), Is.EqualTo(ann), "Ann keeps her craft");
            Assert.That(session.State.SeatIdOf(bob), Is.EqualTo(seatB), "and Bob his");

            // Each points their own stick a different way: each craft turns to its own player's, not the other's.
            var goalA = PilotFlight.RotateAbout(craftA.Forward, DVec3.UnitZ, 90 * Astro.Deg2Rad);
            var goalB = PilotFlight.RotateAbout(craftB.Forward, DVec3.UnitZ, -90 * Astro.Deg2Rad);
            session.Submit(new SeatControlCommand(ann, new EmbodimentInput { Aim = goalA, Throttle = 1 }));
            session.Submit(new SeatControlCommand(bob, new EmbodimentInput { Aim = goalB, Throttle = 1 }));
            Run(session, b, 10 * LocalSession.TicksPerSecond);
            Assert.That(AngleDeg(craftA.Forward, goalA), Is.LessThan(1), "Ann's craft is on Ann's stick");
            Assert.That(AngleDeg(craftB.Forward, goalB), Is.LessThan(1), "Bob's craft is on Bob's stick");
            Assert.That(session.State.Tick, Is.EqualTo(2 + 10 * LocalSession.TicksPerSecond));

            // Bob lets go: his craft goes back to its AI, Ann's stays in her hands.
            session.Submit(new ReleaseSeatCommand(bob));
            session.Step();
            Assert.That(session.State.SeatOf(bob), Is.Null);
            Assert.That(b.Flown.ContainsKey(craftB), Is.False);
            Assert.That(b.Flown.ContainsKey(craftA), Is.True);
            Assert.That(session.State.HolderOf(seatA), Is.EqualTo(ann));
        }

        [Test]
        public void ACommandFromAPlayerHoldingNothingIsRefused()
        {
            var session = LocalSession.Create(seed: 1, players: 2);
            var refused = new List<CommandRefused>();
            session.EventRaised += e => { if (e is CommandRefused r) refused.Add(r); };
            session.Submit(new SeatControlCommand(session.Players[1], new EmbodimentInput { Throttle = 1 }));
            session.Submit(new MoveToHostCommand(session.Players[1]));
            session.Submit(new TakeSeatCommand(session.Players[0], 99));
            session.Step();
            Assert.That(refused.Select(r => r.Player), Is.EqualTo(new[] { session.Players[1], session.Players[1], session.Players[0] }));
        }

        [Test]
        public void EveryCommandRoundTripsThroughTheCodec()
        {
            var commands = new IGameCommand[]
            {
                new TakeSeatCommand(new PlayerId(0), 3),
                new ReleaseSeatCommand(new PlayerId(1)),
                new MoveToHostCommand(new PlayerId(1)),
                new SeatControlCommand(new PlayerId(1), new EmbodimentInput
                {
                    Aim = new DVec3(0.1, -0.7, 1.0 / 3), Throttle = -0.25, StrafeRight = 1, StrafeUp = -1, Roll = 0.123456789012345,
                    Primary = true, Boost = true,
                }),
            };
            var text = CommandCodec.WriteLog(commands);
            var back = CommandCodec.ReadLog(text);
            Assert.That(back.Count, Is.EqualTo(commands.Length));
            Assert.That(CommandCodec.WriteLog(back), Is.EqualTo(text));
            for (int i = 0; i < commands.Length; i++)
            {
                Assert.That(back[i].GetType(), Is.EqualTo(commands[i].GetType()));
                Assert.That(back[i].Player, Is.EqualTo(commands[i].Player));
            }
            var c = (SeatControlCommand)back[3];
            var o = ((SeatControlCommand)commands[3]).Input;
            Assert.That(c.Input.Aim, Is.EqualTo(o.Aim));
            Assert.That(c.Input.Roll, Is.EqualTo(o.Roll));
            Assert.That(c.Input.Throttle, Is.EqualTo(o.Throttle));
            Assert.That((c.Input.Primary, c.Input.Secondary, c.Input.Boost), Is.EqualTo((true, false, true)));
            Assert.That(((TakeSeatCommand)back[0]).Seat, Is.EqualTo(3));
        }

        [Test]
        public void TheSeedAndTheCommandLogReplayTheSameFight()
        {
            (DVec3 a, DVec3 b) Play(string log, out string written)
            {
                var (battle, craftA, craftB) = TwoCraft();
                var session = LocalSession.Create(seed: 3, players: 2);
                session.Logging = true;
                int seatA = session.IdOf(Embodiables.FighterOf(battle, craftA));
                int seatB = session.IdOf(Embodiables.FighterOf(battle, craftB));
                if (log == null)
                {
                    session.Submit(new TakeSeatCommand(session.Players[0], seatA));
                    session.Submit(new TakeSeatCommand(session.Players[1], seatB));
                    session.Submit(new SeatControlCommand(session.Players[0], new EmbodimentInput { Aim = DVec3.UnitZ, Throttle = 1, Boost = true }));
                    session.Submit(new SeatControlCommand(session.Players[1], new EmbodimentInput { Aim = -DVec3.UnitY, Throttle = 0.5, StrafeRight = 1 }));
                }
                else
                    foreach (var c in CommandCodec.ReadLog(log)) session.Submit(c);
                Run(session, battle, 4 * LocalSession.TicksPerSecond);
                written = CommandCodec.WriteLog(session.Log);
                return (craftA.Pos, craftB.Pos);
            }

            var first = Play(null, out string log);
            var replay = Play(log, out string again);
            Assert.That(again, Is.EqualTo(log));
            Assert.That(replay.a, Is.EqualTo(first.a), "Ann's craft ends where it did");
            Assert.That(replay.b, Is.EqualTo(first.b), "and Bob's");
            Assert.That(first.a, Is.Not.EqualTo(first.b));
        }
    }
}
