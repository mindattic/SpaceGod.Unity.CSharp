using System;
using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// Phaser arrays and plasma cannons (U.SPC-T488): an array is fitted only to destroyers and cruisers and its captain tunes
    /// it in battle between a narrow lance for hulls and a wide sweep for fighters and missiles; a plasma cannon is fitted only
    /// to the brawlers and its bolt bleeds off over distance; every figure is a setting.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class EnergyWeaponsTests
    {
        const double StartJd = 2461324.0;
        static readonly Faction Blue = new Faction("Blue", "BLU", new Rgb(0, 0, 1)) { IsMilitary = true };
        static readonly Faction Red = new Faction("Red", "RED", new Rgb(1, 0, 0)) { IsMilitary = true };

        static Ship Hull(ShipClass c, Faction f, int id = 1) => new Ship
        {
            Id = id, Name = f.Prefix + " " + c + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c),
        };

        /// <summary>Fits <paramref name="module"/> into the hull's first energy mount that takes it, as a yard would.</summary>
        static int Arm(Ship ship, ModuleKind module)
        {
            var l = Outfitting.LoadoutOf(ship);
            for (int i = 0; i < l.Slots.Count; i++)
            {
                if (l.Slots[i].Type != MountType.Energy || l.Fitted[i] == module || !Outfitting.CanFit(l, i, module)) continue;
                l.Fitted[i] = module;
                ship.Combat = ship.Combat.Refitted(l.Build(), l.Class, l.Slots[i].LayoutIndex);
                return l.Slots[i].LayoutIndex;
            }
            return -1;
        }

        /// <summary>Whether any of the hull's mounts of <paramref name="type"/> (and <paramref name="size"/>, if given) takes <paramref name="m"/>.</summary>
        static bool Takes(ShipClass c, ModuleKind m, MountType type, HardpointSize? size = null)
        {
            var ship = Hull(c, Blue);
            var l = Outfitting.LoadoutOf(ship);
            return Enumerable.Range(0, l.Slots.Count).Any(i => l.Slots[i].Type == type && (size == null || l.Slots[i].Size == size) && Outfitting.CanFit(l, i, m));
        }

        // ---------------------------------------------------------------- fitting by role

        [Test]
        public void PhaserArraysGoOnlyOnDestroyersAndCruisersOnAnEnergyMount()
        {
            Assert.That(Takes(ShipClass.Cruiser, ModuleKind.PhaserArray, MountType.Energy, HardpointSize.Large), Is.True);
            Assert.That(Takes(ShipClass.Destroyer, ModuleKind.PhaserArray, MountType.Energy, HardpointSize.Small), Is.True, "a destroyer's point-defence mounts take an array for its screen");
            Assert.That(Takes(ShipClass.Destroyer, ModuleKind.PhaserArray, MountType.Ballistic), Is.False, "an array is an energy weapon: the railgun's mount takes none");
            Assert.That(Takes(ShipClass.Destroyer, ModuleKind.PhaserArray, MountType.Missile), Is.False);
            Assert.That(Takes(ShipClass.Battleship, ModuleKind.PhaserArray, MountType.Energy), Is.False, "not a destroyer's or cruiser's");
            Assert.That(Takes(ShipClass.Carrier, ModuleKind.PhaserArray, MountType.Energy), Is.False);
            Assert.That(Takes(ShipClass.Fighter, ModuleKind.PhaserArray, MountType.Ballistic), Is.False);
            Assert.That(EnergyWeapons.RoleAllows(ModuleKind.PhaserArray, null), Is.False, "nor a station's");
            Assert.That(EnergyWeapons.RoleAllows(ModuleKind.LaserTurret, ShipClass.Battleship), Is.True, "a weapon with no role fits any hull");
        }

        [Test]
        public void PlasmaCannonsGoOnlyOnTheBrawlersWeaponMounts()
        {
            Assert.That(Takes(ShipClass.Battleship, ModuleKind.PlasmaCannon, MountType.Energy, HardpointSize.Large), Is.True);
            Assert.That(Takes(ShipClass.Battleship, ModuleKind.PlasmaCannon, MountType.Energy, HardpointSize.Small), Is.False, "too big for a point-defence mount");
            Assert.That(Takes(ShipClass.Battleship, ModuleKind.PlasmaCannon, MountType.Missile), Is.False, "an energy weapon, on no missile mount");
            Assert.That(Takes(ShipClass.Cruiser, ModuleKind.PlasmaCannon, MountType.Energy), Is.False);
            Assert.That(Takes(ShipClass.Destroyer, ModuleKind.PlasmaCannon, MountType.Energy), Is.False);
            Assert.That(ModuleSpec.Fits(ModuleKind.PlasmaCannon, MountType.Missile, HardpointSize.Large, true), Is.False);
        }

        [Test]
        public void AFittedArrayOrCannonBecomesItsOwnHardpoint()
        {
            var cruiser = Hull(ShipClass.Cruiser, Blue);
            double strength = cruiser.Combat.Strength;
            int at = Arm(cruiser, ModuleKind.PhaserArray);
            Assert.That(at, Is.GreaterThanOrEqualTo(0));
            Assert.That(cruiser.Combat.Subsystems[at].Kind, Is.EqualTo(SubsystemKind.PhaserArray));
            Assert.That(cruiser.Combat.Subsystems[at].Def.Weapon, Is.SameAs(EnergyWeapons.PhaserLance), "an array's own weapon is its lance");
            Assert.That(cruiser.Combat.Strength, Is.GreaterThan(strength), "an array outguns the laser turret it replaced");
            Assert.That(ModuleSpec.Of(ModuleKind.PhaserArray).Kind, Is.EqualTo(ModuleKind.PhaserArray));
            Assert.That(ModuleSpec.Of(ModuleKind.PlasmaCannon).Kind, Is.EqualTo(ModuleKind.PlasmaCannon));
            Assert.That(ModuleSpec.SubsystemOf(ModuleKind.PlasmaCannon), Is.EqualTo(SubsystemKind.PlasmaCannon));

            var battleship = Hull(ShipClass.Battleship, Blue);
            int gun = Arm(battleship, ModuleKind.PlasmaCannon);
            Assert.That(battleship.Combat.Subsystems[gun].Def.Weapon, Is.SameAs(EnergyWeapons.PlasmaCannon));
            Assert.That(SubsystemDef.Name(SubsystemKind.PlasmaCannon), Is.EqualTo("Plasma cannon"));
        }

        // ---------------------------------------------------------------- tuning

        [Test]
        public void ALanceReachesFurtherAndHitsHarderWhileASweepFiresFastAtSmallCraft()
        {
            var lance = EnergyWeapons.PhaserLance;
            var sweep = EnergyWeapons.PhaserSweep;
            Assert.That(lance.RangeKm, Is.GreaterThan(sweep.RangeKm));
            Assert.That(lance.Damage, Is.GreaterThan(sweep.Damage * 4));
            Assert.That(sweep.Cooldown, Is.LessThan(lance.Cooldown / 4));
            Assert.That(sweep.AntiSmall, Is.True, "a sweep engages missiles and fighters first");
            Assert.That(lance.AntiSmall, Is.False);
            Assert.That(lance.RangeKm, Is.GreaterThan(WeaponSpec.Laser.RangeKm).And.LessThan(WeaponSpec.Railgun.RangeKm), "medium range");
        }

        [Test]
        public void TheCaptainsTuningIsWhatEveryArrayOnTheHullFiresAndItOutlastsARefit()
        {
            var ship = Hull(ShipClass.Destroyer, Blue);
            int a = Arm(ship, ModuleKind.PhaserArray);
            int b = Arm(ship, ModuleKind.PhaserArray);
            Assert.That(a, Is.Not.EqualTo(b));
            var c = ship.Combat;
            Assert.That(c.PhaserTuning, Is.EqualTo(PhaserTuning.Lance), "an array starts as a lance");
            Assert.That(EnergyWeapons.Effective(c.Subsystems[a], c), Is.SameAs(EnergyWeapons.PhaserLance));
            Assert.That(EnergyWeapons.Retune(c), Is.EqualTo(PhaserTuning.Sweep));
            Assert.That(EnergyWeapons.Effective(c.Subsystems[a], c), Is.SameAs(EnergyWeapons.PhaserSweep));
            Assert.That(EnergyWeapons.Effective(c.Subsystems[b], c), Is.SameAs(EnergyWeapons.PhaserSweep));
            var railgun = c.Subsystems.First(s => s.Kind == SubsystemKind.Railgun);
            Assert.That(EnergyWeapons.Effective(railgun, c), Is.SameAs(railgun.Def.Weapon), "tuning touches only the arrays");
            var refitted = c.Refitted(c.Spec, ShipClass.Destroyer, -1);
            Assert.That(refitted.PhaserTuning, Is.EqualTo(PhaserTuning.Sweep));
            Assert.That(EnergyWeapons.Retune(c), Is.EqualTo(PhaserTuning.Lance));
        }

        static (Battle b, BattleUnit me, BattleUnit them) Fight(Ship mine, Ship theirs, double km, int seed = 1)
        {
            var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, seed);
            b.AddShip(mine, 0, DVec3.Zero, DVec3.UnitX);
            b.AddShip(theirs, 1, new DVec3(km, 0, 0), -DVec3.UnitX);
            return (b, b.Units[0], b.Units[1]);
        }

        static void Incoming(Battle b, BattleUnit at, int count, double km)
        {
            for (int i = 0; i < count; i++)
                b.Missiles.Add(new Missile { Pos = at.Pos + new DVec3(km, i * 0.2, 0), Side = 1 - at.Side, Damage = 100, Speed = 1, Hp = 12, Life = 90, Target = at });
        }

        [Test]
        public void AnAiCaptainSweepsWhenSmallCraftCloseAndLancesHullsOtherwise()
        {
            var mine = Hull(ShipClass.Cruiser, Blue, 0);
            Arm(mine, ModuleKind.PhaserArray);
            var (b, me, _) = Fight(mine, Hull(ShipClass.Cruiser, Red, 1), 15);
            Assert.That(EnergyWeapons.Choose(b, me), Is.EqualTo(PhaserTuning.Lance), "a hull in the lance's reach and nothing small about");
            Incoming(b, me, 1, 4);
            Assert.That(EnergyWeapons.Choose(b, me), Is.EqualTo(PhaserTuning.Lance), "one missile is point defence's while a hull is there to burn");
            Incoming(b, me, 1, 5);
            Assert.That(EnergyWeapons.Choose(b, me), Is.EqualTo(PhaserTuning.Sweep), "two closing at once: sweep them");

            EnergyWeapons.Step(b, me);
            Assert.That(me.Combat.PhaserTuning, Is.EqualTo(PhaserTuning.Sweep), "the AI's captain retunes as it steps");
            me.PlayerControlled = true;
            me.Combat.PhaserTuning = PhaserTuning.Lance;
            EnergyWeapons.Step(b, me);
            Assert.That(me.Combat.PhaserTuning, Is.EqualTo(PhaserTuning.Lance), "the player's tuning is the player's");

            var far = Hull(ShipClass.Cruiser, Blue, 2);
            Arm(far, ModuleKind.PhaserArray);
            var (b2, alone, _) = Fight(far, Hull(ShipClass.Cruiser, Red, 3), 35);
            Incoming(b2, alone, 1, 4);
            Assert.That(EnergyWeapons.Choose(b2, alone), Is.EqualTo(PhaserTuning.Sweep), "one missile and no hull in reach: sweep it");
        }

        [Test]
        public void TheAiTuningCanBeTurnedOff()
        {
            var mine = Hull(ShipClass.Cruiser, Blue, 0);
            Arm(mine, ModuleKind.PhaserArray);
            var (b, me, _) = Fight(mine, Hull(ShipClass.Cruiser, Red, 1), 15);
            Incoming(b, me, 3, 3);
            try
            {
                EnergyWeapons.AiTunesPhasers.Set(false);
                EnergyWeapons.Step(b, me);
                Assert.That(me.Combat.PhaserTuning, Is.EqualTo(PhaserTuning.Lance));
            }
            finally
            {
                EnergyWeapons.AiTunesPhasers.Set(true);
            }
        }

        [Test]
        public void ASweepingArrayShootsDownMissilesInBattle()
        {
            var mine = Hull(ShipClass.Destroyer, Blue, 0);
            Arm(mine, ModuleKind.PhaserArray);
            var (b, me, them) = Fight(mine, Hull(ShipClass.Cruiser, Red, 1), 30, seed: 4);
            me.Combat.PhaserTuning = PhaserTuning.Sweep;
            me.PlayerControlled = true;
            Incoming(b, me, 3, 3);
            for (int i = 0; i < 20; i++) b.Advance(0.1);
            Assert.That(b.Fx.Any(f => f.Kind == FxKind.Phaser && f.Side == me.Side), Is.True, "the swept array fired");
        }

        // ---------------------------------------------------------------- plasma bleeding off

        [Test]
        public void APlasmaBoltBleedsOffEvenlyOverItsRange()
        {
            var p = EnergyWeapons.PlasmaCannon;
            Assert.That(p.DamageAt(0), Is.EqualTo(p.Damage));
            Assert.That(p.DamageAt(p.RangeKm), Is.EqualTo(p.Damage * EnergyWeapons.PlasmaEdgeShare.Value).Within(1e-9));
            Assert.That(p.DamageAt(p.RangeKm / 2), Is.EqualTo(p.Damage * (1 + EnergyWeapons.PlasmaEdgeShare.Value) / 2).Within(1e-9));
            Assert.That(p.DamageAt(p.RangeKm * 3), Is.EqualTo(p.DamageAt(p.RangeKm)), "never below its edge");
            Assert.That(p.DamageAt(1), Is.GreaterThan(p.DamageAt(2)));
            Assert.That(WeaponSpec.Laser.DamageAt(WeaponSpec.Laser.RangeKm), Is.EqualTo(WeaponSpec.Laser.Damage), "a laser hits as hard at any range");
            Assert.That(p.RangeKm, Is.LessThan(WeaponSpec.Laser.RangeKm), "short range");
            Assert.That(p.Damage / p.Cooldown, Is.GreaterThan(WeaponSpec.Heavy.Damage / WeaponSpec.Heavy.Cooldown * 2), "devastating up close");
        }

        /// <summary>What each plasma bolt a small hull landed did, pinned <paramref name="km"/> from its target.</summary>
        static double[] PlasmaHits(double km, int seed)
        {
            // Small hulls, so where on each the bolt leaves and lands hardly moves the distance it crosses: a gunship with a
            // plasma cannon on a bow mount (no stock small hull takes one by role, U.SPC-T517).
            var spec = CombatSpec.Of(ShipClass.Gunship).Clone();
            spec.Layout = spec.Layout.Concat(new[] { new SubsystemDef(SubsystemKind.PlasmaCannon, 0, 0.03, 0.3, coneAxis: FireCones.Bow, coneHalfDeg: 25) }).ToArray();
            var mine = new Ship { Id = 0, Name = Blue.Prefix + " plasma gunship", Spec = ShipClassSpec.Of(ShipClass.Gunship), Faction = Blue,
                                  Combat = new ShipCombat(spec, ShipClass.Gunship) };
            var (b, me, them) = Fight(mine, Hull(ShipClass.Destroyer, Red, 1), km, seed);
            for (int i = 0; i < 600; i++)
            {
                me.Pos = DVec3.Zero;
                me.Vel = DVec3.Zero;
                me.Forward = DVec3.UnitX;
                them.Pos = new DVec3(km, 0, 0);
                them.Vel = DVec3.Zero;
                b.Advance(0.1);
            }
            var bolts = b.Fx.Where(f => f.Kind == FxKind.Plasma && f.Side == me.Side).ToArray();
            foreach (var f in bolts)
                Assert.That(f.Size, Is.EqualTo(EnergyWeapons.PlasmaCannon.DamageAt((f.B - f.A).Length)).Within(1e-6), "each bolt lands with what its distance leaves it");
            return bolts.Select(f => f.Size).ToArray();
        }

        [Test]
        public void APlasmaCannonLandsHarderCloseInThanAtRange()
        {
            var p = EnergyWeapons.PlasmaCannon;
            var near = PlasmaHits(2, 5);
            var far = PlasmaHits(8.5, 5);
            Assert.That(near, Is.Not.Empty, "the cannon fired close in");
            Assert.That(far, Is.Not.Empty, "the cannon fired at range");
            Assert.That(near.Min(), Is.GreaterThan(p.Damage * 0.75), "close in it lands near full");
            Assert.That(far.Max(), Is.LessThan(p.Damage * 0.4), "near its edge it has bled most away");
        }

        // ---------------------------------------------------------------- settings

        [Test]
        public void EveryFigureIsASettingInTheWeaponsGroup()
        {
            foreach (var key in new[] { "phaser.lanceDamage", "phaser.lanceRange", "phaser.sweepDamage", "phaser.sweepRange", "phaser.aiTunes", "plasma.damage", "plasma.range", "plasma.edgeShare" })
            {
                var s = SettingsRegistry.Default.Find(key);
                Assert.That(s, Is.Not.Null, key);
                Assert.That(s.Group, Is.EqualTo(SettingGroups.Weapons), key);
            }
            double was = EnergyWeapons.PlasmaDamage.Value;
            double edge = EnergyWeapons.PlasmaEdgeShare.Value;
            try
            {
                EnergyWeapons.PlasmaDamage.Set(800);
                EnergyWeapons.PlasmaEdgeShare.Set(1);
                Assert.That(EnergyWeapons.PlasmaCannon.Damage, Is.EqualTo(800), "the fitted cannons follow the menu");
                Assert.That(EnergyWeapons.PlasmaCannon.DamageAt(EnergyWeapons.PlasmaCannon.RangeKm), Is.EqualTo(800), "100% turns the bleed off");
                EnergyWeapons.LanceRange.Set(30);
                Assert.That(EnergyWeapons.PhaserLance.RangeKm, Is.EqualTo(30));
            }
            finally
            {
                EnergyWeapons.PlasmaDamage.Set(was);
                EnergyWeapons.PlasmaEdgeShare.Set(edge);
                EnergyWeapons.LanceRange.Set(EnergyWeapons.LanceRange.Default);
            }
        }
    }
}
