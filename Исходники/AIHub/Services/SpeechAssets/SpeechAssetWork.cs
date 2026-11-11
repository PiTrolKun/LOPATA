using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Services.SpeechAssets;

internal sealed record SpeechAssetJobItem(string RowId, string TakeId, string Text);
internal sealed record SpeechAssetJob(string SessionId, VideoNarratorChoice Voice, SpeechAssetJobItem[] Items,
    string? StoreRoot = null, bool ContinueOnError = false, string? RunId = null);
internal sealed record SpeechAssetPart(string TextHash, string AudioHash, OutputModelUse[] Models);

/// <summary>A durable sequence with immutable input, complete-fragment receipts and explicit review afterwards.</summary>
internal sealed class SpeechAssetWork(SpeechAssetStore store, VideoNarratorRegistry? narrators = null, SpeechAssetModelSession? resident = null)
{
    internal const string Kind = "speech.assets.generate";
    internal Task<bool> RunAsync(SpeechAssetJob job, string title, Action<string>? completed, CancellationToken token,
        BackgroundOperationState? restored = null, Action<int, int>? progress = null, Action<int, int>? retry = null) => ApplicationBackgroundOperations.RunAsync(Kind, title, job.SessionId, job,
        attempt => Task.Run(async () =>
        {
            try
            {
            SpeechAssetStore.Id(job.SessionId);
            store.RunState(job, "Running");
            VideoNarratorCatalog.Validate(job.Voice.Id, job.Voice.Language, job.Voice.Rate, job.Voice.Options);
            var library = new ManagedModelLibraryStore();
            var installedModel = library.Load(VideoNarratorCatalog.ArtifactId(job.Voice.Id));
            var installedRuntime = installedModel is null ? null : library.Load(VideoNarratorRuntime.Card(installedModel.ModelsRoot, job.Voice.Id).ModelArtifactId);
            using var lease = resident is null ? null : await resident.AcquireAsync(attempt);
            if (lease is not null) attempt = lease.Token;
            using var ownedBatch = resident is null ? new VideoNarratorBatch(narrators ?? new()) : null;
            var batch = lease?.Batch ?? ownedBatch!;
            var savedRows = store.Load(job.SessionId).Rows.ToDictionary(r => r.Id);
            for (var index = 0; index < job.Items.Length; index++)
            {
                attempt.ThrowIfCancellationRequested();
                progress?.Invoke(index + 1, job.Items.Length);
                var item = job.Items[index]; SpeechAssetStore.Id(item.RowId); SpeechAssetStore.Id(item.TakeId);
                for (var trial = 0; ; trial++)
                {
                try
                {
                if (string.IsNullOrWhiteSpace(item.Text)) throw new InvalidDataException("SpeechAssets.NoText");
                var cached = savedRows.GetValueOrDefault(item.RowId);
                var row = cached?.Takes.Any(t => t.Id == item.TakeId) == true
                    ? cached : store.Load(job.SessionId).Rows.Single(r => r.Id == item.RowId);
                if (row.Takes.FirstOrDefault(t => t.Id == item.TakeId) is { } existing
                    && (!File.Exists(store.FileFor(job.SessionId, existing.File)) || SpeechAssetStore.Hash(store.FileFor(job.SessionId, existing.File)) != existing.Sha256))
                    throw new InvalidDataException("SpeechAssets.AudioChanged");
                if (row.Takes.Any(t => t.Id == item.TakeId))
                {
                    // Replaying validated output needs no UI rebuild, model call or per-row fsync of the large checkpoint.
                    if ((index + 1) % 64 == 0 || index == job.Items.Length - 1)
                        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Completed = index + 1, Total = job.Items.Length });
                    break;
                }
                if (row.Takes.All(t => t.Id != item.TakeId))
                {
                    var parts = ChatSpeechText.Parts(item.Text, stripMarkup: false);
                    if (parts.Count == 0) throw new InvalidDataException("SpeechAssets.NoText");
                    var files = new List<string>(); var models = new List<OutputModelUse>();
                    for (var part = 0; part < parts.Count; part++)
                    {
                        attempt.ThrowIfCancellationRequested();
                        var file = store.FileFor(job.SessionId, $"audio/{item.TakeId}/parts/{part:D6}.wav");
                        Lopata.Updates.SafeUpdatePath.RejectLinks(file + ".json");
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        var textHash = ChatSpeechWork.Hash(JsonSerializer.Serialize(new { Text = parts[part], job.Voice,
                            Model = installedModel?.Revision, Runtime = installedRuntime?.Revision }));
                        SpeechAssetPart? receipt = null;
                        if (File.Exists(file) && File.Exists(file + ".json"))
                            try
                            {
                                receipt = JsonSerializer.Deserialize<SpeechAssetPart>(File.ReadAllText(file + ".json"));
                                if (receipt?.TextHash != textHash || receipt.AudioHash != SpeechAssetStore.Hash(file)) receipt = null;
                            }
                            catch (Exception e) when (e is IOException or JsonException) { receipt = null; }
                        if (receipt is null)
                        {
                            using var workspace = VideoNarrationWorkspace.Capture(
                                store.FileFor(job.SessionId, $"work/{item.TakeId}/{part:D6}"), publishOutput: false);
                            var narrator = batch.Get(job.Voice.Id); var voice = job.Voice;
                            var audio = narrator is IConfiguredVideoNarrator configured
                                ? await configured.GenerateAsync(voice.Id, voice.Language, parts[part], voice.Rate, voice.Options, attempt)
                                : await narrator.GenerateAsync(voice.Id, voice.Language, parts[part], voice.Rate, attempt);
                            attempt.ThrowIfCancellationRequested();
                            using (var source = OpenPcm(audio.Path)) WaveFileWriter.CreateWaveFile(file + ".tmp", source);
                            using (var durable = new FileStream(file + ".tmp", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
                            File.Move(file + ".tmp", file, true);
                            receipt = new(textHash, SpeechAssetStore.Hash(file), audio.Models);
                            SpeechAssetStore.WriteAtomic(file + ".json", JsonSerializer.Serialize(receipt));
                        }
                        files.Add(file); models.AddRange(receipt.Models);
                        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Row = index, Part = part + 1, Total = job.Items.Length });
                    }
                    var relative = $"audio/{item.TakeId}/recording.wav";
                    var result = store.FileFor(job.SessionId, relative);
                    await AssembleAsync(files, result, models.Distinct().ToArray(), attempt);
                    using var reader = OpenPcm(result);
                    var take = new SpeechAssetTake { Id = item.TakeId, Text = item.Text, File = relative, Voice = job.Voice,
                        Models = models.Distinct().ToArray(), Seconds = reader.TotalTime.TotalSeconds,
                        Sha256 = SpeechAssetStore.Hash(result), Selected = row.Takes.Count == 0 };
                    lease?.MarkWarm(job.Voice);
                    // Reload at commit: bulk confirmation and UI unload must not overwrite completed takes.
                    store.Update(job.SessionId, latest =>
                    {
                        var target = latest.Rows.Single(r => r.Id == item.RowId);
                        if (target.Takes.All(t => t.Id != take.Id)) { target.Takes.Add(take); target.Confirmed = false; }
                        target.LastError = "";
                    });
                }
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Completed = index + 1, Total = job.Items.Length });
                completed?.Invoke(item.RowId);
                break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    if (lease is null) batch.Dispose(); else lease.Reset();
                    store.Update(job.SessionId, latest =>
                    {
                        var failed = latest.Rows.Single(r => r.Id == item.RowId);
                        failed.LastError = error.Message; failed.FailedAttempts++;
                    });
                    completed?.Invoke(item.RowId);
                    if (!job.ContinueOnError) throw;
                    if (trial == 3) break; // Initial attempt plus three retries; then continue with the next line.
                    retry?.Invoke(index + 1, trial + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (trial + 1)), attempt);
                }
                }
            }
            store.Update(job.SessionId, latest =>
            {
                if (latest.PendingJob?.Items.Select(i => i.TakeId).SequenceEqual(job.Items.Select(i => i.TakeId)) == true)
                    latest.PendingJob = null;
                if (latest.Runs.FirstOrDefault(r => r.Id == job.RunId) is { } run)
                    run.State = job.Items.Any(i => latest.Rows.Any(r => r.Id == i.RowId && !string.IsNullOrEmpty(r.LastError))) ? "CompletedWithErrors" : "Completed";
            });
            return true;
            }
            catch (OperationCanceledException)
            { store.RunState(job, ApplicationBackgroundOperations.Current?.State?.Phase == BackgroundOperationPhase.Pausing ? "Paused" : "Interrupted"); throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { store.RunState(job, "Failed"); throw new BackgroundOperationFailedException("SpeechAssets.GenerationFailed", error); }
        }, attempt), token, restored);

    internal static WaveFileReader OpenPcm(string path)
    {
        var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm && reader.WaveFormat.BitsPerSample == 16 && reader.Length > 0) return reader;
        reader.Dispose(); throw new InvalidDataException("SpeechAssets.InvalidAudio");
    }
    private static async Task AssembleAsync(List<string> files, string destination, OutputModelUse[] models, CancellationToken token)
    {
        var temporary = destination + ".tmp";
        try
        {
            using (var first = OpenPcm(files[0]))
            using (var output = new WaveFileWriter(temporary, first.WaveFormat))
            {
                var buffer = new byte[65536];
                foreach (var file in files)
                {
                    using var input = OpenPcm(file);
                    if (!input.WaveFormat.Equals(output.WaveFormat)) throw new InvalidDataException("SpeechAssets.InvalidAudio");
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                }
            }
            var facts = new OutputProvenance { Scenario = "speech-assets/generate", CreatedUtc = DateTimeOffset.UtcNow,
                ModelHistory = "recorded", Models = models };
            await AudioMetadataWriter.AttachAsync(temporary, MusicAudioFormat.Wav,
                new Dictionary<string, string> { ["software"] = "LOPATA " + facts.ProgramVersion,
                    ["encoded_by"] = "LOPATA", ["LOPATA_PROVENANCE"] = facts.Json() }, [], token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
