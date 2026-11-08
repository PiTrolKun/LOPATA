using System.IO;
using AIHub.Services;
using AIHub.Services.Dictation;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeAiJournalTests
{
    [TestMethod]
    public async Task DictationUsesOneEntryForChangingHypothesesAndTheFinalAudioFingerprint()
    {
        var events = new List<OfficeAiEntry>();
        var journal = new OfficeDictationJournal(new OfficeAiJournal("chapter-a", "Dictation", (entry, file) =>
        {
            Assert.IsNull(file);
            events.Add(entry);
            return Task.CompletedTask;
        }), "Распознать");
        await journal.RecognizeAsync(new DictationChunk(0, false, new byte[16000], 0), () => Task.FromResult("Промежуточная"));
        Assert.AreEqual(1, events.Count);
        var audio = new byte[32000];
        await journal.RecognizeAsync(new DictationChunk(0, true, audio, 1), () => Task.FromResult("Итоговая фраза"));
        Assert.AreEqual(2, events.Count); Assert.AreEqual(events[0].Id, events[1].Id);
        var last = events[1];
        Assert.AreEqual("Итоговая фраза", last.Response); Assert.AreEqual("completed", last.Status);
        Assert.IsNull(last.SystemPrompt); Assert.AreEqual("chapter-a", last.SheetId);
        Assert.AreEqual(DictationComponents.Revision, last.Models.Single().Revision);
        StringAssert.Contains(last.Parameters!, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(audio)));
    }

    [TestMethod]
    public async Task TranslationLogsExactPayloadAnswerAndModelAtSendBoundary()
    {
        ReaderTranslationPiece[] pieces = [new("s1", "Фраза 42")];
        var input = ReaderTranslationProtocol.Input("ru", "en", pieces);
        var entries = new List<OfficeAiEntry>();
        var journal = new OfficeAiJournal("chapter", "Translate", (entry, _) => { entries.Add(entry); return Task.CompletedTask; });
        var model = new OutputModelUse("id", "runtime model", "pinned-revision", "translation");
        var answer = """{"translations":[{"id":"s1","text":"Phrase 42"}]}""";
        var result = await ReaderTranslationRuntime.GenerateBatchAsync(journal, input, ReaderTranslationProtocol.Instructions,
            pieces, model, async send => { await send(); return answer; }, () => false);
        Assert.AreEqual("Phrase 42", result.Text["s1"]);
        Assert.AreEqual(2, entries.Count);
        var first = entries[0]; var last = entries[1];
        Assert.AreEqual(first.Id, last.Id);
        Assert.AreEqual("running", first.Status);
        Assert.AreEqual("completed", last.Status);
        Assert.AreEqual(input, last.Request); Assert.AreEqual(answer, last.Response);
        Assert.AreEqual(ReaderTranslationProtocol.Instructions, last.SystemPrompt);
        Assert.AreEqual("chapter", last.SheetId); Assert.AreEqual(model, last.Models.Single());

        entries.Clear();
        await Assert.ThrowsAsync<IOException>(() => ReaderTranslationRuntime.GenerateBatchAsync(journal, input, "system",
            pieces, model, _ => throw new IOException("private preparation path"), () => false));
        Assert.IsEmpty(entries, "Preparing a model must not create an AI sheet.");
        await Assert.ThrowsAsync<InvalidDataException>(() => ReaderTranslationRuntime.GenerateBatchAsync(journal, input, "system",
            pieces, model, async send => { await send(); return "invalid output"; }, () => false));
        Assert.AreEqual("failed", entries.Last().Status); Assert.AreEqual("invalid output", entries.Last().Response);
        Assert.AreEqual("Reader.Translation.InvalidReply", entries.Last().Failure);
    }
}
