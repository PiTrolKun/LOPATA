using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoImportProvenanceTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public void EmbeddedHistorySurvivesDirectAndLegacyQuotedTags(bool quoted)
    {
        var facts = new OutputProvenance { Models = [new("actual-model", "Actual model", "pinned-revision", "generation")] };
        var text = facts.Json(); if (quoted) text = JsonSerializer.Serialize(text);
        using var probe = Probe(text); CollectionAssert.AreEqual(facts.Models, VideoImportProvenance.Models(probe.RootElement));
    }
    [TestMethod, DataRow("invalid"), DataRow("null"), DataRow("foreign"), DataRow("path"), DataRow("oversized")]
    public void UnusableMetadataDoesNotInventModelHistoryOrBreakMediaImport(string variant)
    {
        var facts = new OutputProvenance { Program = variant == "foreign" ? "OTHER" : "LOPATA",
            Models = [new(variant == "path" ? "C:\\private\\model" : "model", "Model", "revision", "generation")] };
        var text = variant switch { "invalid" => "{broken", "null" => "null", "oversized" => new string('x', 48001), _ => JsonSerializer.Serialize(facts) };
        using var probe = Probe(text); Assert.AreEqual(0, VideoImportProvenance.Models(probe.RootElement).Length);
    }
    private static JsonDocument Probe(string text) => JsonDocument.Parse(JsonSerializer.Serialize(new
        { format = new { tags = new Dictionary<string, string> { ["lopata_video"] = text } } }));
}
