using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class OpenAiSseStreamParserTests
{
    [TestMethod]
    public async Task ReadAsync_AssemblesContentAndToolArguments()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Привет "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"мир"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"web_","arguments":"{\"q\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"search","arguments":"\"test\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var chunks = new List<ModelStreamChunk>();

        var result = await OpenAiSseStreamParser.ReadAsync(
            stream,
            new ImmediateProgress<ModelStreamChunk>(chunks.Add),
            CancellationToken.None);

        Assert.AreEqual("Привет мир", result.Content);
        Assert.AreEqual("tool_calls", result.FinishReason);
        Assert.AreEqual("web_search", result.ToolCalls.Single().Function.Name);
        Assert.AreEqual("{\"q\":\"test\"}", result.ToolCalls.Single().Function.Arguments);
        Assert.IsTrue(chunks.Any(chunk => chunk.Text.Contains("Привет", StringComparison.Ordinal)));
        Assert.IsTrue(chunks.Last().IsComplete);
    }

    [TestMethod]
    [DataRow("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n")]
    [DataRow("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\ndata: {\"error\":{\"message\":\"VK_ERROR_OUT_OF_DEVICE_MEMORY\"}}\n")]
    public async Task InterruptedOrFailedStreamNeverReportsCompletion(string sse)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var chunks = new List<ModelStreamChunk>();
        await Assert.ThrowsAsync<System.IO.IOException>(() => OpenAiSseStreamParser.ReadAsync(stream,
            new ImmediateProgress<ModelStreamChunk>(chunks.Add), CancellationToken.None));
        Assert.AreEqual("partial", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.IsFalse(chunks.Any(chunk => chunk.IsComplete));
    }

    [TestMethod]
    public async Task ExplicitFinishReasonAtEofIsACompletedStream()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n"));
        var result = await OpenAiSseStreamParser.ReadAsync(stream, null, CancellationToken.None);
        Assert.AreEqual("answer", result.Content);
        Assert.AreEqual("stop", result.FinishReason);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
