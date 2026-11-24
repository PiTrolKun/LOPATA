using System.Text.Json;
using AIHub.Models;
using AIHub.Models.OneC;
using AIHub.Services.AnimatedAssets;
using AIHub.Services.CharacterKnowledge;
using AIHub.Services.GameSounds;
using AIHub.Services.NeuroConstructor;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoPreparation;
using AIHub.Services.VisualScenario;

namespace AIHub.Tests;

[TestClass]
public sealed class ExternalAgentProjectOriginTests
{
    [TestMethod]
    public void NativeDocumentsKeepTheAgentMarkerAndTreatOlderFilesAsManual()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        object[] manualProjects = [
            new VideoPreparationProject(), new LiteraryProject(), new MusicProject(id.ToString("N"), 1, now),
            new ShortsProject(id.ToString("N"), now, new()), new SongClipProject(), new VisualProject(),
            new GameSoundProject(), new SpeechAssetSession(), new OneCProject(),
            new KnowledgeProject(new(id, "test", now), new([], []), id), new NeuroProject(), new AnimationProject()
        ];

        foreach (var manual in manualProjects)
        {
            var type = manual.GetType();
            var marker = type.GetProperty("ExternalAgentProject")!;
            Assert.AreEqual(false, marker.GetValue(manual), type.Name);
            var legacy = JsonSerializer.SerializeToNode(manual, type)!.AsObject();
            legacy.Remove(marker.Name);
            Assert.AreEqual(false, marker.GetValue(legacy.Deserialize(type)), type.Name);
            legacy[marker.Name] = true;
            var agent = legacy.Deserialize(type);
            var reopened = JsonSerializer.Deserialize(JsonSerializer.Serialize(agent, type), type);
            Assert.AreEqual(true, marker.GetValue(reopened), type.Name);
        }
    }

    [TestMethod]
    public void KnowledgeOriginSurvivesEditingExportAndImportIntoAnotherLibrary()
    {
        var folder = Directory.CreateTempSubdirectory("lopata-knowledge-origin-").FullName;
        try
        {
            var library = new KnowledgeStore(Path.Combine(folder, "library"));
            var other = new KnowledgeStore(Path.Combine(folder, "other"));
            foreach (var fromAgent in new[] { true, false })
            {
                var original = library.Create("Draft", externalAgentProject: fromAgent);
                var edit = library.Save(original with { Base = original.Base with { Name = "Reviewed" } });
                var path = Path.Combine(folder, edit.Base.BaseId + ".zip");
                KnowledgeArchive.Export(path, library.Load(edit.Base.BaseId));
                var imported = other.Import(path);
                Assert.AreEqual(fromAgent, other.Load(imported.Base.BaseId).ExternalAgentProject);
                Assert.AreEqual("Reviewed", imported.Base.Name);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
