using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoVaceCoverageTests
{
    [TestMethod]
    public void ExistingWholeReceiptCannotHideIncompleteDeliveryOfALaterPlan()
    {
        var job = Fixture();
        job.Run.Takes.Add(new("first", "opening", "first.mp4", 1, 81, 0, ""));
        job.Run.Takes.Add(new("second", "ending", "second.mp4", 2, 81, 9, "first"));
        job.Run.Results.Add(new(Guid.NewGuid().ToString("N"), "vace-run/" + job.Run.Id + "/whole",
            job.Run.Takes.Sum(t => t.DeliveredSeconds), DateTimeOffset.UtcNow, [new("fixture", "Fixture", "fixture", "test")]));
        Assert.IsFalse(VideoVaceRunner.CompleteCoverage(job), "The second native pass has 81 frames but delivers only 72 after removing its prefix; its 5-second plan needs 80.");
        job.Run.Takes.Add(new("third", "ending", "third.mp4", 3, 17, 9, "second"));
        Assert.IsTrue(VideoVaceRunner.CompleteCoverage(job), "A confirmed extension supplying the missing eight frames completes that plan.");
    }

    [TestMethod]
    public void ExcessFramesOfOnePlanCannotCompensateForAnUnstartedPlan()
    {
        var job = Fixture();
        job.Run.Takes.Add(new("first", "opening", "first.mp4", 1, 81, 0, ""));
        job.Run.Takes.Add(new("extra", "opening", "extra.mp4", 2, 81, 9, "first"));
        job.Run.Takes.Add(new("extra2", "opening", "extra2.mp4", 3, 81, 9, "extra"));
        Assert.IsFalse(VideoVaceRunner.CompleteCoverage(job));
    }

    [TestMethod]
    public void AWholeOutputCannotCompleteEmptyUnknownOrNonFiniteInputs()
    {
        var job = Fixture();
        Assert.IsFalse(VideoVaceRunner.CompleteCoverage(job with { Items = [] }));
        Assert.IsFalse(VideoVaceRunner.CompleteCoverage(job with { Items = [job.Items[0] with { Seconds = double.NaN }] }));
        job.Run.Takes.Add(new("unknown", "not-in-project", "unknown.mp4", 1, 81, 0, ""));
        Assert.IsFalse(VideoVaceRunner.CompleteCoverage(job));
    }

    private static VideoVaceJob Fixture() => new(Guid.NewGuid().ToString("N"), "fixture.lopataAIVid", "models", 4, new(),
        [new("opening", "scene", "Opening action", 5, 1, []), new("ending", "scene", "Ending action", 5, 2, [])], new());
}
