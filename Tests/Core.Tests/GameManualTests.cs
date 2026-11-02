using System.Linq;
using NUnit.Framework;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod.Core.Tests
{
    /// <summary>The in-game manual (U.SPC-T497): the chapters the owner asked for, each with something to say, and every key group printed.</summary>
    [TestFixture]
    public class GameManualTests
    {
        [Test]
        public void TheManualHasTheTicketsChaptersInOrder()
        {
            var titles = GameManual.Chapters.Select(c => c.Title).ToList();
            string[] asked =
            {
                "Getting started", "The god view", "Flying", "Ships: strike craft", "Ships: capital ships",
                "Ships: escorts and sub-capitals", "Ships: logistics and civilian craft", "Combat and boarding", "Trade",
                "Diplomacy and the law", "Fleets and command", "The galaxy and travel", "Settings",
            };
            int last = -1;
            foreach (var t in asked)
            {
                int i = titles.IndexOf(t);
                Assert.That(i, Is.GreaterThan(last), t + " is missing or out of order");
                last = i;
            }
            Assert.That(titles.Last(), Is.EqualTo(GameManual.EveryKeyTitle));
            Assert.That(titles.Distinct().Count(), Is.EqualTo(titles.Count), "two chapters share a title");
        }

        [Test]
        public void EveryChapterExplainsSomething()
        {
            foreach (var c in GameManual.Chapters)
            {
                Assert.That(c.Summary.Trim(), Is.Not.Empty, c.Title);
                if (c.Title == GameManual.EveryKeyTitle) continue;
                Assert.That(c.Topics, Is.Not.Empty, c.Title);
                foreach (var t in c.Topics)
                {
                    Assert.That(t.Heading.Trim(), Is.Not.Empty, c.Title);
                    Assert.That(t.Text.Trim().Length, Is.GreaterThan(20), c.Title + " / " + t.Heading);
                }
            }
        }

        [Test]
        public void EveryKeyGroupIsPrintedByAChapterAndNamedGroupsExist()
        {
            var named = GameManual.Chapters.Where(c => c.Title != GameManual.EveryKeyTitle).SelectMany(c => c.ControlGroups).ToList();
            foreach (var name in named)
                Assert.That(ControlsHelp.Sections.Any(s => s.Title == name), Is.True, "no key group " + name);
            foreach (var s in ControlsHelp.Sections)
                Assert.That(named, Does.Contain(s.Title), s.Title + " is in no chapter");
            Assert.That(GameManual.ControlsOf(GameManual.Find(GameManual.EveryKeyTitle)).Count(), Is.EqualTo(ControlsHelp.Sections.Count));
        }
    }
}
