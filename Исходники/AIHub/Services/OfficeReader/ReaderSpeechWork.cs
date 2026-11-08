using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.OfficeEditor;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderSpeechRequest(string Id, string Text, VideoNarratorChoice Voice, string Title);
internal sealed record ReaderSpeechResult(string Path, double Seconds, VideoNarratorChoice Voice)
{
    internal OutputModelUse[] Models { get; init; } = [];
}

/// <summary>Existing narrators, audit and background gate; one checkpoint per complete PCM fragment.</summary>
internal sealed class ReaderSpeechWork(ReaderSpeechSession session, VideoNarratorRegistry? narrators = null)
{
    internal const string Kind = "office.reader.speech";
    private sealed record PartReceipt(string Hash, OutputModelUse[] Models);
    private readonly VideoNarratorRegistry _narrators = narrators ?? new();

    internal async Task<ReaderSpeechResult> GenerateAsync(ReaderSpeechRequest request, IProgress<(int Done, int Total)>? progress,
        CancellationToken token, Func<Task>? onInferenceStarted = null, OfficeAiJournal? journal = null)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidDataException("Reader.Speech.NoText");
        VideoNarratorCatalog.Validate(request.Voice.Id, request.Voice.Language, request.Voice.Rate, request.Voice.Options);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var parts = ChatSpeechText.Parts(request.Text, stripMarkup: false);
        var notified = false;
        OfficeAiEntry? entry = null;
        async Task StartedAsync(OutputModelUse? model)
        {
            if (notified) return;
            if (onInferenceStarted is not null) await onInferenceStarted();
            if (journal is not null)
            {
                var options = request.Voice.Options;
                entry = journal.NewRequest(request.Text, null, VideoNarratorCatalog.ArtifactId(request.Voice.Id)) with
                {
                    Models = model is null ? [] : [model],
                    Parameters = JsonSerializer.Serialize(new { request.Voice.Language, request.Voice.Rate, options.Speaker, options.Style, options.Steps, options.Preset })
                };
                await journal.WriteAsync(entry);
            }
            notified = true;
        }
        var directory = Path.Combine(session.DirectoryPath, request.Id);
        var state = new BackgroundOperationState { Id = request.Id, Kind = Kind, Title = request.Title,
            Project = session.Id, Private = true, Input = JsonSerializer.SerializeToElement(new { Session = session.Id, Request = request }) };
        var generating = ApplicationBackgroundOperations.RunAsync(Kind, state.Title, session.Id, state.Input, async attempt =>
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(directory); Directory.CreateDirectory(directory);
            using var batch = new VideoNarratorBatch(_narrators);
            var files = new List<string>(); var models = new List<OutputModelUse>();
            try
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    attempt.ThrowIfCancellationRequested(); progress?.Report((i, parts.Count));
                    var file = Path.Combine(directory, $"{i:D6}.wav");
                    var receipt = ReadReceipt(file);
                    if (receipt is null)
                    {
                        using var workspace = VideoNarrationWorkspace.Capture(Path.Combine(directory, "work", i.ToString("D6")));
                        var narrator = batch.Get(request.Voice.Id); var voice = request.Voice;
                        if (narrator is UpstreamVideoNarrator upstream) upstream.BeforeInference = StartedAsync;
                        else await StartedAsync(null);
                        var audio = narrator is IConfiguredVideoNarrator configured
                            ? await configured.GenerateAsync(voice.Id, voice.Language, parts[i], voice.Rate, voice.Options, attempt)
                            : await narrator.GenerateAsync(voice.Id, voice.Language, parts[i], voice.Rate, attempt);
                        attempt.ThrowIfCancellationRequested();
                        using (var source = new WaveFileReader(audio.Path))
                        {
                            if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.BitsPerSample != 16 || source.Length == 0)
                                throw new InvalidDataException("Reader.Speech.Failed");
                            var temporary = file + ".tmp";
                            WaveFileWriter.CreateWaveFile(temporary, source); File.Move(temporary, file, true);
                        }
                        receipt = new(Hash(file), audio.Models);
                        var json = file + ".json";
                        await File.WriteAllTextAsync(json + ".tmp", JsonSerializer.Serialize(receipt), attempt);
                        File.Move(json + ".tmp", json, true);
                    }
                    files.Add(file); models.AddRange(receipt.Models);
                    if (entry is not null) entry = entry with { Models = models.Distinct().ToArray() };
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { CompletedParts = i + 1, TotalParts = parts.Count });
                    progress?.Report((i + 1, parts.Count));
                }
                var result = Path.Combine(directory, "recording.wav");
                var usedModels = models.Distinct().ToArray();
                await AssembleAsync(files, result, usedModels, attempt);
                using var reader = new WaveFileReader(result);
                var recording = new ReaderSpeechResult(result, reader.TotalTime.TotalSeconds, request.Voice) { Models = usedModels };
                if (entry is not null)
                {
                    await journal!.WriteAsync(entry with { Models = usedModels, Status = "completed", ResultName = "recording.wav",
                        Response = FormattableString.Invariant($"WAV · {recording.Seconds:0.##} s") }, result);
                }
                return recording;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                throw new BackgroundOperationFailedException("Reader.Speech.Failed", error);
            }
        }, lifetime.Token, state);
        try { return await generating; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (entry is not null) await journal!.InterruptedAsync(entry, error);
            throw;
        }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static PartReceipt? ReadReceipt(string file)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(file);
        Lopata.Updates.SafeUpdatePath.RejectLinks(file + ".json");
        if (!File.Exists(file) || !File.Exists(file + ".json")) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<PartReceipt>(File.ReadAllText(file + ".json"));
            using var reader = new WaveFileReader(file);
            return receipt?.Hash == Hash(file) && receipt.Models is { Length: > 0 } && reader.Length > 0 ? receipt : null;
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or ArgumentException) { return null; }
    }

    private static async Task AssembleAsync(IReadOnlyList<string> files, string result, OutputModelUse[] models, CancellationToken token)
    {
        var pcm = result + ".pcm";
        try
        {
            await Task.Run(() => {
                using var first = new WaveFileReader(files[0]);
                using var output = new WaveFileWriter(pcm, first.WaveFormat);
                var buffer = new byte[65536];
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested(); using var reader = new WaveFileReader(file);
                    if (!reader.WaveFormat.Equals(output.WaveFormat)) throw new InvalidDataException("Reader.Speech.Failed");
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                }
            }, token);
            token.ThrowIfCancellationRequested();
            await OutputWaveMetadata.ExportAsync(pcm, result, new() { Scenario = "office/reader/speech", CreatedUtc = DateTimeOffset.UtcNow,
                ModelHistory = "recorded", Models = models });
            token.ThrowIfCancellationRequested();
        }
        finally { if (File.Exists(pcm)) File.Delete(pcm); }
    }

    internal static void DiscardOrphans(BackgroundOperationController controller, string? root = null)
    {
        foreach (var state in controller.PendingOperations.Where(s => s.Kind == Kind))
            if (!controller.HasLiveOperation(state.Id)) controller.DiscardPending(state.Id);
        ReaderSpeechSession.RemoveOrphans(root);
    }
}
