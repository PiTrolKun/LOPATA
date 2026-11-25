using System.Text.Json;
using AIHub.Services;
using AIHub.Services.ExternalAgents;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class AgentModelPlanTests
{
    [TestMethod]
    public void EveryScenarioWithANativeModelDownloadPublishesOperations()
    {
        var projects = JsonSerializer.SerializeToElement(AgentProjects.List()).EnumerateArray()
            .Select(p => p.GetProperty("scenario").GetString()!)
            .Where(id => id is not (ScenarioNavigationCatalog.CharacterKnowledge
                or ScenarioNavigationCatalog.NeuroConstructor or ScenarioNavigationCatalog.VideoConverter))
            .Order().ToArray();
        CollectionAssert.AreEqual(projects, AgentModelPlan.Operations.Keys.Order().ToArray());
        foreach (var (id, operations) in AgentModelPlan.Operations)
        {
            Assert.IsTrue(operations.Length > 0, id);
            Assert.AreEqual(operations.Length, operations.Distinct(StringComparer.Ordinal).Count(), id);
            foreach (var operation in operations) AgentModelPlan.Validate(id, operation);
        }
    }

    [TestMethod]
    public void MusicAndSpeechOnlyPublishModelsAvailableInTheNativePicker()
    {
        var music = MusicModelSelectionCatalog.All.SelectMany(model => model.Variants)
            .Where(v => v.Connected).Select(v => MusicModelVariants.Normalize(v.Id)).Distinct().Order().ToArray();
        CollectionAssert.AreEqual(music, AgentModelPlan.Operations[ScenarioNavigationCatalog.Music].Order().ToArray());
        var voices = VideoNarratorCatalog.Models.Where(v => v.Blocked is null).Select(v => "speech/" + v.Id).Order().ToArray();
        foreach (var scenario in new[] { ScenarioNavigationCatalog.SpeechAssets, ScenarioNavigationCatalog.OfficeEditor, ScenarioNavigationCatalog.VideoEditor })
            CollectionAssert.AreEqual(voices, AgentModelPlan.Operations[scenario].Where(op => op.StartsWith("speech/")).Order().ToArray());
    }
}
