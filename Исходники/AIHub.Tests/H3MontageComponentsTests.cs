using System.IO;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class H3MontageComponentsTests
{
    [TestMethod]
    public void MontageReusesSharedModelsAndAnalysisLibrariesWithoutAnotherSetOfWeights()
    {
        var root = Path.GetTempPath(); var original = H3MusicComponents.Cards(root);
        var montage = H3MontageComponents.Cards(root);
        Assert.AreEqual(original.Count + 2, montage.Count);
        foreach (var card in original)
        {
            var reused = montage.Single(item => item.ModelArtifactId == card.ModelArtifactId);
            Assert.AreEqual(card.InstallDirectory, reused.InstallDirectory);
            Assert.AreEqual(card.Revision, reused.Revision);
            CollectionAssert.AreEqual(card.Files.Select(file => file.Sha256).ToArray(), reused.Files.Select(file => file.Sha256).ToArray());
        }
        var source = montage.Single(item => item.ModelArtifactId == H3MontageComponents.SourceId);
        var originalAnalysis = GlitchframeComponents.Cards(root).Single(card => card.ModelArtifactId == H3MontageComponents.AnalysisLibrariesId);
        var reusedAnalysis = montage.Single(card => card.ModelArtifactId == H3MontageComponents.AnalysisLibrariesId);
        Assert.AreEqual(originalAnalysis.InstallDirectory, reusedAnalysis.InstallDirectory);
        Assert.AreEqual(originalAnalysis.Revision, reusedAnalysis.Revision);
        CollectionAssert.AreEqual(originalAnalysis.Files.Select(file => file.Sha256).ToArray(), reusedAnalysis.Files.Select(file => file.Sha256).ToArray());
        Assert.IsFalse(montage.Any(card => card.ModelArtifactId == "song-clip-glitch-sdxl" || card.ModelArtifactId == "song-clip-glitch-source"));
        Assert.AreEqual(H3MontageComponents.SourceRevision, source.Revision);
        Assert.AreEqual("MIT", source.License);
        Assert.AreEqual(montage.Sum(card => card.TotalBytes), H3MontageComponents.TotalBytes);
        Assert.AreEqual(320245L, source.TotalBytes);
        Assert.IsFalse(source.Files.Any(file => file.RelativePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)),
            "Changing the H3 editing mode must not request another set of model weights.");
    }

    [TestMethod]
    public void EverySourceFileHasTheSameImmutableAcquisitionAndRuntimeIdentity()
    {
        var source = H3MontageComponents.Cards(Path.GetTempPath()).Single(card => card.ModelArtifactId == H3MontageComponents.SourceId);
        Assert.AreEqual(source.Files.Count, H3MontageComponents.SourceTree.Length);
        foreach (var file in source.Files)
        {
            Assert.IsTrue(file.IsRequired);
            Assert.IsTrue(file.SourceUrl.Contains("/" + H3MontageComponents.SourceRevision + "/", StringComparison.Ordinal));
            Assert.AreEqual(64, file.Sha256.Length); Assert.IsTrue(file.Sha256.All(Uri.IsHexDigit));
            var runtime = H3MontageComponents.SourceTree.Single(item => item.Path == file.RelativePath);
            Assert.AreEqual(file.SizeBytes, runtime.Size); Assert.AreEqual(file.Sha256, runtime.Sha256);
        }
        foreach (var required in new[] { "LICENSE", "README.md", "__init__.py", "director_cloud.py", "director_local.py",
            "director_parser.py", "director_state.py", "schemas.py", "runtime_compat.py", "audio_preprocessor.py",
            "motion_loader.py", "pyproject.toml", "requirements.txt", "requirements-local-director.txt",
            "motion_library/metadata.json", "workflows/minimax_h3_any_song_v20.json" })
            Assert.IsTrue(source.Files.Any(file => file.RelativePath == required), "Required pinned source missing: " + required);
    }
}
