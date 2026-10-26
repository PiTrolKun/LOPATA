using AIHub.Services.VideoEditor;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoNarratorBatchTests
{
    [TestMethod]
    public void SequentialFragmentsReuseOnlyTheCurrentModelAndDisposeItOnSwitch()
    {
        var registry = new VideoNarratorRegistry(); var created = new List<FakeNarrator>();
        foreach (var id in new[] { "first", "second" }) registry.Register(id, () => { var model = new FakeNarrator(); created.Add(model); return model; });
        using (var batch = new VideoNarratorBatch(registry))
        {
            var first = batch.Get("first"); Assert.AreSame(first, batch.Get("first")); Assert.HasCount(1, created);
            var second = batch.Get("second"); Assert.IsTrue(created[0].Disposed); Assert.AreSame(second, batch.Get("second"));
            Assert.IsFalse(created[1].Disposed); Assert.HasCount(2, created);
        }
        Assert.IsTrue(created.All(n => n.Disposed));
    }

    [TestMethod]
    public void OrpheusContextIsSizedForPacketsInsteadOfTheWholeDocument()
    {
        Assert.AreEqual(4096, VideoNarratorDevice.ContextTokens("orpheus3b", "Hello"));
        Assert.AreEqual(4096, VideoNarratorDevice.ContextTokens("orpheus3b", new string('a', 120000)));
    }

    [TestMethod]
    public void PreparedBackendMatrixKeepsCompatibleNonCudaPathsAndCpuFallback()
    {
        foreach (var id in VideoNarratorCatalog.Models.Select(m => m.Id))
            Assert.Contains(HardwareRuntimeCatalog.PythonCpuId, VideoNarratorDevice.SupportedProfiles(id));
        Assert.Contains(HardwareRuntimeCatalog.PythonXpuId, VideoNarratorDevice.SupportedProfiles("qwen3-17b"));
        Assert.Contains(HardwareRuntimeCatalog.PythonRocmId, VideoNarratorDevice.SupportedProfiles("kokoro-sveta"));
        Assert.DoesNotContain(HardwareRuntimeCatalog.PythonXpuId, VideoNarratorDevice.SupportedProfiles("supertonic3"));
        Assert.DoesNotContain(HardwareRuntimeCatalog.PythonRocmId, VideoNarratorDevice.SupportedProfiles("kitten2"));
    }

    private sealed class FakeNarrator : IVideoNarrator
    {
        public bool Disposed { get; private set; }
        public IReadOnlyList<VideoVoice> Voices => [];
        public Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
