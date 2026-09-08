using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageBatchProfileTests
{
    [TestMethod]
    public void LegacyAndUnknownProfiles_DoNotBecomeBeta()
    {
        var old = JsonSerializer.Deserialize<ImageBatchJob>("{}")!;
        Assert.AreEqual(ImageAnalysisBundleCatalog.LightId, old.BundleId);
        old.ModelRevision = OmniLlamaProfile.Alpha.Revision;
        Assert.IsTrue(ImageBatchProfiles.CanContinue(old));
        old.BundleId = "unknown";
        Assert.IsFalse(ImageBatchProfiles.CanContinue(old));
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => ImageBatchProfiles.Session(old));
    }

    [TestMethod]
    public void BetaRoundTrip_RoutesBothStagesAndPreservesRevision()
    {
        var job = ImageBatchProfiles.Create(ImageAnalysisBundleCatalog.MediumId, "ru");
        job = JsonSerializer.Deserialize<ImageBatchJob>(JsonSerializer.Serialize(job))!;
        var session = ImageBatchProfiles.Session(job);
        using var runtime = new OmniLlamaRuntimeService(new ManagedModelLibraryStore(), OmniLlamaProfile.ForBundle(session.BundleId));
        Assert.AreEqual(ImageAnalysisPipelineIds.OmniBeta, session.PipelineId);
        Assert.AreEqual(runtime.ModelRevision, session.ModelRevision);
        Assert.AreEqual(runtime.ModelId, session.ModelId);
        Assert.IsTrue(ImageBatchProfiles.CanContinue(job));
        job.ModelRevision = "old-revision";
        Assert.IsFalse(ImageBatchProfiles.CanContinue(job));
        Assert.AreEqual("old-revision", ImageBatchProfiles.Session(job).ModelRevision);
        Assert.IsTrue(ImageBatchProfiles.IsEnabled(ImageAnalysisBundleCatalog.HeavyId));
    }

    [TestMethod]
    public void GammaRoundTrip_PreservesItsRuntimeAndRevision()
    {
        var job = ImageBatchProfiles.Create(ImageAnalysisBundleCatalog.HeavyId, "ru");
        job = JsonSerializer.Deserialize<ImageBatchJob>(JsonSerializer.Serialize(job))!;
        var session = ImageBatchProfiles.Session(job);
        var profile = OmniLlamaProfile.ForBundle(session.BundleId)!;
        using var runtime = new OmniLlamaRuntimeService(new ManagedModelLibraryStore(), profile);
        Assert.AreEqual(ImageAnalysisPipelineIds.OmniGamma, session.PipelineId);
        Assert.AreEqual(ImageAnalysisBundleCatalog.HeavyId, job.BundleId);
        Assert.AreEqual(runtime.ModelRevision, session.ModelRevision);
        Assert.AreEqual(runtime.ModelId, session.ModelId);
        Assert.AreSame(OmniLlamaProfile.Gamma, profile);
        Assert.IsTrue(ImageBatchProfiles.CanContinue(job));
        job.ModelRevision = "old-revision";
        Assert.IsFalse(ImageBatchProfiles.CanContinue(job));
        Assert.AreEqual("old-revision", ImageBatchProfiles.Session(job).ModelRevision);
    }
}
