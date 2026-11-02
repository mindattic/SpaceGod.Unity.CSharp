using System.Linq;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The tachyon lance (U.SPC-T487): a spinal weapon only a capital hull can carry, in a bow mount, built only at the
    /// greatest yards and dear; its shot lands the instant it is fired at the longest reach of any gun, with no round in
    /// flight and no lead; it recharges slowly and starves the shields while it charges; and its figures are settings.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class TachyonLanceTests
    {
        static readonly Faction Navy = new Faction("Navy", "NS", new Rgb(0, 0, 1)) { IsMilitary = true };
        static readonly Faction Foe = new Faction("Foe", "FOE", new Rgb(1, 0, 0)) { IsMilitary = true };
        static readonly Faction Raiders = new Faction("Black Banner", "", new Rgb(1, 0, 0)) { IsPirate = true };

        const double StartJd = 2461324.0;

        static (Economy economy, Settlement yard) Yard(Faction owner, double level, SettlementKind kind = SettlementKind.Station)
        {
            var economy = new Economy(new Diplomacy(), 0);
            var yard = economy.Add("Yard", body: 3, kind, owner, 0);
            yard.Facilities.Add(new Facility(Recipe.Of(IndustryKind.Shipyard), level));
            foreach (var g in new[] { Good.Alloys, Good.Electronics, Good.Machinery, Good.Munitions, Good.Platinum, Good.RareEarths }) yard.Stock[(int)g] = 1e6;
            yard.RefreshPrices();
            return (economy, yard);
        }

        static Ship Hull(ShipClass c, Faction f, int id = 1)
        {
            f.Treasury = 1e12;
            return new Ship { Id = id, Name = c.ToString() + id, Spec = ShipClassSpec.Of(c), Faction = f, Combat = new ShipCombat(CombatSpec.Of(c), c) };
        }

        /// <summary>The hull's first large energy mount bearing on the bow (a battleship's forward heavy laser).</summary>
        static int BowMount(Ship ship)
        {
            var l = Outfitting.LoadoutOf(ship);
            for (int i = 0; i < l.Slots.Count; i++)
                if (l.Slots[i].Type == MountType.Energy && l.Slots[i].Size == HardpointSize.Large && l.Slots[i].BearsOnBow) return i;
            return -1;
        }

        static Ship Lanced(Faction f, int id)
        {
            var ship = Hull(ShipClass.Battleship, f, id);
            var (economy, yard) = Yard(f, 2);
            Assert.That(Outfitting.Fit(economy, ship, yard, BowMount(ship), ModuleKind.TachyonLance, out _), Is.EqualTo(OutfitOutcome.Ok));
            return ship;
        }

        [Test]
        public void OnlyACapitalHullsBowMountTakesALance()
        {
            var battleship = Hull(ShipClass.Battleship, Navy);
            var l = Outfitting.LoadoutOf(battleship);
            Assert.That(Outfitting.CanFit(l, BowMount(battleship), ModuleKind.TachyonLance), Is.True, "a battleship's bow mount");
            int broadside = Enumerable.Range(0, l.Slots.Count).First(i => l.Slots[i].Type == MountType.Energy && l.Slots[i].Size == HardpointSize.Large && !l.Slots[i].BearsOnBow);
            Assert.That(Outfitting.CanFit(l, broadside, ModuleKind.TachyonLance), Is.False, "a lance is spinal: no broadside mount takes one");

            // A carrier is a capital ship too, though its stock hull has no bow mount to put one in.
            Assert.That(TachyonLances.CanCarry(ShipClass.Carrier), Is.True);

            foreach (var c in new[] { ShipClass.Cruiser, ShipClass.Destroyer, ShipClass.Gunship, ShipClass.Fighter, ShipClass.Freighter })
            {
                var ship = Hull(c, Navy);
                var lc = Outfitting.LoadoutOf(ship);
                for (int i = 0; i < lc.Slots.Count; i++)
                    Assert.That(Outfitting.CanFit(lc, i, ModuleKind.TachyonLance), Is.False, c + " is no capital ship");
            }
            Assert.That(TachyonLances.CanCarry(null), Is.False, "a station has no class to carry one");
        }

        [Test]
        public void AFittedLanceIsSpinalAndOutrangesEveryOtherGun()
        {
            var ship = Lanced(Navy, 1);
            var def = ship.Combat.Subsystems.Single(s => s.Kind == SubsystemKind.TachyonLance).Def;
            Assert.That(def.Weapon, Is.SameAs(WeaponSpec.TachyonLance));
            Assert.That(def.Weapon.Lance, Is.True);
            Assert.That(def.Weapon.MuzzleSpeedKmS, Is.EqualTo(0), "no round to fly: it lands the instant it is fired");
            Assert.That(def.HalfDeg, Is.LessThanOrEqualTo(TachyonLances.ConeHalfDeg), "the hull must lay it, not a turret");
            Assert.That(def.Covers(ship.Combat.Spec, FireCones.Bow), Is.True);
            Assert.That(def.Covers(ship.Combat.Spec, new DVec3(0.5, 0, 0.866)), Is.False);
            foreach (var w in new[] { WeaponSpec.Railgun, WeaponSpec.Missiles, WeaponSpec.HeavyTorpedo, WeaponSpec.Heavy })
                Assert.That(def.Weapon.RangeKm, Is.GreaterThan(w.RangeKm), "longer-ranged than every other weapon");
            Assert.That(def.Weapon.Cooldown, Is.GreaterThan(WeaponSpec.Railgun.Cooldown * 3), "a long recharge");
        }

        [Test]
        public void OnlyTheGreatestLawfulYardsBuildALanceAndItIsDear()
        {
            var ship = Hull(ShipClass.Battleship, Navy);
            int bow = BowMount(ship);

            var (smallEconomy, small) = Yard(Navy, 1);
            Assert.That(Outfitting.Fit(smallEconomy, ship, small, bow, ModuleKind.TachyonLance, out _), Is.EqualTo(OutfitOutcome.YardCannotBuild));
            Assert.That(Outfitting.LoadoutOf(ship).Fitted[bow], Is.EqualTo(ModuleKind.HeavyLaser), "nothing changed");
            Assert.That(Outfitting.Fit(smallEconomy, ship, small, bow, ModuleKind.PlasmaCannon, out _), Is.EqualTo(OutfitOutcome.Ok), "a lesser yard still fits a plasma cannon");

            var (havenEconomy, haven) = Yard(Raiders, 3, SettlementKind.Haven);
            Assert.That(TachyonLances.YardBuilds(haven), Is.False, "no raider haven builds one");

            var (economy, great) = Yard(Navy, 2);
            Assert.That(TachyonLances.YardBuilds(great), Is.True);
            var lance = Outfitting.Quote(ship, great, bow, ModuleKind.TachyonLance);
            var rail = Outfitting.Quote(ship, great, bow, ModuleKind.HeavyLaser);
            Assert.That(lance.Materials, Is.GreaterThan(rail.Materials * 5), "far dearer than a heavy gun");
            Assert.That(Outfitting.Fit(economy, ship, great, bow, ModuleKind.TachyonLance, out _), Is.EqualTo(OutfitOutcome.Ok));
            Assert.That(ship.Combat.Subsystems.Count(s => s.Kind == SubsystemKind.TachyonLance), Is.EqualTo(1));
        }

        [Test]
        public void ALanceShotLandsTheInstantItIsFiredAtLongRangeWithNoRoundInFlight()
        {
            int hits = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                var b = new Battle(0, 3, "Test", new DVec3(50000, 0, 0), StartJd, seed);
                var shooter = b.AddShip(Lanced(Navy, 1), 0, DVec3.Zero, DVec3.UnitX);
                // Beyond every other gun's reach: only the lance bears.
                var target = b.AddShip(Hull(ShipClass.Battleship, Foe, 2), 1, new DVec3(52, 0, 0), -DVec3.UnitX);
                var lance = shooter.Combat.Subsystems.Single(s => s.Kind == SubsystemKind.TachyonLance);
                foreach (var s in shooter.Combat.Subsystems) if (s.Def.IsWeapon && s != lance) s.Cooldown = 9999;
                foreach (var s in target.Combat.Subsystems) if (s.Def.IsWeapon) s.Cooldown = 9999;
                target.Vel = shooter.Vel = DVec3.Zero;

                for (int i = 0; i < 40; i++)
                {
                    double before = target.Combat.Hull + target.Combat.Shield;
                    int seen = b.Fx.Count;
                    b.Advance(0.1);
                    var shot = b.Fx.Skip(seen).Where(f => f.Kind == FxKind.TachyonLance || f.Kind == FxKind.TachyonLanceMiss).ToList();
                    Assert.That(b.Rounds.Count, Is.EqualTo(0), "a lance never leaves a round in flight");
                    if (shot.Count == 0) continue;
                    Assert.That(lance.Cooldown, Is.GreaterThan(TachyonLances.Recharge.Value * 0.8), "it has to charge again");
                    if (shot[0].Kind == FxKind.TachyonLance)
                    {
                        hits++;
                        Assert.That(before - (target.Combat.Hull + target.Combat.Shield), Is.GreaterThan(TachyonLances.Damage.Value * 0.5),
                            "the damage lands in the same step the shot is fired");
                    }
                    break;
                }
            }
            Assert.That(hits, Is.GreaterThanOrEqualTo(4), "as sure at 52 km as close in");
        }

        [Test]
        public void AChargingLanceStarvesTheShields()
        {
            var ship = Lanced(Navy, 1);
            var lance = ship.Combat.Subsystems.Single(s => s.Kind == SubsystemKind.TachyonLance);
            lance.Cooldown = 0;
            Assert.That(TachyonLances.ShieldRegenFactor(ship.Combat), Is.EqualTo(1), "a charged lance draws nothing");
            lance.Cooldown = 30;
            Assert.That(TachyonLances.ShieldRegenFactor(ship.Combat), Is.EqualTo(1 - TachyonLances.PowerDraw.Value).Within(1e-9));
            Assert.That(TachyonLances.PowerDraw.Value, Is.GreaterThan(0));
            Assert.That(TachyonLances.ShieldRegenFactor(Hull(ShipClass.Battleship, Navy, 3).Combat), Is.EqualTo(1), "no lance, no draw");
        }

        [Test]
        public void TheLancesFiguresAreSettings()
        {
            foreach (var key in new[] { "lance.range", "lance.damage", "lance.recharge", "lance.powerDraw", "lance.yardLevel" })
                Assert.That(SettingsRegistry.Default.Find(key), Is.Not.Null, key);
            double saved = TachyonLances.Range.Value;
            try
            {
                TachyonLances.Range.Set(90);
                Assert.That(WeaponSpec.TachyonLance.RangeKm, Is.EqualTo(90));
            }
            finally
            {
                TachyonLances.Range.Set(saved);
            }
            Assert.That(WeaponSpec.TachyonLance.RangeKm, Is.EqualTo(saved));
        }

        [Test]
        public void EveryModuleSitsAtItsOwnKindsPlaceInTheCatalogue()
        {
            foreach (var m in ModuleSpec.Catalogue) Assert.That(ModuleSpec.Of(m.Kind), Is.SameAs(m), m.Name);
            Assert.That(ModuleSpec.Of(ModuleKind.TachyonLance).Name, Is.EqualTo("Tachyon lance"));
        }
    }
}
