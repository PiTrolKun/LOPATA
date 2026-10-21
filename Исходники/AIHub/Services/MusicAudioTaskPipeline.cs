using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Optional stages of the ordinary durable music job; no parallel generation queue.</summary>
public sealed class MusicAudioTaskPipeline(IMusicCoverScoreWorker? scoreWorker = null, IMusicStemsWorker? stems = null, Action<string>? log = null,
    IMusicLayerFitter? layerFitter = null)
{
    private void Emit(string line) => log?.Invoke("[Cover] " + line);
    public async Task<MusicYueRequest> PrepareAsync(MusicGenerationJob job, MusicGenerationVariant variant, MusicYueRequest request,
        Action<MusicGenerationVariant> save, CancellationToken token)
    {
        if (job.AudioTask is not { Enabled: true } task) return request;
        task.Validate(job.Variation, true);
        MusicAudioInputPolicy.ValidateExecution(task, job.Variation);
        foreach (var input in task.References.Concat(task.Source is null ? [] : new[] { task.Source })) await MusicAudioTaskFiles.VerifyAsync(input, token);
        request = request with { AudioTask = task };
        if (job.Variation != MusicStudioRuntime.Variation || !task.UsesGenerator) return request;
        if (task.References.Any(r => r.Role == MusicReferenceRole.Voice)) throw new InvalidDataException("Music.Transform.StudioVoiceReference");
        var folder = Path.GetDirectoryName(task.Source?.Path ?? task.References[0].Path)!;
        var score = scoreWorker ?? new MusicCoverScoreWorker();
        if (task.NeedsSource) {
            if (variant.PlanHash is null) {
                Emit("Transcribe source melody" + (task.KeepHarmony ? " and harmony" : ""));
                var abc = scoreWorker is null ? await MusicAudioScoreCache.GetAsync(task.Source!, task.KeepHarmony, () => score.TranscribeAsync(job.ModelsRoot, task.Source!.Path, task.KeepHarmony, Emit, token), token) : await score.TranscribeAsync(job.ModelsRoot, task.Source!.Path, task.KeepHarmony, Emit, token);
                var path = Path.Combine(folder, "source-" + (task.KeepHarmony ? "full" : "melody") + ".abc");
                await AtomicText(path, abc, token);
                variant = variant with { PlanFile = path, PlanHash = await MusicAudioTaskFiles.HashAsync(path, token), PlanHardware = "SheetSage2 Q8 / native auto" }; save(variant);
            }
            await MusicOutputPublisher.MatchHashAsync(variant.PlanFile!, variant.PlanHash!, token);
            request = request with { Abc = await File.ReadAllTextAsync(variant.PlanFile!, token) };
        }
        var facts = new List<MusicStudioReferenceFacts>();
        foreach (var reference in task.References) {
            var path = Path.Combine(folder, "reference-" + reference.Sha256 + ".abc");
            var receipt = path + ".sha256";
            if (!File.Exists(path) || !File.Exists(receipt) || await File.ReadAllTextAsync(receipt, token) != await MusicAudioTaskFiles.HashAsync(path, token)) {
                var abc = scoreWorker is null ? await MusicAudioScoreCache.GetAsync(reference, true, () => score.TranscribeAsync(job.ModelsRoot, reference.Path, true, Emit, token), token) : await score.TranscribeAsync(job.ModelsRoot, reference.Path, true, Emit, token);
                await AtomicText(path, abc, token); await AtomicText(receipt, await MusicAudioTaskFiles.HashAsync(path, token), token);
            }
            var value = await File.ReadAllTextAsync(path, token);
            facts.Add(MusicStudioReferenceFacts.Read(value));
        }
        if (facts.Count > 0) {
            var combined = MusicStudioReferenceFacts.Combine(facts);
            Emit("Studio reference hints: " + combined + "; no timbre/voice/genre transfer; adherence not guaranteed.");
            request = request with { Style = string.Join("; ", new[] { request.Style, "musical reference characteristics: " + combined }.Where(s => !string.IsNullOrWhiteSpace(s))) };
        }
        return request;
    }
    public async Task<MusicGenerationVariant> FinishAsync(MusicGenerationJob job, MusicGenerationVariant variant,
        Action<MusicGenerationVariant> save, CancellationToken token)
    {
        if (job.AudioTask is not { Enabled: true } task) return variant;
        var receipt = variant.AudioTask ?? new();
        if (receipt.OutputHash is not null) {
            await MusicOutputPublisher.MatchHashAsync(receipt.OutputPath!, receipt.OutputHash, token);
            return variant with { AudioFile = receipt.OutputPath, AudioHash = receipt.OutputHash, DurationSeconds = MusicWaveFile.ReadDuration(receipt.OutputPath!).TotalSeconds };
        }
        var folder = Path.Combine(Path.GetDirectoryName(task.Source?.Path ?? task.References[0].Path)!, "finish-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "processed.wav"); var rendered = receipt.RenderPath ?? task.Source?.Path ?? throw new InvalidDataException("Music.Transform.MissingInput");
        if (receipt.RenderHash is not null) await MusicOutputPublisher.MatchHashAsync(rendered, receipt.RenderHash, token);
        if (task.Mode == MusicAudioTaskMode.Restore) {
            Emit("Conservative pause noise gate / peak normalization; no generative restoration.");
            await MusicAudioTaskFiles.VerifyAsync(task.Source!, token);
            await Task.Run(() => MusicAudioTaskDsp.Restore(task.Source!.Path, output, task.ReduceNoise, task.Normalize, token), token);
        } else if (task.Mode == MusicAudioTaskMode.Repaint) {
            Emit("Restore original samples outside edit and crossfade boundary.");
            await Task.Run(() => MusicAudioTaskDsp.Splice(task.Source!.Path, rendered, output, task.EditStart, task.EditEnd, task.Crossfade, token), token);
        } else if (task.NeedsSeparation) {
            var separator = stems ?? new MusicStemsWorker();
            if (receipt.OriginalStems is null) {
                receipt = receipt with { OriginalStems = await separator.SeparateAsync(task.Source!.Path, task.Source.Sha256!, Path.Combine(folder, "original"), Emit, token) };
                variant = variant with { AudioTask = receipt }; save(variant);
            }
            if (receipt.RenderedStems is null) {
                receipt = receipt with { RenderedStems = await separator.SeparateAsync(rendered, receipt.RenderHash!, Path.Combine(folder, "generated"), Emit, token) };
                variant = variant with { AudioTask = receipt }; save(variant);
            }
            foreach (var stage in new[] { receipt.OriginalStems, receipt.RenderedStems }) {
                await MusicOutputPublisher.MatchHashAsync(stage.VoicePcm, stage.VoiceHash, token);
                await MusicOutputPublisher.MatchHashAsync(stage.MusicPcm, stage.MusicHash, token);
            }
            var kept = task.Mode == MusicAudioTaskMode.ReplaceVoice ? receipt.OriginalStems.MusicPcm : receipt.OriginalStems.VoicePcm;
            var replaced = task.Mode == MusicAudioTaskMode.ReplaceVoice ? receipt.RenderedStems.VoicePcm : receipt.RenderedStems.MusicPcm;
            if (receipt.FittedLayerHash is null) {
                var fit = await (layerFitter ?? new MusicAudioTaskAlignment()).FitAsync(receipt.OriginalStems.VoicePcm, receipt.RenderedStems.VoicePcm,
                    replaced, string.IsNullOrWhiteSpace(task.SourceLyrics) ? job.Lyrics : task.SourceLyrics, job.Lyrics,
                    Path.Combine(Path.GetDirectoryName(receipt.OriginalStems.VoicePcm)!, "cover-fit"), Emit, token);
                receipt = receipt with { FittedLayerPath = fit.Path, FittedLayerHash = fit.Hash, Alignment = fit.Summary };
                variant = variant with { AudioTask = receipt }; save(variant);
            }
            if (receipt.FittedLayerPath is null || receipt.Alignment is null) throw new InvalidDataException("Music.Transform.InvalidInput");
            await MusicOutputPublisher.MatchHashAsync(receipt.FittedLayerPath, receipt.FittedLayerHash, token);
            await Task.Run(() => MusicAudioTaskDsp.MixKeeping(kept, receipt.FittedLayerPath, output, token), token);
            Emit("Kept source stem unchanged; new layer level adjusted if required for headroom.");
        } else output = rendered;
        receipt = receipt with { OutputPath = output, OutputHash = await MusicAudioTaskFiles.HashAsync(output, token),
            Detail = JsonSerializer.Serialize(new { operation = task.Mode.ToString(), task.KeepHarmony, task.Crossfade,
                source = task.Source?.Sha256, references = task.References.Select(r => new { r.Sha256, r.Role }),
                score_revision = task.UsesGenerator && job.Variation == MusicStudioRuntime.Variation ? MusicCoverScoreCatalog.Revision : null,
                reference_method = !task.UsesGenerator ? "none" : job.Variation == MusicStudioRuntime.Variation ? "SheetSage2 measured musical attributes; source ABC for covers" : "official ACE packed timbre references",
                alignment = receipt.Alignment,
                processing = task.Mode == MusicAudioTaskMode.Restore ? "soft pause gate / peak normalization, not declipping or spectral reconstruction" : "owned PCM" }) };
        variant = variant with { AudioTask = receipt, AudioFile = output, AudioHash = receipt.OutputHash, DurationSeconds = MusicWaveFile.ReadDuration(output).TotalSeconds }; save(variant);
        return variant;
    }
    private static async Task AtomicText(string path, string text, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, text, token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
