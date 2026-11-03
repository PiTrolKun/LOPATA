using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ChatAudioTests
{
    [TestMethod]
    public async Task AudioChunksSurvivePauseAndOriginalRemainsSearchableAndUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-chat-audio-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "history.db")); var id = Guid.NewGuid().ToString("N"); store.Create(id);
            var source = Path.Combine(root, "Разговор.wav"); var bytes = new byte[] { 1, 2, 3, 4 }; await File.WriteAllBytesAsync(source, bytes);
            var file = await store.ImportAttachmentAsync(id, source, default);
            store.SaveContent(id, [new("user", "", Attachments: [file.Id])], "");
            Assert.AreEqual("audio", file.Kind);
            Assert.ThrowsExactly<InvalidDataException>(() => store.ReadAttachmentPage(id, file.Id, 0, 1000, default));
            var starts = new List<double>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => LetItOutAudioTranscription.TranscribeAsync(store, id, file, 25.5,
                (start, length, _) => { starts.Add(start); return start == 0 ? Task.FromResult("Обсудили работу и пятницу.") : Task.FromException<string>(new OperationCanceledException()); }, default));
            var interrupted = store.AudioTranscript(id, file.Id)!; Assert.AreEqual(11, interrupted.Next); Assert.IsFalse(interrupted.Complete); Assert.HasCount(1, interrupted.Parts);
            var reopened = new LetItOutConversationStore(store.DatabasePath);
            await LetItOutAudioTranscription.TranscribeAsync(reopened, id, file, 25.5, (start, length, _) =>
            { starts.Add(start); return Task.FromResult(start == 11 ? "пятницу. Встреча в семь вечера." : "семь вечера. Код ZETA-4827."); }, default);
            CollectionAssert.AreEqual(new[] { 0d, 11d, 11d, 22d }, starts.ToArray());
            Assert.IsTrue(reopened.AudioTranscript(id, file.Id)!.Complete);
            using var page = JsonDocument.Parse(reopened.ReadAttachmentPage(id, file.Id, 0, 1000, default));
            var text = page.RootElement.GetProperty("content").GetString()!;
            Assert.AreEqual("Обсудили работу и пятницу. Встреча в семь вечера. Код ZETA-4827.", text);
            Assert.AreEqual("automatic_speech_transcript", page.RootElement.GetProperty("representation").GetString());
            using var found = JsonDocument.Parse(reopened.SearchAttachment(id, file.Id, "ZETA", default));
            StringAssert.Contains(found.RootElement.GetProperty("content").GetString()!, "ZETA-4827");
            store.SaveContent(id, [new("user", "", Attachments: [file.Id]), new("assistant", "Обсудим встречу.")], "");
            StringAssert.Contains(store.ReadMemoryIndex(id), "Разговор.wav");
            Assert.AreEqual(file.Id, store.MemoryPairs(id).Single().Attachments!.Single().Id);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(store.AttachmentPath(id, file)));
            var other = Guid.NewGuid().ToString("N"); store.Create(other);
            Assert.ThrowsExactly<InvalidDataException>(() => store.ReadAttachmentPage(other, file.Id, 0, 1000, default));
            var count = starts.Count;
            await LetItOutAudioTranscription.TranscribeAsync(store, id, file, 25.5, (_, _, _) => throw new AssertFailedException("Already recognized audio must not be decoded again."), default);
            Assert.AreEqual(count, starts.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task EmptyTranscriptIsExplicitAndAudioWorkerIsNotExemptFromEmergencyStop()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-chat-audio-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "history.db")); var id = Guid.NewGuid().ToString("N"); store.Create(id);
            var source = Path.Combine(root, "тишина.mp3"); await File.WriteAllTextAsync(source, "fixture"); var file = await store.ImportAttachmentAsync(id, source, default);
            await LetItOutAudioTranscription.TranscribeAsync(store, id, file, 2, (_, _, _) => Task.FromResult(""), default);
            using var page = JsonDocument.Parse(store.ReadAttachmentPage(id, file.Id, 0, 1000, default));
            Assert.IsTrue(page.RootElement.GetProperty("no_speech_recognized").GetBoolean());
            StringAssert.Contains(page.RootElement.GetProperty("warning").GetString()!, "Нет анализа");
            Assert.IsFalse(OwnedProcessRegistry.IsEmergencyExempt("LetItOut.Audio"));
            Assert.IsTrue(OwnedProcessRegistry.IsEmergencyExempt("VoiceInput.Parakeet"));
            foreach (var extension in new[] { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".opus" }) Assert.IsTrue(LetItOutConversationStore.SupportsAttachment("file" + extension));
        }
        finally { Directory.Delete(root, true); }
    }
}
