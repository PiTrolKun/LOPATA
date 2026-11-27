using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VisualConstructor;

namespace AIHub.Tests;

[TestClass]
public sealed class ConstructorGemmaWorkTests
{
    private static readonly OutputModelUse Model = new("fixture", "Fixture", "test", "constructor");

    [TestMethod]
    public async Task GenericErrorCannotReplaceRequestedNavigationEvenIfRepeated()
    {
        var placeholder = Reply(ConstructorStepKind.Message, "Error");
        var result = await ConstructorGemmaWork.ReviewStepsAsync(Request("Открой вторую форму в отдельном окне"), placeholder,
            _ => throw new AssertFailedException("The placeholder reached the model reviewer."),
            _ => Task.FromResult(placeholder));
        Assert.IsTrue(result.NeedsInput);
        Assert.IsFalse(result.Checked);
        Assert.IsEmpty(result.Steps);
    }

    [TestMethod]
    public async Task ExplicitErrorMessageIsAllowed()
    {
        var result = await ConstructorGemmaWork.ReviewStepsAsync(Request("Покажи сообщение «Error»"), Reply(ConstructorStepKind.Message, "Error"),
            _ => Task.FromResult("""{"verdict":"match","detail":""}"""),
            _ => throw new AssertFailedException("An explicitly requested popup was rejected."));
        Assert.IsFalse(result.NeedsInput);
        Assert.IsTrue(result.Checked);
        Assert.AreEqual("Error", result.Steps.Single().Arguments.Single());
    }

    [TestMethod]
    public async Task CorrectedWindowModeMustPassItsOwnReview()
    {
        var initial = Reply(ConstructorStepKind.Inside, "Form_2");
        var corrected = Reply(ConstructorStepKind.Separate, "Form_2");
        var reviewed = new List<ConstructorStepKind>();
        var result = await ConstructorGemmaWork.ReviewStepsAsync(Request("Открой вторую форму отдельным окном"), initial, candidate =>
        {
            var kind = candidate.Steps.Single().Kind;
            reviewed.Add(kind);
            return Task.FromResult(kind == ConstructorStepKind.Inside
                ? """{"verdict":"revise","detail":"Нужно открыть отдельное окно."}"""
                : """{"verdict":"match","detail":""}""");
        }, _ => Task.FromResult(corrected));
        CollectionAssert.AreEqual(new[] { ConstructorStepKind.Inside, ConstructorStepKind.Separate }, reviewed);
        Assert.IsTrue(result.Checked);
        Assert.AreEqual(ConstructorStepKind.Separate, result.Steps.Single().Kind);
    }

    [TestMethod]
    [DataRow("clarify", true)]
    [DataRow("unsupported", false)]
    public async Task ReviewCanReturnToUserWithoutActions(string verdict, bool hasQuestion)
    {
        const string detail = "Уточните, что нужно открыть.";
        var response = JsonSerializer.Serialize(new { verdict, detail });
        var result = await ConstructorGemmaWork.ReviewStepsAsync(Request("Открой"), Reply(ConstructorStepKind.Inside, "Form_2"),
            _ => Task.FromResult(response), _ => throw new AssertFailedException("Unnecessary retry."));
        Assert.IsTrue(result.NeedsInput);
        Assert.IsFalse(result.Checked);
        Assert.IsEmpty(result.Steps);
        Assert.AreEqual(detail, hasQuestion ? result.Question : result.Problem);
    }

    [TestMethod]
    public void ProblemAndActionsCannotAppearTogether()
    {
        const string raw = """{"steps":[{"kind":"Message","arguments":["Error"]}],"body":"","question":"","problem":"Нет подходящего действия."}""";
        Assert.ThrowsExactly<InvalidDataException>(() => ConstructorGemmaWork.Parse(raw, false, Model));
        var problem = ConstructorGemmaWork.Parse("""{"steps":[],"body":"","question":"","problem":"Нет подходящего действия."}""", false, Model);
        Assert.IsTrue(problem.NeedsInput);
        Assert.IsEmpty(problem.Steps);
    }

    [TestMethod]
    public void OldCheckpointIsReadAsUnchecked()
    {
        var legacy = new { Steps = new[] { new ConstructorStepSpec(ConstructorStepKind.Message, ["Error"]) },
            Body = "", Question = "", Model };
        var restored = JsonSerializer.Deserialize<ConstructorGemmaReply>(JsonSerializer.Serialize(legacy))!;
        Assert.IsFalse(restored.Checked);
        Assert.IsFalse(restored.NeedsInput);
        Assert.AreEqual(ConstructorStepKind.Message, restored.Steps.Single().Kind);
    }

    private static ConstructorGemmaRequest Request(string prompt) => new("fixture.csproj",
        new ConstructorEventSite(ConstructorProject.MainForm, "Button_1", "Click"), "test", prompt, null, null);

    private static ConstructorGemmaReply Reply(ConstructorStepKind kind, string argument) =>
        new([new(kind, [argument])], "", "", Model);
}
