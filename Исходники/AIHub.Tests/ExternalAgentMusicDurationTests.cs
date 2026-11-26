using System.Text.Json;
using AIHub.Services;
using AIHub.Services.ExternalAgents;

namespace AIHub.Tests;

[TestClass]
public sealed class ExternalAgentMusicDurationTests
{
    [TestMethod]
    public void MusicAgentAdvertisesAutomaticAndModelSpecificManualLimits()
    {
        var description = JsonSerializer.SerializeToElement(AgentProjects.Describe(ScenarioNavigationCatalog.Music), AgentProtocol.Json);
        var schema = description.GetProperty("dataSchema");
        Assert.IsFalse(schema.GetProperty("required").EnumerateArray().Any(field => field.ValueEquals("durationSeconds")));
        var variants = schema.GetProperty("allOf").EnumerateArray().ToDictionary(
            item => item.GetProperty("if").GetProperty("properties").GetProperty("variation").GetProperty("enum")[0].GetString()!,
            item => item.GetProperty("then").GetProperty("properties").GetProperty("durationSeconds").GetProperty("maximum").GetInt32());
        Assert.AreEqual(MusicDurationPolicy.MaximumManualSeconds(MusicStudioRuntime.Variation, AgentProjects.MusicStudioRevisionForAgent()),
            variants[MusicStudioRuntime.Variation]);
        Assert.AreEqual(600, variants[MusicAceCatalog.Variation]);
        Assert.AreEqual(600, variants[MusicDiffRhythmCatalog.Variation]);
        Assert.AreEqual(655, variants[MusicHeartMuLaCatalog.Variation]);
    }
}
