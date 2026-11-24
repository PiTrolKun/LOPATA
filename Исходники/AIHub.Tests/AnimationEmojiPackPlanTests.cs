using System.IO;
using AIHub.Services.AnimatedAssets;

namespace AIHub.Tests;

[TestClass]
public sealed class AnimationEmojiPackPlanTests
{
    [TestMethod]
    public void FullCatalogBuildsOneTargetPerEmojiInCatalogOrder()
    {
        var expected = AnimationEmojiCatalog.Entries.Select(entry => entry.Emoji).ToArray();
        var project = new AnimationProject { Reference = [4, 5, 6] };

        var plan = AnimationEmojiPackPlan.Build(project, expected.Reverse().Append(expected[0]), 2.75,
            entry => entry.RussianName, Prompt);

        Assert.IsTrue(plan.Count >= 90);
        CollectionAssert.AreEqual(expected, plan.Select(card => card.TargetEmoji).ToArray());
        Assert.AreEqual(plan.Count, plan.Select(card => card.Id).Distinct().Count());
        Assert.IsTrue(plan.All(card => card.Kind == "reaction" && card.Motion == "frames" && card.Duration == 2.75
            && card.Poses is [{ Time: 1.375, Image: null }] && card.Poses[0].Text == card.TargetEmoji));
        Assert.AreEqual(0, project.Cards.Count);
    }

    [TestMethod]
    public void RetryCoalescesAliasesAndRetimesCompletedArtworkWithoutRedrawing()
    {
        byte[] artwork = [1, 2, 3];
        var completed = new AnimationPose
        {
            Time = 1, Text = "☺️", Prompt = "Actual earlier artist instruction",
            Image = artwork, Models = [new("artist", "test-artist", "fixture")]
        };
        var ready = new AnimationCard { Name = "Earlier name", TargetEmoji = "☺", Poses = [completed] };
        var missing = new AnimationCard { TargetEmoji = "☺️", Poses = [new() { Time = 1 }] };
        var project = new AnimationProject { Reference = [4, 5, 6], Cards = [missing, ready] };

        var plan = AnimationEmojiPackPlan.Build(project, ["☺", "☺️"], 4, entry => entry.EnglishName,
            _ => throw new InvalidOperationException("The completed pose should not be replanned."));

        var card = plan.Single();
        var pose = card.Poses.Single();
        Assert.IsTrue(AnimationEmojiPackPlan.IsReady(card));
        Assert.AreEqual(ready.Id, card.Id);
        Assert.AreEqual(completed.Id, pose.Id);
        Assert.AreEqual(AnimationEmojiCatalog.Find("☺️")!.Emoji, card.TargetEmoji);
        Assert.AreEqual(4d, card.Duration);
        Assert.AreEqual(2d, pose.Time);
        Assert.AreSame(artwork, pose.Image);
        Assert.AreEqual(completed.Prompt, pose.Prompt);
        Assert.AreEqual(completed.Models.Single(), pose.Models.Single());
        Assert.AreNotSame(ready, card);
        Assert.AreNotSame(completed, pose);
        Assert.AreEqual(2d, ready.Duration);
        Assert.AreEqual(1d, completed.Time);
        Assert.AreEqual(2, project.Cards.Count);
    }

    [TestMethod]
    public void RetryReusesPendingIdentityAndPlansAVisiblePulseForTheReferenceEmoji()
    {
        var pending = new AnimationPose { Time = 1, Text = "☺", Prompt = "Old draft" };
        var ready = new AnimationCard { TargetEmoji = "☺", Poses = [pending] };
        var project = new AnimationProject { ReferenceEmoji = "☺", Cards = [ready] };

        var plan = AnimationEmojiPackPlan.Build(project, ["☺️", "🙂"], 2, entry => entry.RussianName, Prompt);
        var same = plan.Single(card => card.TargetEmoji == AnimationEmojiCatalog.Find("☺️")!.Emoji);
        var different = plan.Single(card => card.TargetEmoji == "🙂");

        Assert.AreEqual(ready.Id, same.Id);
        Assert.AreEqual(pending.Id, same.Poses[0].Id);
        StringAssert.Contains(same.Poses[0].Prompt, "Keep the same emotion");
        StringAssert.Contains(same.Poses[0].Prompt, "visibly distinct variation");
        Assert.AreEqual(Prompt("🙂"), different.Poses[0].Prompt);
        Assert.AreEqual("Old draft", pending.Prompt);
        Assert.IsFalse(AnimationEmojiPackPlan.IsReady(same));
    }

    [TestMethod]
    public void ChangedReferenceLeavesPriorResultsUntouchedAndCreatesMissingTarget()
    {
        var previous = new AnimationCard
        {
            TargetEmoji = "🙂",
            Poses = [new() { Time = 1, Image = [1], Prompt = "Pose made with earlier reference" }]
        };
        var project = new AnimationProject { Reference = [2], Cards = [previous] };

        var created = AnimationEmojiPackPlan.Build(project, ["🙂"], 2, entry => entry.EnglishName, Prompt,
            reuseExisting: false).Single();

        Assert.AreNotEqual(previous.Id, created.Id);
        Assert.IsFalse(AnimationEmojiPackPlan.IsReady(created));
        Assert.IsTrue(AnimationEmojiPackPlan.IsReady(previous));
        Assert.AreSame(previous, project.Cards.Single());
    }

    [TestMethod]
    public void InvalidSelectionOrDurationFailsBeforeAnyPlanIsCreated()
    {
        var project = new AnimationProject();
        var empty = Assert.ThrowsExactly<InvalidDataException>(() =>
            AnimationEmojiPackPlan.Build(project, [], 2, entry => entry.EnglishName, Prompt));
        Assert.AreEqual("Animation.AutoPackNeedSelection", empty.Message);
        var unknown = Assert.ThrowsExactly<InvalidDataException>(() =>
            AnimationEmojiPackPlan.Build(project, ["🙂", "not an emoji"], 2, entry => entry.EnglishName, Prompt));
        Assert.AreEqual("Animation.AutoPackUnknownEmoji", unknown.Message);
        var invalidTiming = Assert.ThrowsExactly<InvalidDataException>(() =>
            AnimationEmojiPackPlan.Build(project, ["🙂"], double.NaN, entry => entry.EnglishName, Prompt));
        Assert.AreEqual("AnimatedAssets.Error.Duration", invalidTiming.Message);
        Assert.AreEqual(0, project.Cards.Count);
    }

    private static string Prompt(string emoji) => $"Draw reaction {emoji}.";
}
