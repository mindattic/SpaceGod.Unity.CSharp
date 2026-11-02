using System.Linq;
using Unity.SpaceGod.Core;
using NUnit.Framework;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>
    /// The heavy fighter's own hull (U.SPC-T467): an armoured, multi-crew gunship with crewed turrets, in the heavy-fighter
    /// flight family, holding every heavy-fighter slot the Breacher used to fill (patrol wings, pickets, convoy escorts and
    /// merchant wings at the heavy hire fee), so those wings keep their strength once the Breacher becomes a boarding round.
    /// </summary>
    [TestFixture]
    public class GunshipTests
    {
        [Test]
        public void TheGunshipIsAnArmouredMultiCrewHeavyFighterWithTurrets()
        {
            var spec = CombatSpec.Of(ShipClass.Gunship);
            Assert.That(spec.DesignName, Is.EqualTo("Gunship"));
            Assert.That(ShipClassSpec.Of(ShipClass.Gunship).DisplayName, Is.EqualTo("Gunship"));
            Assert.That(ShipClassSpec.Of(ShipClass.Gunship).IsMilitary, Is.True);
            Assert.That(FlightModelRegistry.Default.FamilyOf(ShipClass.Gunship), Is.EqualTo(FlightFamily.HeavyFighter));

            var fighter = CombatSpec.Of(ShipClass.Fighter);
            Assert.That(spec.Armor, Is.GreaterThan(fighter.Armor), "not armoured past a light fighter");
            Assert.That(spec.Hull, Is.GreaterThan(fighter.Hull), "no tougher than a light fighter");
            Assert.That(spec.Crew, Is.GreaterThan(fighter.Crew), "not multi-crew");
            Assert.That(spec.Marines, Is.EqualTo(0), "a gunship is no boarding craft");
            Assert.That(spec.Layout.Any(d => d.Kind == SubsystemKind.Drill), Is.False, "a gunship has no drill prow");
            Assert.That(spec.Layout.Count(d => d.Kind == SubsystemKind.LaserTurret), Is.GreaterThanOrEqualTo(2), "no crewed turrets");
            Assert.That(spec.Layout.Any(d => d.Kind == SubsystemKind.LightRailgun), Is.True, "no gun for a corvette's armour");

            Assert.That(HullFrames.CategoryOf(ShipClass.Gunship), Is.EqualTo(HullCategory.Fighter));
            Assert.That(Loadout.IsSmallCraft(ShipClass.Gunship), Is.True);
            Assert.That(FtlCarriage.BerthsOf(ShipClass.Gunship), Is.EqualTo(0));
            Assert.That(CruiseDrives.Of(ShipClass.Gunship), Is.Null);
            Assert.That(Interior.For(ShipClass.Gunship, spec.Crew, 0).Compartments.Any(c => c.Kind == CompartmentKind.Battery), Is.True, "no gun deck for its turret crews");
        }

        [Test]
        public void EveryHeavyFighterSlotIsAGunship()
        {
            Assert.That(Enumerable.Range(0, 4).Select(PatrolWings.Composition).Count(c => c.Contains(ShipClass.Gunship)), Is.EqualTo(2),
                "the heavy-fighter wing and the destroyer wing each fly a gunship (the lane picket flies an interdictor, U.SPC-T479)");
            Assert.That(Enumerable.Range(0, 2).Select(PatrolWings.ConvoyEscort).Count(c => c.Contains(ShipClass.Gunship)), Is.EqualTo(1));

            var rng = new System.Random(3);
            Assert.That(MerchantEscorts.Wing(3, rng)[0], Is.EqualTo(ShipClass.Gunship));
            Assert.That(MerchantEscorts.HireFee(ShipClass.Gunship), Is.EqualTo(MerchantEscorts.HeavyHireFee));
        }
    }
}
