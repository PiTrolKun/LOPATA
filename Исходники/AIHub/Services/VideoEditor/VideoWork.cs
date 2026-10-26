using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VideoEditor;

public sealed record VideoWorkRequest(string Id, string ProjectPath, string Action, string? Destination = null, VideoExportOptions? Export = null,
    string[]? Segments = null, string Brief = "", string Voice = "kokoro-sveta", string Language = "ru", int Rate = 100, bool Whole = false, VideoMusicSpec? Music = null,
    VideoVoiceOptions? VoiceOptions = null);
public sealed record VideoWorkProgress(string Id, string Status, double Fraction, string? Result = null, string? Error = null);
internal sealed class VideoWork(ManagedModelLibraryStore? library = null, IMusicYueWorker? musicWorker = null)
{
    public const string Kind = "video.editor";
    public event Action<VideoWorkProgress>? Changed;
    internal event Action<string, VectorStreamChunk>? SvgChanged;
    public Task<string> RunAsync(VideoWorkRequest request, CancellationToken token, BackgroundOperationState? restored = null) => ApplicationBackgroundOperations.RunAsync(
        Kind, new LocalizationService().T("VideoEditor.Title"), request.ProjectPath, request, async attempt => {
            try { return await ExecuteAsync(request, attempt); }
            catch (Exception error) when (VideoErrors.IsKnownInput(error)) { throw new ArgumentException(error.Message, error); }
        }, token, restored);
    public Task<string> ResumeAsync(BackgroundOperationState state, CancellationToken token) => RunAsync(state.Input.Deserialize<VideoWorkRequest>() ?? throw new InvalidDataException("VideoEditor.InvalidProject"), token, state);
    private async Task<string> ExecuteAsync(VideoWorkRequest request, CancellationToken token)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _)) throw new InvalidDataException("VideoEditor.InvalidProject");
        using var audit = ComponentAudit.CaptureOperation();
        var p = VideoProjectStore.Load(request.ProjectPath);
        var work = Path.Combine(VideoProjectStore.Work(p.Id), "Operations", request.Id); Directory.CreateDirectory(work);
        var resultPath = Path.Combine(work, "result.lopataVideo");
        void Status(string s, double progress = 0, string? result = null) => Changed?.Invoke(new(request.Id, s, progress, result));
        try
        {
            VideoProjectStore.VerifySources(p); await VideoRuntime.PrepareAsync(token);
            if (request.Action == "export")
            {
                if (File.Exists(request.Destination))
                {
                    using var previous = await VideoRuntime.ProbeAsync(request.Destination!, token);
                    if (!previous.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().Any(p => p.Name.Equals("LOPATA_OPERATION", StringComparison.OrdinalIgnoreCase) && p.Value.GetString() == request.Id)) throw new IOException("VideoEditor.NewOutputRequired");
                    if (Math.Abs(double.Parse(previous.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) - (request.Export!.To - request.Export.From)) > .25) throw new IOException("VideoEditor.ExportInvalid");
                    Status("VideoEditor.Done", 1, request.Destination); return request.Destination!;
                }
                var result = await VideoExport.RunAsync(p, request.Destination!, request.Export!, request.Id, value => Status("VideoEditor.Exporting", value), token);
                Status("VideoEditor.Done", 1, result); return result;
            }
            if (File.Exists(resultPath)) p = VideoProjectStore.Load(resultPath);
            if (request.Action == "detect")
            {
                var clips = p.Tracks.Where(t => t.Kind == "video" && !t.Hidden && !t.Locked).SelectMany(t => t.Clips).Where(c => !c.Freeze && c.AssetId is not null && p.Asset(c.AssetId).Kind == "video").ToArray();
                if (clips.Length == 0) throw new InvalidOperationException("VideoEditor.NoVideo");
                Status("VideoEditor.Detecting"); var detections = new Dictionary<string, List<NarrationSegment>>();
                foreach (var clip in clips) {
                    var source = p.Asset(clip.AssetId!);
                    if (!detections.TryGetValue(source.Id, out var events)) detections[source.Id] = events = await VideoAnalysis.DetectAsync(source, Path.Combine(work, "events-" + source.Id), token);
                    VideoEventCuts.Apply(p, clip.Id, events);
                }
            }
            else if (request.Action == "music")
            {
                await VideoBackgroundMusic.RunAsync(p, request.Music ?? throw new InvalidDataException("VideoEditor.MusicInvalid"), work, s => Status(s), token, musicWorker);
            }
            else if (request.Action == "analyze")
            {
                var selected = p.Narration.Where(n => request.Segments is null || request.Segments.Contains(n.Id)).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException("VideoEditor.NoFragment");
                var asset = p.Assets.FirstOrDefault(a => a.Kind == "video") ?? throw new InvalidOperationException("VideoEditor.NoVideo");
                Status("VideoEditor.Analyzing"); await VideoAnalysis.WriteAsync(p, asset, selected, request.Brief, work, s => Status(s), token, library);
            }
            else if (request.Action == "speech")
            {
                var segments = p.Narration.Where(n => !string.IsNullOrWhiteSpace(n.Text) && (request.Segments is null || request.Segments.Contains(n.Id))).OrderBy(n => n.Start).ToArray();
                if (segments.Length == 0) throw new InvalidOperationException("VideoEditor.NoFragment");
                using var narrators = new VideoNarratorBatch(new VideoNarratorRegistry(library));
                var track = p.Tracks.FirstOrDefault(t => t.Kind == "audio" && t.Name == "Narration" && !t.Locked);
                if (track is null) { track = new() { Kind = "audio", Name = "Narration" }; p.Tracks.Add(track); }
                var batches = request.Whole ? new[] { segments } : segments.Select(n => new[] { n }).ToArray();
                var completed = 0;
                foreach (var batch in batches)
                {
                    var n = batch[0]; var id = request.Whole ? "whole" : n.Id;
                    var audio = Path.Combine(work, id + ".wav"); var factsPath = Path.Combine(work, id + ".voice.json"); VideoNarrationReceipt receipt;
                    var voice = request.Whole ? request.Voice : n.Provider; var language = request.Whole ? request.Language : n.Language; var rate = request.Whole ? request.Rate : n.Rate;
                    if (!File.Exists(audio) || !File.Exists(factsPath))
                    {
                        Status("VideoEditor.Speaking", completed / (double)batches.Length);
                        var options = request.Whole ? request.VoiceOptions ?? new() : n.VoiceOptions;
                        VideoNarratorCatalog.Validate(voice, language, rate, options);
                        var narrator = narrators.Get(voice);
                        var text = string.Join("\n", batch.Select(n => n.Text));
                        using var workspace = VideoNarrationWorkspace.Capture(Path.Combine(work, "SpeechChunks", id));
                        var packetProgress = narrator as IVideoNarratorProgress;
                        void UpdateProgress(double fraction) => Status("VideoEditor.Speaking", (completed + fraction) / batches.Length);
                        if (packetProgress is not null) packetProgress.Progress += UpdateProgress;
                        NarratorAudio generated;
                        try { generated = narrator is IConfiguredVideoNarrator configured
                                ? await configured.GenerateAsync(voice, language, text, rate, options, token)
                                : await narrator.GenerateAsync(voice, language, text, rate, token); }
                        finally { if (packetProgress is not null) packetProgress.Progress -= UpdateProgress; }
                        await OutputWaveMetadata.ExportAsync(generated.Path, audio, new OutputProvenance { Scenario = "video-editor/narration", Models = VideoNarrationReceipt.SourceModels(batch.SelectMany(s => s.Models)).Concat(generated.Models).Distinct().ToArray(), ModelHistory = "recorded" });
                        receipt = new(generated.Models, new(voice, language, rate, options)); await receipt.SaveAsync(factsPath, token);
                    }
                    else receipt = VideoNarrationReceipt.Read(factsPath);
                    var facts = receipt.Models;
                    var asset = await VideoImport.ReadAsync(audio, work, token); asset.NarratorProfile = receipt.Profile;
                    asset.Models = VideoNarrationReceipt.SourceModels(batch.SelectMany(s => s.Models)).Concat(facts).Distinct().ToList();
                    if (batch.All(b => b.AudioAssetId is {} oldId && p.Assets.Any(a => a.Id == oldId && a.Path == audio))) { completed++; continue; }
                    var prior = track.Clips.Where(c => batch.Any(b => b.Id == c.NarrationId || b.AudioAssetId == c.AssetId)).ToArray(); foreach (var old in prior) track.Clips.Remove(old);
                    p.Assets.Add(asset); var plannedEnd = batch[^1].End;
                    track.Clips.Add(new() { AssetId = asset.Id, Start = n.Start, Duration = asset.Duration, NarrationId = request.Whole ? null : n.Id, PlannedDuration = plannedEnd - n.Start, SeriesNumbers = batch.SelectMany(b => b.SeriesNumbers).Distinct().Order().ToList() });
                    var totalWords = batch.Sum(b => Math.Max(1, b.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)); double offset = 0;
                    foreach (var b in batch) { b.AudioAssetId = asset.Id; b.Stale = false; b.Models = VideoNarrationReceipt.SourceModels(b.Models).Concat(facts).Distinct().ToArray(); b.SpokenDuration = request.Whole ? null : asset.Duration;
                        b.SpeechOffset = offset; b.SpeechLength = asset.Duration * Math.Max(1, b.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length) / totalWords; offset += b.SpeechLength.Value; }
                    await VideoPreviewAssets.PrepareAsync(p, asset, token); VideoProjectStore.Save(p, resultPath);
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { request.Id, Segment = n.Id, Stage = "speech-generated" }); completed++;
                }
            }
            else if (request.Action == "svg")
            {
                var vector = File.Exists(VectorStore.FilePath(request.Id, "job.json")) ? VectorStore.Load(request.Id).Request : new VectorRequest(request.Id, request.Brief, work, null, DateTimeOffset.UtcNow, Options: new VectorGenerationOptions(1, 1, Background: "transparent"));
                if (!File.Exists(VectorStore.FilePath(vector.Id, "job.json"))) VectorStore.Save(new(vector));
                var live = new InlineProgress<VectorStreamChunk>(chunk => SvgChanged?.Invoke(request.Id, chunk));
                using var generator = new VectorWork(); var output = await generator.RunAsync(vector, (s, _) => Status("Vector." + s), token, live);
                var asset = await VideoImport.ReadAsync(output, work, token); p.Assets.Add(asset);
            }
            else throw new InvalidDataException("VideoEditor.InvalidProject");
            p.ModifiedUtc = DateTimeOffset.UtcNow; VideoProjectStore.Save(p, resultPath); Status("VideoEditor.Done", 1, resultPath); return resultPath;
        }
        catch (OperationCanceledException) { Status("VideoEditor.Interrupted"); throw; }
        catch (Exception error) {
            VideoErrors.Report(error);
            try { await File.WriteAllTextAsync(Path.Combine(work, "failure.json"), JsonSerializer.Serialize(new { request.Action, Error = error.Message, Type = error.GetType().Name, At = DateTimeOffset.UtcNow })); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { OwnedProcessRegistry.Log("video_failure_record_unavailable", "VideoEditor", detail: logError.GetType().Name); }
            Changed?.Invoke(new(request.Id, "VideoEditor.Failed", 0, Error: error.Message)); throw;
        }
    }
}
