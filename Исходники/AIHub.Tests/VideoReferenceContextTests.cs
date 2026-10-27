using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoReferenceContextTests
{
    [TestMethod]
    public void EightRussianCardsAreReadableWithoutChangingStoredContextOrQuotedNames()
    {
        var p = VideoReferencePlanTests.Root(); var w = p.World!;
        var sample = "Рыжий кот в круглых очках. Вывеска «Лопата / LOPATA», 中文, 🐈. Буквальная запись \\u041A.";
        for (var n = 1; n < 8; n++) VideoWorld.AddCard(p, n % 2 == 0 ? "place" : "character");
        foreach (var card in w.Cards) { card.Name = "Карточка «Лопата»"; card.Text.IdeaText = string.Concat(Enumerable.Repeat(sample, 4)); card.Fixed = "Не менять имя LOPATA."; }
        VideoWorld.Capture(p);
        var r = VideoReferencePlanTests.Request(p); var snapshot = r.ContextText;
        Assert.IsTrue(snapshot.Contains("\\u", StringComparison.Ordinal));
        var input = VideoReferencePromptProtocol.Input(r);
        StringAssert.Contains(input, "Рыжий кот"); StringAssert.Contains(input, "中文");
        using var parsed = JsonDocument.Parse(input); var cards = parsed.RootElement.GetProperty("context").GetProperty("cards");
        Assert.AreEqual(8, cards.GetArrayLength());
        for (var n = 0; n < 8; n++) Assert.AreEqual(w.Cards[n].Text.IdeaText, cards[n].GetProperty("description").GetString());
        Assert.AreEqual(snapshot, r.ContextText); Assert.AreEqual(snapshot, VideoReferencePlans.Context(p));
        Assert.IsTrue(input.Length < snapshot.Length / 2);
    }
    [TestMethod]
    public void PromptDefinitionIsAnObjectAndUserTextIsDecodedExactlyOnce()
    {
        var p = VideoReferencePlanTests.Root(); var item = VideoReferencePlans.Add(p);
        item.Name = "Кот «Лопата»"; item.Brief = "Надпись: \"LOPATA\". Сохранить \\u041A как текст.";
        item.Native = "Крупный план 🐈. 中文."; item.English = "A cat labelled «Лопата».";
        var r = VideoReferencePlanTests.Request(p, item); var stored = r.Text;
        using var input = JsonDocument.Parse(VideoReferencePromptProtocol.Input(r)); var draft = input.RootElement.GetProperty("draft");
        Assert.AreEqual(JsonValueKind.Object, draft.GetProperty("definition").ValueKind);
        Assert.AreEqual(item.Name, draft.GetProperty("definition").GetProperty("Name").GetString());
        Assert.AreEqual(item.Brief, draft.GetProperty("definition").GetProperty("Brief").GetString());
        Assert.AreEqual(item.Native, draft.GetProperty("native").GetString());
        Assert.AreEqual(item.English, draft.GetProperty("english").GetString()); Assert.AreEqual(stored, r.Text);
    }
    [TestMethod]
    public void WorldRequestsAlsoPreserveUnicodeDraftsWithoutEscapeInflation()
    {
        var p = VideoReferencePlanTests.Root(); var r = VideoReferencePlanTests.Request(p) with
        { Text = "Рыжий кот «Лопата» в мастерской.", ContextOriginal = "中文 и LOPATA", Purpose = VideoWorld.CardPurpose };
        var input = VideoWorldProtocol.Input(r); StringAssert.Contains(input, "Рыжий кот");
        using var parsed = JsonDocument.Parse(input); Assert.AreEqual(r.Text, parsed.RootElement.GetProperty("draft").GetString());
        Assert.AreEqual(r.ContextOriginal, parsed.RootElement.GetProperty("original_concept").GetString());
    }
    [TestMethod, DataRow(8192, 2000, 4096), DataRow(8192, 6912, 1024), DataRow(4096, 2000, 1840)]
    public void OutputReserveUsesActualCapacityAndRetainsHeadroom(int capacity, int used, int reserve)
        => Assert.AreEqual(reserve, VideoConceptAssistant.OutputTokens(capacity, used));
    [TestMethod]
    public async Task RealOverflowIsTerminalAndReleasesTheGateWithoutDiscardingPreparedAnswers()
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = VideoReferencePlanTests.Root(); var item = VideoReferencePlans.Add(p); item.Name = "Cat";
            var next = VideoReferencePlans.Add(p); next.Name = "Workshop";
            var first = VideoReferencePlanTests.Request(p, item); var r = VideoReferencePlanTests.Request(p, next);
            var batch = new VideoReferenceBatch(Guid.NewGuid().ToString("N"), p.Id, r.ProjectPath, [first, r]);
            var assistant = new VideoConceptTests.FakeAssistant { Run = (request, _) => {
                if (request.MaterialId == next.Id) VideoConceptAssistant.OutputTokens(8192, 6913);
                return Task.FromResult(VideoReferencePlanTests.PromptAnswer()); } };
            var receipts = new VideoConceptReceiptStore(files.File("receipts"));
            Task Commit(VideoConceptRequest request, VideoConceptAnswer answer) { VideoReferencePlans.Apply(p, request, answer); return Task.CompletedTask; }
            var error = await Assert.ThrowsExactlyAsync<BackgroundOperationFailedException>(() => VideoReferenceWork.RunAsync(batch, "context", assistant, receipts, Commit, default));
            Assert.AreEqual("VideoPrep.AiContextLimit", error.Message); Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase);
            Assert.IsFalse(controller.BlocksNewWork); Assert.IsFalse(controller.State.RequiresDecision); Assert.IsFalse(ComponentAuditFailures.IsEligible(error));
            Assert.AreEqual("VideoPrep.AiContextLimit", controller.State.Detail); Assert.IsNull(receipts.Read(r));
            Assert.IsNotNull(receipts.Read(first)); Assert.HasCount(1, item.History);
            assistant.Run = (_, _) => Task.FromResult(VideoReferencePlanTests.PromptAnswer());
            await VideoReferenceWork.RunAsync(batch with { Id = Guid.NewGuid().ToString("N") }, "retry", assistant, receipts,
                Commit, default);
            Assert.IsTrue(VideoReferencePlans.Ready(p)); Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State.Phase);
            Assert.AreEqual(3, assistant.Calls); Assert.HasCount(1, item.History);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
}
