using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Services;

internal sealed record ChatSpeechMessage(string Conversation, string Key, string Text);
internal sealed record ChatSpeechRequest(string Id, ChatSpeechMessage Message, VideoNarratorChoice Voice, string? Title = null);
internal sealed record ChatSpeechAudio(string[] Files);

/// <summary>Private, resumable synthesis. Complete WAVs outlive workers and model installations.</summary>
internal sealed class ChatSpeechWork(ManagedModelLibraryStore? library = null, string? root = null, VideoNarratorRegistry? narrators = null)
{
    internal const string Kind = "chat.let_it_out.speech";
    internal static string DefaultRoot => Path.Combine(AppDataPaths.BaseDirectory, "Chat", "Audio");
    private readonly ManagedModelLibraryStore _library = library ?? new();
    private readonly VideoNarratorRegistry _narrators = narrators ?? new(library);
    internal string Root { get; } = root ?? DefaultRoot;
    private sealed record Receipt(string Key, int Part, string Hash);

    // 0.7.15 recorded a completed speech IO failure as Waiting. Preserve the job,
    // inputs and partial WAVs, but release its gate; never resume it automatically.
    internal static void RecoverFailedAttempts(BackgroundOperationController controller)
    {
        foreach (var state in controller.PendingOperations.Where(s => s.Kind == Kind
            && s is { Phase: BackgroundOperationPhase.Waiting, RequiresDecision: true, UserPaused: false, Detail: "IOException" }))
        {
            if (controller.HasLiveOperation(state.Id)) continue;
            try
            {
                var request = state.Input.Deserialize<ChatSpeechRequest>();
                if (request is null) continue;
                Validate(request);
                if (request.Id == state.Id && request.Message.Conversation == state.Project)
                    controller.FailWaiting(state.Id, "ChatSpeech.Failed");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NullReferenceException) { }
        }
    }

    private string Key(ChatSpeechRequest request)
    {
        Validate(request);
        var model = _library.Load(VideoNarratorCatalog.ArtifactId(request.Voice.Id));
        var runtime = model is null ? null : _library.Load(VideoNarratorRuntime.Card(model.ModelsRoot, request.Voice.Id).ModelArtifactId);
        return Hash(JsonSerializer.Serialize(new { request.Message.Text, request.Voice, model = model?.Revision, runtime = runtime?.Revision, schema = 2 }));
    }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void Validate(ChatSpeechRequest request)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || !Guid.TryParseExact(request.Message.Conversation, "N", out _)
            || request.Message.Key.Length != 64 || request.Message.Key.Any(c => !Uri.IsHexDigit(c))
            || string.IsNullOrWhiteSpace(request.Message.Text)) throw new InvalidDataException("ChatSpeech.Invalid");
        VideoNarratorCatalog.Validate(request.Voice.Id, request.Voice.Language, request.Voice.Rate, request.Voice.Options);
    }
    internal ChatSpeechAudio? Cached(ChatSpeechRequest request)
    {
        var key = Key(request); var parts = ChatSpeechText.Parts(request.Message.Text);
        var directory = Path.Combine(Root, request.Message.Conversation, key);
        var files = Enumerable.Range(0, parts.Count).Select(i => Path.Combine(directory, $"{i:D4}.wav")).ToArray();
        return files.Length > 0 && files.Select((file, i) => Valid(file, key, i)).All(ok => ok) ? new(files) : null;
    }
    private static bool Valid(string file, string key, int part)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(file);
        Lopata.Updates.SafeUpdatePath.RejectLinks(file + ".json");
        if (!File.Exists(file) || !File.Exists(file + ".json")) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file + ".json"));
            if (receipt?.Key != key || receipt.Part != part) return false;
            using var stream = File.OpenRead(file);
            if (Convert.ToHexString(SHA256.HashData(stream)) != receipt.Hash) return false;
            using var wave = new WaveFileReader(file);
            return wave.TotalTime > TimeSpan.Zero && wave.WaveFormat.Encoding == WaveFormatEncoding.Pcm;
        }
        catch (Exception error) when (error is IOException or JsonException or FormatException or ArgumentException) { return false; }
    }
    internal async Task<ChatSpeechAudio> GenerateAsync(ChatSpeechRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        Validate(request);
        if (restored is not null && (restored.Kind != Kind || restored.Id != request.Id
            || restored.Input.GetRawText() != JsonSerializer.SerializeToElement(request).GetRawText()))
            throw new InvalidDataException("ChatSpeech.Invalid");
        if (restored is null && Cached(request) is { } cached) return cached;
        var state = restored ?? new BackgroundOperationState { Id = request.Id, Kind = Kind, Project = request.Message.Conversation,
            Title = request.Title ?? "Chat audio", Private = true, Input = JsonSerializer.SerializeToElement(request) };
        return await ApplicationBackgroundOperations.RunAsync(Kind, state.Title, state.Project, request, async attempt =>
        {
            var key = Key(request); var parts = ChatSpeechText.Parts(request.Message.Text);
            if (parts.Count == 0) throw new BackgroundOperationFailedException("ChatSpeech.Empty");
            var directory = Path.Combine(Root, request.Message.Conversation, key);
            Lopata.Updates.SafeUpdatePath.RejectLinks(directory); Directory.CreateDirectory(directory);
            using var batch = new VideoNarratorBatch(_narrators);
            var files = new List<string>();
            for (var i = 0; i < parts.Count; i++)
            {
                attempt.ThrowIfCancellationRequested(); var file = Path.Combine(directory, $"{i:D4}.wav");
                if (!Valid(file, key, i))
                {
                    using var workspace = VideoNarrationWorkspace.Capture(Path.Combine(directory, "work", i.ToString("D4")));
                    var narrator = batch.Get(request.Voice.Id); var choice = request.Voice;
                    NarratorAudio audio;
                    try
                    {
                        audio = narrator is IConfiguredVideoNarrator configured
                            ? await configured.GenerateAsync(choice.Id, choice.Language, parts[i], choice.Rate, choice.Options, attempt)
                            : await narrator.GenerateAsync(choice.Id, choice.Language, parts[i], choice.Rate, attempt);
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not BackgroundOperationFailedException)
                    {
                        // This attempt is over, not awaiting user input. Keep the original
                        // cause for ComponentAudit and never retry a failed native worker.
                        OwnedProcessRegistry.Log("chat_speech_synthesis_failed", "LetItOut.Speech",
                            detail: $"operation={request.Id}; model={choice.Id}; part={i}; cause={error.GetType().Name}");
                        throw new BackgroundOperationFailedException("ChatSpeech.Failed", error);
                    }
                    attempt.ThrowIfCancellationRequested();
                    await PublishAsync(audio, file);
                    using var stream = File.OpenRead(file);
                    var receipt = new Receipt(key, i, Convert.ToHexString(SHA256.HashData(stream)));
                    var receiptPath = file + ".json"; var temporary = receiptPath + ".tmp";
                    Lopata.Updates.SafeUpdatePath.RejectLinks(temporary);
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), attempt);
                    File.Move(temporary, receiptPath, true);
                }
                files.Add(file);
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Key = key, CompletedParts = i + 1, TotalParts = parts.Count });
            }
            return new ChatSpeechAudio(files.ToArray());
        }, token, state);
    }

    private static async Task PublishAsync(NarratorAudio audio, string file)
    {
        // The shared narrator already marks its trial. Copy PCM samples into a fresh WAV so
        // readers find one provenance record for this chat operation, not two conflicting tags.
        var pcm = file + ".pcm.wav"; Lopata.Updates.SafeUpdatePath.RejectLinks(pcm);
        try
        {
            using (var source = new WaveFileReader(audio.Path))
            {
                if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.BitsPerSample != 16 || source.Length == 0)
                    throw new InvalidDataException("ChatSpeech.Failed");
                WaveFileWriter.CreateWaveFile(pcm, source);
            }
            await OutputWaveMetadata.ExportAsync(pcm, file, new OutputProvenance {
                Scenario = "chat/let-it-out/speech", ModelHistory = "recorded", Models = audio.Models });
        }
        finally { if (File.Exists(pcm)) File.Delete(pcm); }
    }
}
