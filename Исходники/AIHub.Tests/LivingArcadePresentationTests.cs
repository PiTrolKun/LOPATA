using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadePresentationTests
{
    private static readonly int[] Seeds = [0, 1, -1, 1731, int.MaxValue, int.MinValue];

    [TestMethod]
    public void EveryThreeStageChapterContainsBothViewsIncludingTheFirstChapter()
    {
        foreach (var seed in Seeds)
        for (var chapter = 0; chapter < 128; chapter++)
        {
            var modes = Chapter(seed, chapter);
            Assert.IsTrue(modes.Contains(LivingArcadeViewMode.TopDown), $"Seed {seed}, chapter {chapter} needs a 2D stage.");
            Assert.IsTrue(modes.Contains(LivingArcadeViewMode.Perspective), $"Seed {seed}, chapter {chapter} needs a perspective stage.");
            Assert.IsTrue(modes.All(mode => mode is LivingArcadeViewMode.TopDown or LivingArcadeViewMode.Perspective));
        }
    }

    [TestMethod]
    public void BothExtraViewsAndEveryOrderOccurWithoutLockingTheTutorialTo2D()
    {
        var arrangements = new HashSet<string>();
        var firstViews = new HashSet<LivingArcadeViewMode>();
        foreach (var seed in Enumerable.Range(-128, 256))
        {
            firstViews.Add(LivingArcadePresentationPolicy.ModeFor(seed, 1));
            for (var chapter = 0; chapter < 24; chapter++)
                arrangements.Add(string.Concat(Chapter(seed, chapter).Select(mode => mode == LivingArcadeViewMode.Perspective ? '1' : '0')));
        }
        CollectionAssert.AreEquivalent(new[] { "001", "010", "100", "011", "101", "110" }, arrangements.ToArray());
        Assert.AreEqual(2, firstViews.Count, "The authored tutorial keeps its gameplay but participates in the same view selection.");
    }

    [TestMethod]
    public void ModeIsStableAcrossLookupOrderRetriesAndUnrelatedSeeds()
    {
        foreach (var seed in Seeds)
        {
            var expected = Enumerable.Range(1, 384).Select(level => LivingArcadePresentationPolicy.ModeFor(seed, level)).ToArray();
            for (var retry = 0; retry < 4; retry++)
            for (var level = expected.Length; level >= 1; level--)
            {
                _ = LivingArcadePresentationPolicy.ModeFor(unchecked(seed + 99), level + 1);
                Assert.AreEqual(expected[level - 1], LivingArcadePresentationPolicy.ModeFor(seed, level));
            }
            Assert.IsTrue(expected.Contains(LivingArcadeViewMode.TopDown) && expected.Contains(LivingArcadeViewMode.Perspective));
        }
    }

    [TestMethod]
    public void ExtremeIntegersAreSafeAndNonpositiveLevelsUseTheFirstStage()
    {
        foreach (var seed in Seeds)
        {
            var first = LivingArcadePresentationPolicy.ModeFor(seed, 1);
            foreach (var invalidLevel in new[] { int.MinValue, -1, 0 })
                Assert.AreEqual(first, LivingArcadePresentationPolicy.ModeFor(seed, invalidLevel));
            foreach (var level in new[] { 1, 2, 3, 4, int.MaxValue - 2, int.MaxValue - 1, int.MaxValue })
            {
                var mode = LivingArcadePresentationPolicy.ModeFor(seed, level);
                Assert.IsTrue(mode is LivingArcadeViewMode.TopDown or LivingArcadeViewMode.Perspective);
                Assert.AreEqual(mode, LivingArcadePresentationPolicy.ModeFor(seed, level));
            }
        }
    }

    [TestMethod]
    public void ExistingSchemaOneSavesReopenWithTheSameViewsWithoutRewritingAnyBytes()
    {
        foreach (var seed in Seeds)
        {
            var directory = Path.Combine(Path.GetTempPath(), "arcade-presentation-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                var run = new LivingArcadeRunStore(directory).Create("Existing run", seed);
                Assert.AreEqual(1, run.SchemaVersion);
                var before = Files(directory);
                var expected = Enumerable.Range(1, 48).Select(level => LivingArcadePresentationPolicy.ModeFor(run.Seed, level)).ToArray();
                var reopened = new LivingArcadeRunStore(directory).Load(run.Id)!;
                var actual = Enumerable.Range(1, 48).Select(level => LivingArcadePresentationPolicy.ModeFor(reopened.Seed, level)).ToArray();
                CollectionAssert.AreEqual(expected, actual);
                Assert.AreEqual(run.Revision, reopened.Revision);
                var after = Files(directory);
                CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
                foreach (var name in before.Keys)
                    CollectionAssert.AreEqual(before[name], after[name], "Presentation lookup must not migrate or rewrite " + name);
            }
            finally { DeleteFixture(directory); }
        }
    }

    [TestMethod]
    public void PresentationLookupsDoNotAffectBattleSimulation()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(1731);
        var first = new LivingArcadeSimulation(level, new LivingArcadeHeroState());
        var second = new LivingArcadeSimulation(level, new LivingArcadeHeroState());
        for (var frame = 0; frame < 240; frame++)
        {
            var input = new LivingArcadeInput(Left: frame < 60, Right: frame >= 120 && frame < 180);
            first.Advance(1d / 60, input);
            _ = LivingArcadePresentationPolicy.ModeFor(1731, frame % 24 + 1);
            _ = LivingArcadePresentationPolicy.ModeFor(int.MinValue, int.MaxValue - frame);
            second.Advance(1d / 60, input);
        }
        Assert.AreEqual(JsonSerializer.Serialize(first.Snapshot), JsonSerializer.Serialize(second.Snapshot));
        Assert.AreEqual(JsonSerializer.Serialize(first.ResultHero), JsonSerializer.Serialize(second.ResultHero));
    }

    private static LivingArcadeViewMode[] Chapter(int seed, int chapter) => Enumerable.Range(chapter * 3 + 1, 3)
        .Select(level => LivingArcadePresentationPolicy.ModeFor(seed, level)).ToArray();

    private static Dictionary<string, byte[]> Files(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);

    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("arcade-presentation-save-", StringComparison.Ordinal)) Directory.Delete(full, true);
    }
}
