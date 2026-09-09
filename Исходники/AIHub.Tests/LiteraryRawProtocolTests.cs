using System.IO;
using System.Text;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryRawProtocolTests
{
    [TestMethod]
    public async Task PreservesThinkingAndPartialAnswerOnServerLengthStop()
    {
        const string input = "data: {\"choices\":[{\"delta\":{\"content\":\"<think>draft</think> answer\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var result = await LiteraryRawProtocol.ReadAsync(stream, null, _ => { }, CancellationToken.None);
        Assert.AreEqual("<think>draft</think> answer", result);
    }

    [TestMethod]
    public async Task DoesNotRejectEmptyCompletedServerResponse()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n"));
        Assert.AreEqual("", await LiteraryRawProtocol.ReadAsync(stream, null, _ => { }, CancellationToken.None));
    }
}
