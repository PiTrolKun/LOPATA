using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services;
using AIHub.Services.ExternalAgents;
using AIHub.Services.ImageLayers;
using AIHub.Services.OfficeEditor;
using AIHub.Services.VectorEditor;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class ExternalAgentEditorOriginTests
{
    private const string Phrase = "Проверка сохранённого черновика";
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><path d="M2 2 L20 20" stroke="red"/></svg>""";

    [TestMethod]
    [DataRow(ScenarioNavigationCatalog.OfficeEditor, "manifest.json")]
    [DataRow(ScenarioNavigationCatalog.VectorEditor, "project.json")]
    [DataRow(ScenarioNavigationCatalog.ImageLayers, "manifest.json")]
    [DataRow(ScenarioNavigationCatalog.VideoEditor, "project.json")]
    [DataRow(ScenarioNavigationCatalog.VideoUpscale, null)]
    public void AgentOriginAndLegacyOrManualOriginsSurviveEditing(string scenario, string? manifestName)
    {
        using var files = new VideoPreparationTests.TestFiles();
        using var upscale = UpscaleStore.UseTestRoot(files.File("upscale"));
        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(SKColors.SkyBlue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        object data = scenario switch
        {
            ScenarioNavigationCatalog.OfficeEditor => new { title = Phrase, text = Phrase },
            ScenarioNavigationCatalog.VectorEditor => new { title = Phrase, svg = Svg },
            ScenarioNavigationCatalog.ImageLayers => new { title = Phrase, imageBase64 = Convert.ToBase64String(png) },
            ScenarioNavigationCatalog.VideoEditor => new { title = Phrase, narration = new[] { new { text = Phrase, start = 0, end = 2 } } },
            ScenarioNavigationCatalog.VideoUpscale => new { title = Phrase, width = 320, height = 240 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var result = JsonSerializer.SerializeToElement(AgentProjects.Create(scenario, JsonSerializer.SerializeToElement(data), files.Root));
        Assert.AreEqual("external_agent", result.GetProperty("origin").GetString());
        var original = result.GetProperty("path").GetString()!;
        Assert.StartsWith(files.Root + Path.DirectorySeparatorChar, Path.GetFullPath(original));

        foreach (bool? marker in new bool?[] { true, false, null })
        {
            var name = marker switch { true => "agent", false => "manual", null => "legacy" };
            var copy = files.File(name + Path.GetExtension(original));
            File.Copy(original, copy);
            if (marker != true) ChangeOrigin(copy, manifestName, marker);
            var saved = files.File(name + "-saved" + Path.GetExtension(original));
            Assert.AreEqual(marker == true, ReadAndSave(scenario, copy, saved, png), name);
            Assert.AreEqual(marker == true, ReadAndSave(scenario, saved, null, png), name);
        }
    }

    private static void ChangeOrigin(string path, string? manifestName, bool? marker)
    {
        void Update(JsonNode document)
        {
            var project = (document["Project"] ?? document).AsObject();
            var key = project.Select(kv => kv.Key).Single(key => key.Equals("externalAgentProject", StringComparison.OrdinalIgnoreCase));
            if (marker.HasValue) project[key] = marker.Value;
            else project.Remove(key);
        }
        if (manifestName is null)
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Update(json);
            File.WriteAllText(path, json.ToJsonString());
            return;
        }
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry(manifestName)!;
        JsonNode document;
        using (var stream = entry.Open()) document = JsonNode.Parse(stream)!;
        Update(document);
        entry.Delete();
        using var output = archive.CreateEntry(manifestName).Open();
        JsonSerializer.Serialize(output, document);
    }

    private static bool ReadAndSave(string scenario, string path, string? destination, byte[] png)
    {
        switch (scenario)
        {
            case ScenarioNavigationCatalog.OfficeEditor:
                var office = OfficeTextPackage.Inspect(path);
                byte[] payload;
                using (var zip = ZipFile.OpenRead(path))
                using (var entry = zip.GetEntry(OfficeSheets.Payload(office.Sheets[0]))!.Open())
                using (var buffer = new MemoryStream()) { entry.CopyTo(buffer); payload = buffer.ToArray(); }
                using (var docx = new ZipArchive(new MemoryStream(payload)))
                using (var reader = new StreamReader(docx.GetEntry("word/document.xml")!.Open()))
                    StringAssert.Contains(reader.ReadToEnd(), Phrase);
                if (destination != null) OfficeTextPackage.Store(destination, payload, office, path);
                return office.ExternalAgentProject;
            case ScenarioNavigationCatalog.VectorEditor:
                var vector = VectorProjectArchive.Load(path);
                Assert.AreEqual(Svg, vector.Sheets.Single().Svg);
                if (destination != null) VectorProjectArchive.Save(vector, destination);
                return vector.ExternalAgentProject;
            case ScenarioNavigationCatalog.ImageLayers:
                var layers = LayerProjectArchive.Open(path);
                CollectionAssert.AreEqual(png, layers.Source.Original);
                if (destination != null) LayerProjectArchive.Save(destination, layers);
                return layers.ExternalAgentProject;
            case ScenarioNavigationCatalog.VideoEditor:
#if UPDATE_STAND
                var video = VideoProjectArchive.Load(path);
#else
                // The project has no external media. Read the native archive manifest without
                // unpacking into a user cache when running the regular test build.
                VideoProject video;
                using (var zip = ZipFile.OpenRead(path)) video = VideoProjectArchive.ReadManifest(zip).Project;
#endif
                video.Validate();
                Assert.AreEqual(Phrase, video.Narration.Single().Text);
                if (destination != null) VideoProjectArchive.Save(video, destination);
                return video.ExternalAgentProject;
            case ScenarioNavigationCatalog.VideoUpscale:
                var pipeline = UpscaleStore.Load<UpscaleProject>(path);
                Assert.AreEqual(Phrase, pipeline.Name);
                Assert.AreEqual(UpscaleMode.Manual, pipeline.Mode);
                Assert.AreEqual("lanczos", pipeline.Steps.Single().Algorithm);
                if (destination != null) UpscaleStore.Save(destination, pipeline);
                return pipeline.ExternalAgentProject;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }
}
