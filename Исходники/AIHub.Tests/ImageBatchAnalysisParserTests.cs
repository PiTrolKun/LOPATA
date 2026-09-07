using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageBatchAnalysisParserTests
{
    [TestMethod]
    public void StringContract_RemainsUnchanged()
    {
        var value = ImageBatchAnalysisParser.Parse("```json\n{\"details\":\"A cat.\",\"summary\":\"Portrait\"}\n```");
        Assert.AreEqual("A cat.", value.Details); Assert.AreEqual("Portrait", value.Summary);
    }
    [TestMethod]
    public void ListContract_PreservesEveryObservationAndOrder()
    {
        var value = ImageBatchAnalysisParser.Parse("{\"details\":[\"A black cat.\",\"Green smoke.\",\"Three candles.\"],\"summary\":\"A cat by a cauldron.\"}");
        using var details = JsonDocument.Parse(value.Details);
        CollectionAssert.AreEqual(new[] { "A black cat.", "Green smoke.", "Three candles." }, details.RootElement.EnumerateArray().Select(v => v.GetString()).ToArray());
    }
    [TestMethod]
    public void NamedSections_PreserveLabelsNestedValuesAndUncertainty()
    {
        var value = ImageBatchAnalysisParser.Parse("{\"details\":{\"sky\":\"Possibly dawn\",\"objects\":{\"trees\":[\"Dark forest\"],\"count\":4}},\"summary\":[\"Landscape\"]}");
        using var details = JsonDocument.Parse(value.Details);
        Assert.AreEqual("Possibly dawn", details.RootElement.GetProperty("sky").GetString());
        Assert.AreEqual(4, details.RootElement.GetProperty("objects").GetProperty("count").GetInt32());
        Assert.IsTrue(value.Summary.Contains("Landscape"));
    }
    [TestMethod]
    public void EmptyMissingDuplicateAndBrokenContent_StillFails()
    {
        foreach (var input in new[] { "{}", "{\"details\":[],\"summary\":\"x\"}", "{\"details\":{\"count\":4},\"summary\":\"x\"}", "{\"details\":\"x\",\"summary\":null}", "{\"details\":\"x\",\"Details\":\"y\",\"summary\":\"z\"}", "{\"details\":\"x\",\"summary\":\"unterminated}" })
        {
            bool failed = false;
            try { ImageBatchAnalysisParser.Parse(input); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { failed = true; }
            Assert.IsTrue(failed, input);
        }
    }
}
