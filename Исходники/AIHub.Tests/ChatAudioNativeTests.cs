using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ChatAudioNativeTests
{
    [TestMethod]
    public async Task InstalledRecognizerReadsWavMp3AndSilenceAndGemmaUsesTheTranscript()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_CHAT_AUDIO_NATIVE");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt in to installed Parakeet, FFmpeg and Gemma; no microphone capture."); return; }
        Directory.CreateDirectory(output); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8)); var token = timeout.Token;
        Assert.IsTrue(await LetItOutAudioTranscription.PrepareAsync(new(), false, null, token));
        var store = new LetItOutConversationStore(Path.Combine(output, "history.db")); var topic = Guid.NewGuid().ToString("N"); store.Create(topic);
        var ru = VideoNarratorSamples.AudioPath(VideoNarratorSamples.Find("supertonic3", "ru")!);
        var en = VideoNarratorSamples.AudioPath(VideoNarratorSamples.Find("supertonic3", "en")!);
        var audio = MusicAudioRuntime.Default;
        var mp3 = Path.Combine(output, "recording.mp3");
        await audio.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", en, "-c:a", "libmp3lame", mp3], token);
        var silence = Path.Combine(output, "silence.wav");
        using (var writer = new BinaryWriter(File.Create(silence)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + 64000); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
            writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(64000); writer.Write(new byte[64000]);
        }
        var files = new List<LetItOutAttachment>();
        foreach (var path in new[] { ru, mp3, silence }) files.Add(await store.ImportAttachmentAsync(topic, path, token));
        foreach (var (extension, codec) in new[] { ("m4a", "aac"), ("ogg", "libopus"), ("opus", "libopus"), ("webm", "libopus"), ("flac", "flac") })
        {
            var converted = Path.Combine(output, "recording." + extension);
            string[] args = ["-nostdin", "-v", "error", "-n", "-i", ru, "-c:a", codec, "-f", extension == "m4a" ? "mp4" : extension, converted];
            if (extension is "flac" or "ogg" or "opus") await audio.RunAsync(false, args, token);
            else await VideoRuntime.RunAsync(false, args, token);
            files.Add(await store.ImportAttachmentAsync(topic, converted, token));
        }
        store.SaveContent(topic, [new("user", "Материалы для проверки", Attachments: files.Select(f => f.Id).ToArray())], "");
        await LetItOutAudioTranscription.RecognizePendingAsync(store, topic, 4, token);
        var results = files.Select(f => new { f.Name, State = store.AudioTranscript(topic, f.Id)! }).ToArray();
        var russian = LetItOutAudioTranscription.Join(results[0].State.Parts).ToLowerInvariant();
        var english = LetItOutAudioTranscription.Join(results[1].State.Parts).ToLowerInvariant();
        Assert.IsTrue(russian.Contains("тестов") && russian.Contains("запис"), russian);
        Assert.IsTrue(english.Contains("recording") || english.Contains("narrator"), english);
        Assert.AreEqual("", LetItOutAudioTranscription.Join(results[2].State.Parts));
        foreach (var result in results.Skip(3))
            StringAssert.Contains(LetItOutAudioTranscription.Join(result.State.Parts).ToLowerInvariant(), "тестов", result.Name);
        await File.WriteAllTextAsync(Path.Combine(output, "transcripts.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), token);

        // A fresh attachment forces the actual chat runtime to execute ASR before Gemma.
        var secondTopic = Guid.NewGuid().ToString("N"); store.Create(secondTopic);
        var currentFile = await store.ImportAttachmentAsync(secondTopic, ru, token);
        var history = new[] { new LetItOutSavedMessage("user", "Что умеет программа, о которой говорится в последней записи? Ответь одной фразой.", Attachments: [currentFile.Id]) };
        store.SaveContent(secondTopic, history, "");
        var ready = await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 2, false, new Progress<LetItOutChatPreparationProgress>(), token);
        Assert.IsTrue(ready.Ready); var stages = new List<string>();
        using var runtime = new LetItOutChatRuntime(new(new UserProfileStore(), new IpLocationService())) { MemoryStore = store,
            AudioThreads = () => 4, AttachmentStageChanged = stages.Add, DecideGpu = (_, _) => Task.FromResult(LlamaGpuDecision.Hybrid) };
        try
        {
            var request = new LetItOutChatRequest(secondTopic, Guid.NewGuid().ToString("N"), ready.CoreModelPath!,
                history.Select(m => new LetItOutChatMessage(m.Role, m.Content, m.Attachments)).ToArray(), "ru");
            var answer = await runtime.GenerateAsync(request, new Progress<ModelStreamChunk>(), token);
            await File.WriteAllTextAsync(Path.Combine(output, "gemma-answer.txt"), answer, token);
            Assert.Contains("LetItOut.Attachments.AudioWorking", stages);
            Assert.IsTrue(store.AudioTranscript(secondTopic, currentFile.Id)!.Complete);
            Assert.IsTrue(new[] { "говор", "реч", "голос", "озвуч" }.Any(word => answer.Contains(word, StringComparison.OrdinalIgnoreCase)), answer);
        }
        finally { await runtime.RetireAsync(); }
    }
}
