using System.IO;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoAnalysisFramesTests
{
    [TestMethod]
    public void TinyLeadingGapDoesNotRejectTheEntireNarrationFragment()
    {
        var p = new VideoProject(); var a = new VideoAsset { Path = Path.GetFullPath("screen.mp4"), Kind = "video", Duration = 20, VideoStream = 0 }; p.Assets.Add(a);
        p.Tracks[0].Clips.Add(new() { AssetId = a.Id, Start = 1d / 110, SourceIn = 1d / 110, Duration = 5.4 - 1d / 110 });
        var plans = VideoAnalysisFrames.Plan(p, new() { Start = 0, End = 5.4 });
        Assert.HasCount(3, plans); Assert.IsTrue(plans.All(f => f.TimelineTime >= p.Tracks[0].Clips[0].Start)); Assert.AreEqual(.05, plans[0].SourceTime, .000001);
    }
    [TestMethod]
    public void MergedFragmentSamplesTheCorrespondingClipAndSourceSpeed()
    {
        var p = new VideoProject(); var a = new VideoAsset { Path = Path.GetFullPath("one.mp4"), Kind = "video", Duration = 20, VideoStream = 0 }; var b = new VideoAsset { Path = Path.GetFullPath("two.mp4"), Kind = "video", Duration = 20, VideoStream = 0 }; p.Assets.AddRange([a,b]);
        p.Tracks[0].Clips.AddRange([new() { AssetId = a.Id, Duration = 3 }, new() { AssetId = b.Id, Start = 3, SourceIn = 1, Speed = 2, Duration = 3 }]);
        var plans = VideoAnalysisFrames.Plan(p, new() { Start = 0, End = 6 }); Assert.AreEqual(a.Id, plans[0].AssetId); Assert.AreEqual(b.Id, plans[1].AssetId); Assert.AreEqual(1, plans[1].SourceTime); Assert.AreEqual(6.9, plans[2].SourceTime, .000001);
    }
    [TestMethod]
    public void PartialOverlapUsesRealCoveredTimesAndEmptyIntervalsFailClearly()
    {
        var p = new VideoProject(); var a = new VideoAsset { Path = Path.GetFullPath("screen.mp4"), Kind = "video", Duration = 20, VideoStream = 0 }; p.Assets.Add(a); p.Tracks[0].Clips.Add(new() { AssetId = a.Id, Start = 1, Duration = 1 });
        var plans = VideoAnalysisFrames.Plan(p, new() { Start = 0, End = 3 }); Assert.IsTrue(plans.All(f => f.TimelineTime is >= 1 and < 2 && f.SourceTime is >= 0 and < 1));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => VideoAnalysisFrames.Plan(p, new() { Start = 3, End = 4 })); Assert.AreEqual("VideoEditor.NoVideoInFragment", error.Message);
    }
}
