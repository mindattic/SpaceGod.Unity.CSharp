using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>U.SPC-T595: the skybox catalogue, its per-system memory, the panorama frames and the seeded sky.</summary>
    public class SkyboxTests
    {
        static string Repo
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "UnitySpace"))) dir = dir.Parent;
                return dir?.FullName ?? throw new DirectoryNotFoundException("Assets/UnitySpace not found above " + AppContext.BaseDirectory);
            }
        }

        [Test]
        public void EveryShippedPanoramaIsInResourcesWithAnImportSettingAndASourceRow()
        {
            string folder = Path.Combine(Repo, "Assets", "UnitySpace", "Resources", "Skyboxes");
            string sources = File.ReadAllText(Path.Combine(folder, "SOURCES.md"));
            foreach (var s in SkyboxCatalog.BuiltIn.Where(s => s.Kind == SkyKind.Image))
            {
                Assert.That(s.Path, Does.StartWith(SkyboxCatalog.ResourceFolder), s.Id);
                string file = Path.GetFileName(s.Path) + ".jpg";
                Assert.That(File.Exists(Path.Combine(folder, file)), Is.True, file);
                string meta = File.ReadAllText(Path.Combine(folder, file + ".meta"));
                Assert.That(meta, Does.Contain("maxTextureSize: 8192"), file + " imports at full size");
                Assert.That(sources, Does.Contain("`" + file + "`"), file + " has its licence recorded");
                Assert.That(s.Licence, Is.Not.Empty, s.Id);
                Assert.That(s.Credit, Is.Not.Empty, s.Id);
            }
        }

        [Test]
        public void IdsAreUniqueAndStepWrapsBothWays()
        {
            var all = SkyboxCatalog.BuiltIn;
            Assert.That(all.Select(s => s.Id).Distinct().Count(), Is.EqualTo(all.Count));
            var first = SkyboxCatalog.All[0];
            Assert.That(SkyboxCatalog.Step(first, 1), Is.SameAs(SkyboxCatalog.All[1]));
            Assert.That(SkyboxCatalog.Step(first, -1), Is.SameAs(SkyboxCatalog.All[SkyboxCatalog.All.Count - 1]));
            Assert.That(SkyboxCatalog.Register(new SkyboxSpec(first.Id, "dupe", SkyKind.Starfield, "", SkyFrame.Ecliptic, true, "", "")), Is.False);
        }

        [Test]
        public void SolDefaultsToTheRealSkyAndOtherSystemsToTheirOwnNebulae()
        {
            Assert.That(SkyboxCatalog.DefaultFor("Sol System").Id, Is.EqualTo(SkyboxCatalog.DeepStarMapId));
            Assert.That(SkyboxCatalog.DefaultFor("Alpha Centauri").Id, Is.EqualTo(SkyboxCatalog.SeededNebulaId));
        }

        [Test]
        public void EachSystemKeepsItsOwnSkyInTheProfile()
        {
            var store = new MemorySettingStore();
            var memory = new SkyboxMemory(() => store);
            var sol = memory.Cycle("Sol System");
            Assert.That(sol, Is.Not.SameAs(SkyboxCatalog.DefaultFor("Sol System")));
            memory.Set("Barnard's Star; b=c", SkyboxCatalog.Find(SkyboxCatalog.StarfieldId));
            Assert.That(memory.For("Alpha Centauri"), Is.SameAs(SkyboxCatalog.DefaultFor("Alpha Centauri")), "a system never set keeps its default");

            var reread = new SkyboxMemory(() => store);
            Assert.That(reread.For("Sol System"), Is.SameAs(sol));
            Assert.That(reread.For("Barnard's Star; b=c").Id, Is.EqualTo(SkyboxCatalog.StarfieldId), "names with ; and = survive");
            reread.Clear();
            Assert.That(store.ReadSetting(SkyboxMemory.Key), Is.Null);
        }

        [Test]
        public void ARealSkyMapPutsTheGalacticCentreWhereItIs()
        {
            // Sgr A*: RA 266.4, Dec -28.9 -> NASA's map at u = 0.5 - 266.4/360 + 1, v = 0.5 - 28.9/180.
            var gc = Astro.PoleFromRaDec(266.40499, -28.93617);
            SkyboxCatalog.PanoramaUv(SkyFrame.Equatorial, gc, out double u, out double v);
            Assert.That(u, Is.EqualTo(1.5 - 266.40499 / 360).Within(1e-6));
            Assert.That(v, Is.EqualTo(0.5 - 28.93617 / 180).Within(1e-6));
            SkyboxCatalog.PanoramaUv(SkyFrame.Galactic, gc, out u, out v);
            Assert.That(u, Is.EqualTo(0.5).Within(1e-6));
            Assert.That(v, Is.EqualTo(0.5).Within(1e-6));
            // The north galactic pole is the top of ESO's panorama.
            SkyboxCatalog.PanoramaUv(SkyFrame.Galactic, Astro.PoleFromRaDec(192.85948, 27.12825), out _, out v);
            Assert.That(v, Is.EqualTo(1).Within(1e-6));
        }

        [Test]
        public void ASystemsSeededSkyIsTheSameEveryTimeAndDiffersFromAnother()
        {
            var a = ProceduralSky.BakePanorama(ProceduralSky.SeedFor("Alpha Centauri"), 32, out var mips);
            var b = ProceduralSky.BakePanorama(ProceduralSky.SeedFor("Alpha Centauri"), 32, out _);
            var c = ProceduralSky.BakePanorama(ProceduralSky.SeedFor("Tau Ceti"), 32, out _);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a, Is.Not.EqualTo(c));
            Assert.That(mips.Length, Is.EqualTo(6), "32x16 down to 1x1");
            Assert.That(a.Length, Is.EqualTo(mips[mips.Length - 1] + 4));
            Assert.That(ProceduralSky.Layers(ProceduralSky.SeedFor("Sol System")).Count, Is.InRange(1, 6));
            Assert.Throws<ArgumentException>(() => ProceduralSky.BakePanorama(1, 30, out _));
        }
    }
}
