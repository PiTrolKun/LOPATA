using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;
[TestClass, DoNotParallelize]
public sealed class VideoH3LiteRuntimeTests
{
    [TestMethod]
    public void ProfilesKeepIndependentSettingsRunsAndSchemaThreeRoundTrips()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        var heavy = VideoH3WorkspaceState.Ensure(p); heavy.Settings.Seed = 72;
        var light = VideoH3WorkspaceState.Ensure(p, VideoH3LiteCatalog.ModelId); light.Settings.Seed = 73;
        Assert.AreEqual("gpu", light.Settings.MemoryMode); Assert.IsTrue(light.Settings.Width * light.Settings.Height < 500000);
        var a = VideoH3Job.Create(p, f.File("x.lopataAIVid"), f.File("models"), 4);
        var b = VideoH3Job.Create(p, f.File("x.lopataAIVid"), f.File("models"), 4, VideoH3LiteCatalog.ModelId);
        Assert.AreNotEqual(a.Run.Id, b.Run.Id); Assert.AreNotEqual(a.Run.InputHash, b.Run.InputHash);
        Assert.AreEqual(a.Run.Id, VideoH3Job.Create(p, a.ArchivePath, a.ModelsRoot, 4).Run.Id);
        Assert.AreEqual(b.Run.Id, VideoH3Job.Create(p, b.ArchivePath, b.ModelsRoot, 4, b.ModelId).Run.Id);
        VideoPreparationArchive.Save(p, a.ArchivePath);
        using (var zip = ZipFile.OpenRead(a.ArchivePath))
        using (var json = JsonDocument.Parse(zip.GetEntry("project.json")!.Open())) Assert.AreEqual(4, json.RootElement.GetProperty("Schema").GetInt32());
        var restored = VideoPreparationArchive.Load(a.ArchivePath, f.File("restored"));
        Assert.AreEqual(72L, restored.Generator!.H3!.Settings.Seed); Assert.AreEqual(73L, restored.Generator.H3Lite!.Settings.Seed);
        Assert.AreEqual(b.Run.Id, restored.Generator.H3Lite.Runs.Single().Id);
        light.Runs.Add(a.Run); Assert.Throws<InvalidDataException>(p.Validate);
    }
    [TestMethod]
    public void BindingNeverUsesHeavyEncoderAndRejectsUnsupportedUpscaling()
    {
        var files = VideoH3Profile.Models(true); Assert.IsTrue(files["Diffusion"].Contains("w4a8"));
        Assert.IsTrue(files["Encoder"].Contains("4b")); Assert.IsFalse(files.Values.Any(v => v.Contains("32b")));
        Assert.IsTrue(files["Projection"].Contains("3.1-mlp"));
        using var input = typeof(VideoH3Job).Assembly.GetManifestResourceStream("AIHub.VideoH3LiteSourceTree")!;
        var tree = JsonSerializer.Deserialize<AIHub.Services.PinnedTreeFile[]>(input)!;
        Assert.IsTrue(tree.Any(f => f.Path.EndsWith("/clipproj_nodes.py"))); Assert.IsTrue(tree.All(f => f.Sha256.Length == 64 && f.Size >= 0));
        var p = VideoPreparationFinishTests.ReadyProject();
        VideoH3WorkspaceState.Ensure(p, VideoH3LiteCatalog.ModelId).Settings.Upscale = true;
        Assert.Throws<InvalidDataException>(() => VideoH3Job.Create(p, "x", Path.GetTempPath(), 4, VideoH3LiteCatalog.ModelId));
        Assert.IsNull(p.Generator!.H3);
    }
}
