using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.ExternalAgents;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;
using AIHub.Services.VideoUpscale;

namespace AIHub.Tests;

[TestClass]
public sealed class ExternalAgentVideoOptionsTests
{
    [TestMethod]
    public async Task AgentCatalogUsesCurrentMechanicsAndNativeVideoChoices()
    {
        var info = JsonSerializer.SerializeToElement(await AgentTools.CallAsync("lopata_info", Parse("{}")), AgentProtocol.Json);
        Assert.AreEqual(64, info.GetProperty("capabilitiesRevision").GetString()!.Length);
        CollectionAssert.AreEquivalent(ScenarioNavigationCatalog.CloudTags.Select(x => x.Id).ToArray(),
            info.GetProperty("mechanics").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray());
        var song = Description(ScenarioNavigationCatalog.SongClip);
        var options = song.GetProperty("dataSchema").GetProperty("properties");
        CollectionAssert.AreEqual(SongClipVariantCatalog.VisibleVariants.Where(v => v.Available).Select(v => v.Id).ToArray(),
            options.GetProperty("variant").GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToArray());
        CollectionAssert.AreEqual(PiH3Components.StepOptions.ToArray(), options.GetProperty("piH3Steps").GetProperty("enum")
            .EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(v => v.GetInt32()).ToArray());
        CollectionAssert.AreEqual(SongClipDeno.Sizes.Select(x => Math.Min(x.Height, x.Width)).Distinct().ToArray(),
            options.GetProperty("deno").GetProperty("properties").GetProperty("resolution").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.IsTrue(song.GetProperty("modelOperations").EnumerateArray().Any(op => op.ValueEquals("deno-accelerated")));
    }

    [TestMethod]
    public void SongClipKeepsDefaultAndSavesPiH3AndDenoOptions()
    {
        using var folder = new ProjectFolder();
        var store = new SongClipStore(folder.Path);
        var old = Save(ScenarioNavigationCatalog.SongClip, """{"title":"Legacy","prompt":"Fade in"}""", folder.Path);
        Assert.AreEqual("glitchframe", store.Load(Text(old, "projectId")).Model);
        var pi = Save(ScenarioNavigationCatalog.SongClip,
            """{"title":"Pi","prompt":"Fade in","variant":"pi-h3-dwd","piH3Steps":2}""", folder.Path);
        var piProject = store.Load(Text(pi, "projectId"));
        Assert.AreEqual(PiH3Components.ModelId, piProject.Model);
        Assert.AreEqual(2, piProject.Settings.Geek!.PiH3Steps);
        var deno = Save(ScenarioNavigationCatalog.SongClip,
            """{"title":"Deno","prompt":"Fade in","variant":"deno","deno":{"resolution":2160,"orientation":"portrait","accelerate":true}}""", folder.Path);
        var denoProject = store.Load(Text(deno, "projectId"));
        Assert.AreEqual(SongClipDeno.Mode, denoProject.Settings.Motion.Mode);
        Assert.AreEqual(new SongClipDenoSettings(2160, 3840) { Accelerate = true }, denoProject.Settings.Deno);
        Assert.IsTrue(denoProject.ExternalAgentProject);
    }

    [TestMethod]
    public void ShortsSaveTheSameProfileAsTheDesktop()
    {
        using var folder = new ProjectFolder();
        var store = new ShortsProjects(folder.Path);
        foreach (var (settings, expected) in new[]
        {
            ("", new ShortsGenerationProfile(false,false,false)),
            (",\"variant\":\"deno\",\"accelerate\":true", new(false,true,true)),
            (",\"fromSong\":true", new(true,true,true))
        })
        {
            var result = Save(ScenarioNavigationCatalog.Shorts, "{\"title\":\"Draft\",\"prompt\":\"A desk\"" + settings + "}", folder.Path);
            var project = store.Load(Text(result, "projectId"));
            Assert.AreEqual(expected, ShortsGenerationProfile.FromDraft(project.Workspace));
            Assert.IsTrue(project.ExternalAgentProject);
        }
    }

    [TestMethod]
    public void WorkshopAndUpscaleSaveDenoInTheirNativeDocuments()
    {
        using var folder = new ProjectFolder();
        var video = Save(ScenarioNavigationCatalog.VideoGeneration,
            """{"title":"Draft","idea":"A desk","model":"minimax-h3-w4a8","deno":{"resolution":1080,"orientation":"portrait","accelerate":true}}""", folder.Path);
        var project = VideoPreparationArchive.Load(Text(video, "path"), Path.Combine(folder.Path, "opened"));
        Assert.AreEqual(VideoH3LiteCatalog.ModelId, VideoGeneratorPlan.Selected(project)!.Settings.ModelId);
        var settings = VideoH3WorkspaceState.Get(project, VideoH3LiteCatalog.ModelId)!.Settings;
        Assert.AreEqual("9:16", settings.Aspect);
        Assert.AreEqual(1080, settings.OutputHeight);
        Assert.AreEqual(1080, settings.DenoOutput);
        Assert.IsTrue(settings.DenoAccelerate);
        using var storage = UpscaleStore.UseTestRoot(folder.Path);
        var upscale = Save(ScenarioNavigationCatalog.VideoUpscale,
            """{"title":"Draft","width":2160,"height":3840,"preset":"deno"}""", folder.Path);
        var saved = UpscaleStore.Load<UpscaleProject>(Text(upscale, "path"));
        Assert.AreEqual(UpscaleMode.Deno, saved.Mode);
        Assert.AreEqual(UpscaleEngine.Deno, saved.Steps.Single().Engine);
        Assert.AreEqual((2160, 3840), (saved.Steps[0].Width, saved.Steps[0].Height));
    }

    [TestMethod]
    [DataRow("song_clip", "\"variant\":\"pi-h3-dwd\",\"piH3Steps\":3")]
    [DataRow("song_clip", "\"variant\":\"deno\",\"deno\":{\"resolution\":720}")]
    [DataRow("song_clip", "\"variant\":\"montage\",\"deno\":{}")]
    [DataRow("shorts", "\"variant\":\"standard\",\"accelerate\":true")]
    [DataRow("shorts", "\"fromSong\":true,\"accelerate\":false")]
    public void UnsupportedCombinationsAreRejectedBeforeSaving(string scenario, string options)
    {
        using var folder = new ProjectFolder();
        Assert.ThrowsExactly<InvalidDataException>(() => Save(scenario, "{\"title\":\"Draft\",\"prompt\":\"A desk\"," + options + "}", folder.Path));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(folder.Path).Any());
    }

    private static JsonElement Description(string scenario) => JsonSerializer.SerializeToElement(AgentProjects.Describe(scenario), AgentProtocol.Json);
    private static JsonElement Save(string scenario, string json, string path) =>
        JsonSerializer.SerializeToElement(AgentProjects.Create(scenario, Parse(json), path), AgentProtocol.Json);
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static string Text(JsonElement json, string name) => json.GetProperty(name).GetString()!;

    private sealed class ProjectFolder : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("lopata-agent-options-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
