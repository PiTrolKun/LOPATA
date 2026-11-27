using System.IO;
using AIHub.Services;
using AIHub.Services.VisualConstructor;

namespace AIHub.Tests;

[TestClass]
public sealed class ConstructorGemmaHistoryTests
{
    [TestMethod]
    public void SavedRequestsAndRepliesAreKeptApartByProject()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var state = Path.Combine(stand.FullPath, "settings");
        var first = Path.Combine(stand.FullPath, "first", "Example.csproj");
        var second = Path.Combine(stand.FullPath, "second", "Example.csproj");
        var history = new ConstructorGemmaHistory(state, first);
        var start = DateTimeOffset.UtcNow;
        var turn = new ConstructorGemmaTurn(Guid.NewGuid(), start, "Кнопка · При нажатии", false, "Звук ошибки");
        history.Save(turn);
        Assert.AreEqual(turn, new ConstructorGemmaHistory(state, first).Read().Single());

        var response = turn with { Reply = "Воспроизвести звук ошибки Windows" };
        history.Save(response);
        var followUp = new ConstructorGemmaTurn(Guid.NewGuid(), start.AddSeconds(1), turn.Target, false, "При нажатии", "Готово");
        history.Save(followUp);
        var other = new ConstructorGemmaHistory(state, second);
        other.Save(turn with { Prompt = "Запрос другого проекта" });

        CollectionAssert.AreEqual(new[] { followUp, response }, new ConstructorGemmaHistory(state, first.ToUpperInvariant()).Read().ToArray());
        Assert.AreEqual("Запрос другого проекта", other.Read().Single().Prompt);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(first)));
    }

    [TestMethod]
    public void SystemErrorSoundRoundTripsWithoutChangingPreviousStepKinds()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Sound fixture");
        var source = project.ReadWindow();
        var layout = new ConstructorLayout(source);
        var button = layout.Add(ConstructorElementKind.Button, "Проверка", null);
        project.SaveWindow(source, layout.Document);
        var site = new ConstructorEventSite(ConstructorProject.MainForm, ConstructorLayout.Name(button), "Click");
        const string raw = """{"steps":[{"kind":"PlayErrorSound","arguments":[]}],"body":"","question":"","problem":""}""";
        var reply = ConstructorGemmaWork.Parse(raw, false, new OutputModelUse("fixture", "Fixture", "test", "constructor"));
        var chain = project.ReadChain(site);
        var oldMessage = project.CreateStep(site, new(ConstructorStepKind.Message, ["Error"]));
        var oldCode = project.CreateStep(site, new(ConstructorStepKind.Code, []));
        var sound = project.CreateStep(site, reply.Steps.Single());
        project.CommitChain(chain, [oldMessage, oldCode, sound]);
        var loaded = ConstructorProject.Open(project.FilePath).ReadChain(site).Steps;
        Assert.AreEqual(8, (int)loaded[0].Spec!.Kind);
        Assert.AreEqual(11, (int)loaded[1].Spec!.Kind);
        Assert.AreEqual(ConstructorStepKind.PlayErrorSound, loaded[2].Spec!.Kind);
        Assert.IsEmpty(loaded[2].Spec!.Arguments);
        StringAssert.Contains(loaded[2].Source, "System.Media.SystemSounds.Hand.Play();");
        Assert.DoesNotContain("ctx.Show(", loaded[2].Source);
    }
}
