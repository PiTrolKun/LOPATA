using System.Text.Json;
using AIHub.Services;
namespace AIHub.Tests;
[TestClass]
public sealed class OmniBatchEffortTests
{
    [TestMethod]
    [DataRow("heavy", "compose", false, true)]
    [DataRow("heavy", "compose", true, false)]
    [DataRow("heavy", "analyze", false, false)]
    [DataRow("heavy", "revise", false, false)]
    [DataRow("medium", "compose", false, false)]
    [DataRow("light", "compose", false, false)]
    public void Medium_IsScopedToGammaTextComposition(string bundle, string command, bool image, bool expected)
    {
        using var request = JsonDocument.Parse(OmniLlamaProtocol.BuildRequest(
            [new() { Role = "user", Content = "Describe", IncludesImage = image }],
            image ? "data:image/png;base64,YQ==" : "", 4096, OmniLlamaProfile.ForBundle(bundle), command));
        var options = request.RootElement.GetProperty("chat_template_kwargs");
        Assert.IsTrue(options.GetProperty("enable_thinking").GetBoolean());
        Assert.AreEqual(expected, options.TryGetProperty("reasoning_effort", out var effort));
        if (expected) Assert.AreEqual("medium", effort.GetString());
    }
}
