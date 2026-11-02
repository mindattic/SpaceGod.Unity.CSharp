using System.Linq;
using NUnit.Framework;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>Unit tests for <see cref="ModularHull"/> (U.SPC-T596): a hull as modules and the joints holding them,
    /// breaking apart under fire and into wreckage, and what the pieces are worth to a salvager.</summary>
    [TestFixture]
    public class ModularHullTests
    {
        static CombatSpec Cruiser => CombatSpec.Of(ShipClass.Cruiser);

        [Test]
        public void For_CutsTheKeelIntoSectionsByLengthWithTheBowForemost()
        {
            var h = ModularHull.For(Cruiser);
            var keel = h.Modules.Where(m => m.Kind != HullModuleKind.Sponson).ToList();
            Assert.That(keel.Count, Is.EqualTo(ModularHull.KeelSections(Cruiser.LengthKm)));
            Assert.That(keel.First().Kind, Is.EqualTo(HullModuleKind.Engineering));
            Assert.That(keel.Last().Kind, Is.EqualTo(HullModuleKind.Bow));
            Assert.That(ModularHull.KeelSections(0.1), Is.EqualTo(2));
            Assert.That(ModularHull.KeelSections(3.2), Is.EqualTo(ModularHull.MaxSections));
        }

        [Test]
        public void For_PutsEveryHardpointOnExactlyOneModuleAndTheMassSumsToOne()
        {
            var spec = Cruiser;
            var h = ModularHull.For(spec);
            var all = h.Modules.SelectMany(m => m.Hardpoints).OrderBy(k => k).ToList();
            Assert.That(all, Is.EqualTo(Enumerable.Range(0, spec.Layout.Length).ToList()));
            Assert.That(h.Modules.Sum(m => m.MassShare), Is.EqualTo(1).Within(1e-9));
            Assert.That(h.Intact, Is.True);
        }

        [Test]
        public void For_SetsBroadsideMountsOnSponsonsAndTheBridgeSectionIsTheRoot()
        {
            var spec = Cruiser;
            var h = ModularHull.For(spec);
            Assert.That(h.Modules.Any(m => m.Kind == HullModuleKind.Sponson), Is.True);
            int bridge = System.Array.FindIndex(spec.Layout, d => d.Kind == SubsystemKind.Bridge);
            Assert.That(h.ModuleOf(bridge).Id, Is.EqualTo(h.Root));
            Assert.That(h.Module(h.Root).Kind, Is.Not.EqualTo(HullModuleKind.Sponson));
        }

        [Test]
        public void Strike_HardEnoughOnASponson_ShootsItOffWithItsMounts()
        {
            var spec = Cruiser;
            var combat = new ShipCombat(spec, ShipClass.Cruiser);
            var h = combat.Structure;
            var sponson = h.Modules.First(m => m.Kind == HullModuleKind.Sponson);
            var pieces = h.Strike(sponson.Centre, spec.Hull);
            var piece = pieces.Single();
            Assert.That(piece.Modules, Does.Contain(sponson.Id));
            Assert.That(piece.Hulk, Is.False);
            Assert.That(sponson.Detached, Is.True);
            Assert.That(h.Attached(sponson.Id), Is.False);
            // Reported once.
            Assert.That(h.Separate(), Is.Empty);

            int lost = ModularHulls.ApplyBreak(combat, piece);
            Assert.That(lost, Is.EqualTo(sponson.Hardpoints.Count));
            foreach (int k in sponson.Hardpoints) Assert.That(combat.Subsystems[k].Destroyed, Is.True);
            Assert.That(combat.Alive, Is.True);
        }

        [Test]
        public void Strike_ALightHit_BreaksNothing()
        {
            var h = ModularHull.For(Cruiser);
            Assert.That(h.Strike(new DVec3(0, 0, 0.1), 50), Is.Empty);
            Assert.That(h.Joints.All(j => !j.Broken), Is.True);
        }

        [Test]
        public void Strike_CuttingTheKeelAftOfTheBridge_TakesTheDriveSectionAway()
        {
            var spec = Cruiser;
            var h = ModularHull.For(spec);
            var aft = h.Module(0);
            // Hammer the stern until it comes away.
            for (int i = 0; i < 40 && !aft.Detached; i++) h.Strike(aft.Centre + new DVec3(0, 0, aft.HalfExtent.Z * 0.9), spec.Hull * 0.1);
            Assert.That(aft.Detached, Is.True);
            Assert.That(h.Attached(h.Root), Is.True);
        }

        [Test]
        public void BreakUp_IsTheSameEveryTimeForTheSameSeedAndAlwaysSplitsTheHull()
        {
            var a = ModularHull.For(Cruiser).BreakUp(17);
            var b = ModularHull.For(Cruiser).BreakUp(17);
            Assert.That(a.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(a.Select(p => p.Modules.Count), Is.EqualTo(b.Select(p => p.Modules.Count)));
            Assert.That(a[0].Hulk, Is.True);
            Assert.That(a.Sum(p => p.MassShare), Is.EqualTo(1).Within(1e-9));
            Assert.That(a.SelectMany(p => p.Modules).Distinct().Count(), Is.EqualTo(a.SelectMany(p => p.Modules).Count()));
        }

        [Test]
        public void Scrap_IsMostlyAlloysAndSellsForSomething()
        {
            var spec = Cruiser;
            var combat = new ShipCombat(spec, ShipClass.Cruiser);
            var pieces = combat.Structure.BreakUp(3);
            var scrap = pieces.SelectMany(p => HullSalvage.Scrap(combat.Structure, p, spec, combat)).ToList();
            Assert.That(scrap.Where(s => s.Good == Good.Alloys).Sum(s => s.Tonnes), Is.GreaterThan(0));
            Assert.That(HullSalvage.Value(scrap), Is.GreaterThan(0));
            Assert.That(HullSalvage.PartValue(SubsystemKind.LaserTurret, 1, spec.LengthKm), Is.GreaterThan(HullSalvage.PartValue(SubsystemKind.LaserTurret, 0.5, spec.LengthKm)));
            Assert.That(HullSalvage.PartValue(SubsystemKind.Bridge, 1, spec.LengthKm), Is.EqualTo(0));
        }

        [Test]
        public void Wreck_OfAShipWithACombatState_IsLeftInPiecesThatDriftApart()
        {
            var ship = new Ship { Combat = new ShipCombat(Cruiser, ShipClass.Cruiser) };
            var unit = new BattleUnit { Ship = ship, Faction = new Faction("Blue", "BLU", new Rgb(0, 0, 1)), LengthKm = Cruiser.LengthKm };
            var w = new Wrecks().Add(unit, 0, DVec3.Zero, jd: 0);
            Assert.That(w.Pieces.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(w.Pieces[0].Hulk, Is.True);
            var drifter = w.Pieces[1];
            double early = drifter.OffsetKm(w, 0).Length, late = drifter.OffsetKm(w, 10).Length;
            Assert.That(late, Is.GreaterThan(early));
            Assert.That(late, Is.LessThanOrEqualTo(w.DebrisRadiusKm(10) + w.LengthKm));
            Assert.That(drifter.Scrap, Is.Not.Empty);
        }

        [Test]
        public void Repair_InFull_RebuildsTheStructureWhole()
        {
            var combat = new ShipCombat(Cruiser, ShipClass.Cruiser);
            var sponson = combat.Structure.Modules.First(m => m.Kind == HullModuleKind.Sponson);
            combat.Structure.Strike(sponson.Centre, Cruiser.Hull);
            Assert.That(combat.Structure.Intact, Is.False);
            combat.Repair(1);
            Assert.That(combat.Structure.Intact, Is.True);
        }
    }
}
