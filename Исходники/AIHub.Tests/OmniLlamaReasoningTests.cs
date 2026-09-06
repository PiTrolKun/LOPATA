using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class OmniLlamaReasoningTests
{
    private const string Done = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
    private static string Delta(string field, string text) => "data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { delta = new Dictionary<string, string> { [field] = text } } }
    }) + "\n\n";

    [TestMethod]
    public async Task SeparateReasoning_IsKeptOnlyInRawEvidence()
    {
        const string content = "{\"title\":\"Наблюдение\",\"paragraphs\":[\"Две птицы.\"]}";
        var wire = Delta("reasoning_content", "Private reasoning") + Delta("content", content) + Done;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var progress = new Collector();
        string? raw = null;
        var result = await OmniLlamaProtocol.ReadAsync(stream, progress, s => raw = s, default);
        Assert.AreEqual(content, result.Content);
        Assert.AreEqual(content, string.Concat(progress.Chunks.Select(c => c.Text)));
        StringAssert.Contains(raw!, "Private reasoning");
        Assert.AreEqual(raw, result.RawProtocol);
        var conversation = new ImageAnalysisHiddenMessage[]
        {
            new() { Role = "user", Content = "Observe", IncludesImage = true },
            new() { Role = "assistant", Content = result.Content },
            new() { Role = "user", Content = "Compose" }
        };
        using var request = JsonDocument.Parse(OmniLlamaProtocol.BuildRequest(conversation, "data:image/png;base64,YQ=="));
        Assert.AreEqual(content, request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task InlineReasoning_WithTagsSplitIntoSingleCharacters_DoesNotLeak()
    {
        const string answer = "{\"title\":\"Result\",\"text\":\"A literal <think> tag.\"}";
        var wire = string.Concat((" \n<think>Private reasoning</think>" + answer).Select(c => Delta("content", c.ToString()))) + Done;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var progress = new Collector();
        var result = await OmniLlamaProtocol.ReadAsync(stream, progress, null, default);
        Assert.AreEqual(answer, result.Content);
        Assert.AreEqual(answer, string.Concat(progress.Chunks.Select(c => c.Text)));
        Assert.IsTrue(progress.Chunks[^1].IsComplete);
    }

    [TestMethod]
    [DataRow("content", "<think>Unfinished", false)]
    [DataRow("content", "<thi", false)]
    [DataRow("content", "<think>Only reasoning</think>", true)]
    [DataRow("reasoning_content", "Only reasoning", true)]
    public async Task ReasoningWithoutFinalAnswer_IsNotAccepted(string field, string text, bool recoverable)
    {
        var wire = Delta(field, text) + Done;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var progress = new Collector();
        string? raw = null;
        // Completed reasoning without an answer can be retried; a broken tag stream remains a protocol error.
        if (recoverable)
            await Assert.ThrowsAsync<ImageAnalysisOmniFormatException>(() => OmniLlamaProtocol.ReadAsync(stream, progress, s => raw = s, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => OmniLlamaProtocol.ReadAsync(stream, progress, s => raw = s, default));
        Assert.HasCount(0, progress.Chunks);
        Assert.IsFalse(string.IsNullOrWhiteSpace(raw));
    }

    [TestMethod]
    public async Task PlainAnswer_PreservesRepeatedParagraphsForHonestDiagnostics()
    {
        const string answer = "{\"paragraphs\":[\"Repeat\",\"Repeat\"]}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Delta("content", answer) + Done));
        var result = await OmniLlamaProtocol.ReadAsync(stream, null, null, default);
        Assert.AreEqual(answer, result.Content);
    }

    private sealed class Collector : IProgress<ModelStreamChunk>
    {
        public List<ModelStreamChunk> Chunks { get; } = [];
        public void Report(ModelStreamChunk value) => Chunks.Add(value);
    }
}
