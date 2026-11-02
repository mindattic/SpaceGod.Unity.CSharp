using System;
using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The dogfight arsenal (U.SPC-T271): autocannons and light railguns as fittable guns, EMP rounds that stun
    /// rather than hurt, proximity mines laid astern, and auto turrets on a hull's own hardpoints that fight on their
    /// own (U.SPC-T278).
    /// </summary>
    [TestFixture]
    public class ArsenalTests
    {
        const double StartJd = 2461324.0;

        static Ship Hull(ShipClass c, Faction f, int id, ModuleKind? utility = null, bool spineTurret = false)
        {
            var spec = CombatSpec.Of(c);
            if (spineTurret)
            {
                // An auto turret on the spine beside the hull's own guns, covering it from fighters (U.SPC-T278).
                spec = spec.Clone();
                spec.Layout = spec.Layout.Concat(new[] { new SubsystemDef(SubsystemKind.AutoTurret, 0, 0.06, -0.2) }).ToArray();
            }
            if (utility is ModuleKind m)
            {
                // The hull with its utility mount fitted with that module instead of its stock one.
                var l = new Loadout(spec, c, 0);
                l.Fitted[l.Slots.FindIndex(s => s.Type == MountType.Missile)] = m;
                spec = l.Build();
            }
            return new Ship { Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(spec, c) };
        }

        static (Battle b, BattleUnit mine, BattleUnit theirs) Duel(ShipClass mine, ShipClass theirs, DVec3 theirPos, bool player = true, ModuleKind? utility = null,
            bool spineTurret = false)
        {
            var blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, 1);
            b.AddShip(Hull(mine, blue, 0, utility, spineTurret), 0, DVec3.Zero, DVec3.UnitX);
            b.AddShip(Hull(theirs, red, 1), 1, theirPos, -DVec3.UnitX);
            b.Units[0].PlayerControlled = player;
            return (b, b.Units[0], b.Units[1]);
        }

        static double Health(BattleUnit u) => u.Combat.Hull + u.Combat.Subsystems.Sum(s => s.Hp + s.Shield);

        // ------------------------------------------------------------ fitting

        [Test]
        public void SmallCraftCarryASmallBallisticMountAndASmallMissileMountThatTakeTheNewArsenal()
        {
            foreach (var c in new[] { ShipClass.Fighter, ShipClass.Courier })
            {
                var l = new Loadout(CombatSpec.Of(c), c, 0);
                int gun = l.Slots.FindIndex(s => s.Type == MountType.Ballistic);
                int utility = l.Slots.FindIndex(s => s.Type == MountType.Missile);
                Assert.That(gun, Is.GreaterThanOrEqualTo(0), c + " has no ballistic mount");
                Assert.That(utility, Is.GreaterThanOrEqualTo(0), c + " has no missile mount");
                Assert.That(l.Slots[gun].Size, Is.EqualTo(HardpointSize.Small));
                Assert.That(Outfitting.CanFit(l, gun, ModuleKind.LightRailgun), Is.True);
                Assert.That(Outfitting.CanFit(l, gun, ModuleKind.AutoTurret), Is.True);
                Assert.That(Outfitting.CanFit(l, gun, ModuleKind.Railgun), Is.False, "a capital railgun is too big for a fighter's gun mount");
                Assert.That(Outfitting.CanFit(l, gun, ModuleKind.LaserTurret), Is.False, "a laser goes on an energy mount, not a ballistic one");
                foreach (var m in new[] { ModuleKind.EmpLauncher, ModuleKind.MineLayer, ModuleKind.MissileLauncher })
                    Assert.That(Outfitting.CanFit(l, utility, m), Is.True, c + " missile mount refused " + m);
                Assert.That(Outfitting.CanFit(l, utility, ModuleKind.AutoTurret), Is.False, "an auto turret is a gun: a missile mount takes none");
            }
            var destroyer = new Loadout(CombatSpec.Of(ShipClass.Destroyer), ShipClass.Destroyer, 0);
            int flak = destroyer.Slots.FindIndex(s => s.Stock == ModuleKind.FlakCannon);
            Assert.That(Outfitting.CanFit(destroyer, flak, ModuleKind.Autocannon), Is.True, "a warship's small ballistic mount takes an autocannon");
            var cruiser = new Loadout(CombatSpec.Of(ShipClass.Cruiser), ShipClass.Cruiser, 0);
            int pd = cruiser.Slots.FindIndex(s => s.Stock == ModuleKind.PointDefense);
            Assert.That(Outfitting.CanFit(cruiser, pd, ModuleKind.Autocannon), Is.False, "a point-defence mount is an energy mount");
        }

        [Test]
        public void ALightRailgunFittedToAFighterFiresHarderSlowerSlugs()
        {
            var l = new Loadout(CombatSpec.Of(ShipClass.Fighter), ShipClass.Fighter, 0);
            int gun = l.Slots.FindIndex(s => s.Type == MountType.Ballistic);
            l.Fitted[gun] = ModuleKind.LightRailgun;
            var spec = l.Build();
            Assert.That(spec.Layout.Count(d => d.Kind == SubsystemKind.LightRailgun), Is.EqualTo(1));
            Assert.That(WeaponSpec.LightRailgun.Damage, Is.GreaterThan(WeaponSpec.Autocannon.Damage * 4));
            Assert.That(WeaponSpec.LightRailgun.RangeKm, Is.GreaterThan(WeaponSpec.Autocannon.RangeKm * 3));
        }

        [Test]
        public void AMineLayerDoesNotMakeAHullAWarship()
        {
            // A truss freighter carries no weapons and no mines (U.SPC-T482); bolted onto one, a mine layer still leaves it unarmed.
            var spec = CombatSpec.Of(ShipClass.Freighter).Clone();
            spec.Layout = spec.Layout.Concat(new[] { new SubsystemDef(SubsystemKind.MineLayer, 0, -0.04, -0.45, facesAft: true) }).ToArray();
            var c = new ShipCombat(spec, ShipClass.Freighter);
            Assert.That(c.Subsystems.Any(s => s.Kind == SubsystemKind.MineLayer), Is.True);
            Assert.That(c.Armed, Is.False, "mines are a way to get away, not to fight");
        }

        // ------------------------------------------------------------ EMP

        [Test]
        public void AnEmpRoundStunsASmallCraftAndHalvesItsShieldsWithoutHurtingIt()
        {
            var (b, me, them) = Duel(ShipClass.Courier, ShipClass.Fighter, new DVec3(2, 0, 0));
            double hull = them.Combat.Hull;
            double shields = them.Combat.Subsystems.Sum(s => s.Shield);
            me.PlayerAim = DVec3.UnitX;
            me.PlayerEmpHeld = true;
            for (int i = 0; i < 60 && them.Stunned <= 0; i++)
            {
                them.Pos = new DVec3(2, 0, 0);
                b.Advance(0.1);
            }
            Assert.That(them.Stunned, Is.GreaterThan(WeaponSpec.Emp.EmpSeconds * 0.8), "the EMP round never stunned the fighter it was fired at");
            Assert.That(them.Combat.Hull, Is.EqualTo(hull), "an EMP round hurt the hull");
            Assert.That(them.Combat.Subsystems.Sum(s => s.Shield), Is.LessThan(shields * 0.6));
            Assert.That(b.Fx.Any(f => f.Kind == FxKind.EmpBurst), Is.True);
        }

        [Test]
        public void ABigHullShrugsAnEmpOffSoonerThanAFighter()
        {
            double fighter = Battle.EmpStunSeconds(8, CombatSpec.Of(ShipClass.Fighter).LengthKm);
            double battleship = Battle.EmpStunSeconds(8, CombatSpec.Of(ShipClass.Battleship).LengthKm);
            Assert.That(fighter, Is.EqualTo(8));
            Assert.That(battleship, Is.LessThan(1));
        }

        [Test]
        public void AStunnedHullLaysNoMines()
        {
            var (b, me, them) = Duel(ShipClass.Freighter, ShipClass.Fighter, new DVec3(-1, 0, 0));
            me.Stunned = 5;
            me.PlayerMineRequested = true;
            b.Advance(0.1);
            Assert.That(b.Mines, Is.Empty, "a mine went out from a hull with its systems knocked out");
        }

        [Test]
        public void AStunnedHullsCrewHoldTheirFireUntilItsSystemsComeBack()
        {
            var (b, me, them) = Duel(ShipClass.Destroyer, ShipClass.Destroyer, new DVec3(4, 0, 0), player: false);
            them.Combat.ZeroAllShields();
            double before = Health(them);
            me.Stunned = 20;
            for (int i = 0; i < 100; i++)
            {
                them.Stunned = 1;
                me.Pos = DVec3.Zero;
                them.Pos = new DVec3(4, 0, 0);
                b.Advance(0.1);
            }
            Assert.That(Health(them), Is.EqualTo(before), "a stunned hull's guns fired");
            Assert.That(b.Missiles.Count + b.Rounds.Count, Is.Zero);
        }

        [Test]
        public void AStunnedCarrierFighterDriftsOnTheWayItWasGoing()
        {
            var blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
            var red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, 1) { Launch = FighterAI.Launch, FighterStep = FighterAI.Step };
            b.AddShip(Hull(ShipClass.Carrier, blue, 0), 0, DVec3.Zero, DVec3.UnitX);
            b.AddShip(Hull(ShipClass.Destroyer, red, 1), 1, new DVec3(20, 0, 0), -DVec3.UnitX);
            var fighter = b.Units.First(u => u.Kind == UnitKind.Fighter);
            fighter.Vel = new DVec3(0, 0.2, 0);
            fighter.Stunned = 5;
            var from = fighter.Pos;
            b.Advance(1);
            Assert.That((fighter.Pos - (from + new DVec3(0, 0.2, 0))).Length, Is.LessThan(1e-9), "a stunned fighter flew on under its own drive");
        }

        // ------------------------------------------------------------ mines

        [Test]
        public void AMineLaidAsternArmsThenBlowsUnderAHostileThatFliesIntoIt()
        {
            var (b, me, them) = Duel(ShipClass.Courier, ShipClass.Fighter, new DVec3(-3, 0, 0), utility: ModuleKind.MineLayer);
            me.PlayerMineRequested = true;
            b.Advance(0.1);
            Assert.That(b.Mines.Count, Is.EqualTo(1), "no mine was laid on request");
            var mine = b.Mines[0];
            Assert.That(DVec3.Dot(mine.Pos - me.Pos, me.Forward), Is.LessThan(0), "the mine went out ahead, not astern");

            // Straight onto it before it is armed: nothing happens.
            them.Pos = mine.Pos;
            them.Vel = DVec3.Zero;
            b.Advance(0.1);
            Assert.That(b.Mines.Count, Is.EqualTo(1), "a mine blew before it was armed");

            double before = Health(them);
            for (int i = 0; i < 30 && b.Mines.Count > 0; i++)
            {
                them.Pos = mine.Pos;
                b.Advance(0.1);
            }
            Assert.That(b.Mines.Count, Is.EqualTo(0), "an armed mine with a hostile on top of it never went off");
            Assert.That(Health(them), Is.LessThan(before), "the blast hurt nothing");
            Assert.That(b.Fx.Any(f => f.Kind == FxKind.MineBlast), Is.True);
        }

        [Test]
        public void ACouriersCrewLaysMinesForWhatIsOnItsTail()
        {
            // A hostile fighter close astern, a courier with a mine layer on its utility mount flown by its own crew.
            var (b, me, them) = Duel(ShipClass.Courier, ShipClass.Fighter, new DVec3(-1.5, 0, 0), player: false, utility: ModuleKind.MineLayer);
            for (int i = 0; i < 30 && b.Mines.Count == 0; i++)
            {
                me.Forward = DVec3.UnitX;
                them.Pos = me.Pos + new DVec3(-1.5, 0, 0);
                b.Advance(0.1);
            }
            Assert.That(b.Mines.Count, Is.GreaterThan(0), "a courier with a fighter on its tail laid no mines");
        }

        // ------------------------------------------------------------ auto turrets

        [Test]
        public void AnAutoTurretOnTheHullFiresOnItsOwnAtAFighterWithNoTriggerHeld()
        {
            // A courier with an auto turret bolted onto its spine (no stock hull carries one now the Breacher is only a round, U.SPC-T517).
            var (b, me, them) = Duel(ShipClass.Courier, ShipClass.Fighter, new DVec3(0.6, 0.6, 0), spineTurret: true);
            var turret = me.Combat.Subsystems.Single(s => s.Kind == SubsystemKind.AutoTurret);
            Assert.That(turret.Def.Weapon, Is.SameAs(WeaponSpec.AutoTurret));
            me.PlayerFireHeld = false;
            double before = Health(them);
            bool fired = false;
            for (int i = 0; i < 100; i++)
            {
                me.Forward = DVec3.UnitX;
                them.Pos = new DVec3(0.6, 0.6, 0);
                them.Vel = DVec3.Zero;
                them.Stunned = 1;
                double cd = turret.Cooldown;
                b.Advance(0.1);
                fired |= turret.Cooldown > cd + 1e-9;
            }
            Assert.That(fired, Is.True, "the auto turret never fired at a fighter beside the hull");
            Assert.That(Health(them), Is.LessThan(before), "the auto turret never hit the fighter beside the hull");
            Assert.That(me.Heat, Is.EqualTo(0), "the pilot's own guns fired with no trigger held");
        }

        [Test]
        public void AnAutoTurretFitsAWarshipsSmallBallisticMountAsWellAsASmallCrafts()
        {
            var destroyer = new Loadout(CombatSpec.Of(ShipClass.Destroyer), ShipClass.Destroyer, 0);
            int flak = destroyer.Slots.FindIndex(s => s.Stock == ModuleKind.FlakCannon);
            Assert.That(Outfitting.CanFit(destroyer, flak, ModuleKind.AutoTurret), Is.True);
            var courier = new Loadout(CombatSpec.Of(ShipClass.Courier), ShipClass.Courier, 0);
            courier.Fitted[courier.Slots.FindIndex(s => s.Type == MountType.Ballistic)] = ModuleKind.AutoTurret;
            Assert.That(new ShipCombat(courier.Build(), ShipClass.Courier).Subsystems.Any(s => s.Kind == SubsystemKind.AutoTurret), Is.True,
                "a small craft built with an auto turret on its gun mount carries it");
        }

        // ------------------------------------------------------------ missiles at fighters

        [Test]
        public void APlayersMissileLocksOntoAndHitsAFighter()
        {
            var (b, me, them) = Duel(ShipClass.Fighter, ShipClass.Fighter, new DVec3(4, 0, 0));
            me.PlayerLockTarget = them;
            me.PlayerAim = DVec3.UnitX;
            me.PlayerSecondaryHeld = true;
            double before = Health(them);
            for (int i = 0; i < 100; i++)
            {
                me.Forward = DVec3.UnitX;
                them.Pos = new DVec3(4, 0, 0);
                them.Vel = DVec3.Zero;
                b.Advance(0.1);
            }
            Assert.That(Health(them), Is.LessThan(before), "a missile locked onto a fighter never reached it");
        }

        [Test]
        public void APlayersMissileAndEmpAreNotWastedOnAHullThatStruckItsColours()
        {
            var (b, me, them) = Duel(ShipClass.Courier, ShipClass.Fighter, new DVec3(2, 0, 0));
            me.PlayerLockTarget = them;
            me.PlayerAim = DVec3.UnitX;
            me.PlayerSecondaryHeld = true;
            me.PlayerEmpHeld = true;
            them.Surrendering = true;
            int ammo = me.Combat.Subsystems.Sum(s => s.Ammo);
            for (int i = 0; i < 30; i++)
            {
                me.Forward = DVec3.UnitX;
                them.Pos = new DVec3(2, 0, 0);
                them.Vel = DVec3.Zero;
                b.Advance(0.1);
            }
            Assert.That(me.Combat.Subsystems.Sum(s => s.Ammo), Is.EqualTo(ammo), "ammunition was spent on a hull that had surrendered");
            Assert.That(them.Stunned, Is.EqualTo(0), "an EMP was spent on a hull that had surrendered");
        }

        // ------------------------------------------------------------ missiles judged against the patch they came in by (U.SPC-T380, U.SPC-T501)

        [Test]
        public void AHullTargetedMissileFromAstern_DrainsTheSternPatchNotTheBow()
        {
            var (b, _, them) = Duel(ShipClass.Fighter, ShipClass.Battleship, new DVec3(3000, 0, 0), player: false);
            them.Forward = -DVec3.UnitX;
            var bow = them.Combat.EmitterFor(new DVec3(0, 0, 0.5));
            var stern = them.Combat.EmitterFor(new DVec3(0, 0, -0.5));
            Assert.That(bow, Is.Not.Null.And.Not.SameAs(stern));
            double bowBefore = bow.Shield, sternBefore = stern.Shield;
            Assert.That(sternBefore, Is.GreaterThan(0));
            // Just astern of the hull (the target faces -X, so +X is its stern), homing on the hull centre.
            var pos = them.Pos + new DVec3(them.Radius * 0.62, 0, 0);
            b.Missiles.Add(new Missile
            {
                Pos = pos, Vel = new DVec3(-0.3, 0, 0), Target = them, TargetSub = null, Damage = 50, Speed = 0.3, Hp = 10, Life = 60,
                Side = 0, Kind = MissileKind.Missile,
            });
            b.Advance(0.1);
            Assert.That(stern.Shield, Is.LessThan(sternBefore), "the patch the missile came in through took no damage");
            Assert.That(bow.Shield, Is.EqualTo(bowBefore), "a hull-targeted missile hit the bow patch regardless of its approach");
        }
    }
}
