using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoShotsJob(string Id, string ProjectId, string ProjectPath, string Source, string Form, string ModelId,
    string ModelPath, string EngineSetId, string Language, string[] CandidateIds, bool Translation = false, string EditShotId = "");
public static class VideoShotsWork
{
    public static void ValidateRequest(VideoConceptRequest r)
    {
        if (!VideoShotsProtocol.IsRequest(r) || r.PromptVersion != VideoShotsProtocol.Version || !Guid.TryParseExact(r.Id, "N", out _)
            || !Guid.TryParseExact(r.ProjectId, "N", out _) || !Guid.TryParseExact(r.ParentId, "N", out _) || r.Freedom is < 1 or > 3
            || r.Form is not ("story" or "imagery") || r.ContextText is null || r.ContextText.Length > 8_000_000 || r.Text is null || r.Text.Length > 8_000_000
            || string.IsNullOrWhiteSpace(r.ResponseLanguage) || !VideoConceptModels.Ids.Contains(r.ModelId)
            || !r.ProjectPath.EndsWith(VideoPreparationArchive.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        if (r.EngineSetId != EngineSetSelection.ActiveId) throw new BackgroundOperationWaitingException("VideoPrep.AiEngineChanged");
        if (!File.Exists(r.ModelPath)) throw new FileNotFoundException("VideoPrep.AiModelMissing");
    }
    public static void Validate(VideoShotsJob job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _) || !Guid.TryParseExact(job.ProjectId, "N", out _) || job.CandidateIds is null
            || job.CandidateIds.Length is < 1 or > 3 || job.CandidateIds.Distinct().Count() != job.CandidateIds.Length
            || job.CandidateIds.Any(id => !Guid.TryParseExact(id, "N", out _)) || job.Source is null || job.Source.Length > 8_000_000
            || !VideoConceptModels.Ids.Contains(job.ModelId) || job.Form is not ("story" or "imagery")
            || job.ProjectPath is null || !job.ProjectPath.EndsWith(VideoPreparationArchive.Extension, StringComparison.OrdinalIgnoreCase)
            || job.ModelPath is null || job.Language is null || job.EditShotId is null || job.EditShotId != "" && !Guid.TryParseExact(job.EditShotId, "N", out _))
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
    }
    public static string StableId(string seed) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))[..32].ToLowerInvariant();
    public static Task RunAsync(VideoShotsJob job, VideoPreparationProject p, IVideoConceptAssistant assistant, VideoConceptReceiptStore receipts,
        Func<Task> checkpoint, Action<string, int, int>? stage, Action<string>? reasoning, CancellationToken token, BackgroundOperationState? restored = null, string? title = null)
    {
        Validate(job); if (p.Id != job.ProjectId) throw new InvalidDataException("VideoPrep.AiWrongProject");
        var kind = job.Translation ? VideoShots.TranslationKind : VideoShots.Kind;
        var state = restored ?? new BackgroundOperationState { Id = job.Id, Kind = kind, Title = title ?? kind, Project = job.ProjectPath, Input = JsonSerializer.SerializeToElement(job) };
        return ApplicationBackgroundOperations.RunAsync(kind, state.Title, job.ProjectPath, job, async attempt =>
        {
            var d = VideoShots.Ensure(p);
            foreach (var id in job.CandidateIds)
            {
                var candidate = d.Candidates.Single(c => c.Id == id);
                VideoConceptRequest Request(string purpose, string suffix, string text) => new(StableId(id + ":" + VideoShotsProtocol.Version + ":" + suffix
                    + ":" + VideoShots.Hash(text + JsonSerializer.Serialize(candidate.Scenes.Select(s => new { s.Id, s.Name, s.Intent })))), p.Id, job.ProjectPath, id,
                    text, job.Form, job.ModelId, job.ModelPath, candidate.Freedom, VideoShotsProtocol.Version, job.EngineSetId, purpose,
                    ContextText: job.Source, ResponseLanguage: job.Language,
                    ContextOriginal: JsonSerializer.Serialize(candidate.Scenes.Select(s => new { s.Id, s.Name, s.Intent })));
                async Task<VideoConceptAnswer> Answer(VideoConceptRequest r)
                {
                    try
                    {
                    attempt.ThrowIfCancellationRequested(); var a = receipts.Read(r);
                    if (a is null)
                    {
                        await VideoConceptRuntimeLifecycle.BeforeAsync(assistant);
                        try { a = assistant is IVideoShotsProgress streaming ? await streaming.GenerateShotsAsync(r, reasoning, attempt) : await assistant.GenerateAsync(r, attempt); }
                        finally { await VideoConceptRuntimeLifecycle.AfterAsync(assistant); }
                        attempt.ThrowIfCancellationRequested(); VideoConceptProtocol.Validate(a);
                        if (r.Purpose == VideoShotsProtocol.Outline) _ = VideoShotsProtocol.ReadOutline(a.Text, r.ContextText);
                        else if (r.Purpose == VideoShotsQuality.Purpose) _ = VideoShotsQuality.Read(a.Text);
                        else _ = VideoShotsProtocol.ReadScene(a.Text, r);
                        receipts.Save(r, a);
                    }
                    if (!candidate.Models.Contains(a.Model)) candidate.Models.Add(a.Model); return a;
                    }
                    catch (InvalidDataException error) when (error.Message is "VideoPrep.ShotsInvalidReply" or "VideoPrep.ShotsDuplicateReply" or "VideoPrep.TranslationLiteralLost")
                    { throw new BackgroundOperationFailedException(error.Message); }
                }
                async Task<VideoScenePlan> ReviewScene(VideoScenePlan scene, VideoConceptRequest generate, int number, int count)
                {
                    var signature = VideoShots.Hash(VideoShotsProtocol.Version + JsonSerializer.Serialize(scene));
                    if (candidate.ReviewedScenes.TryGetValue(scene.Id, out var checkedSignature) && checkedSignature == signature) return scene;
                    for (var pass = 0; pass < 2; pass++)
                    {
                        stage?.Invoke("ShotsReviewing", number, count);
                        var review = Request(VideoShotsQuality.Purpose, "review:" + scene.Id + ":" + pass,
                            JsonSerializer.Serialize(scene));
                        var report = await Answer(review); var problems = VideoShotsQuality.Read(report.Text);
                        if (problems.Length == 0)
                        { candidate.ReviewedScenes[scene.Id] = VideoShots.Hash(VideoShotsProtocol.Version + JsonSerializer.Serialize(scene)); return scene; }
                        if (pass == 1 || generate.Purpose == VideoShotsProtocol.Edit || generate.TargetText.Length > 0)
                            throw new BackgroundOperationFailedException("VideoPrep.ShotsQualityFailed");
                        stage?.Invoke("ShotsCorrecting", number, count);
                        var retry = generate with { Id = StableId(generate.Id + ":quality-correction"),
                            TargetText = string.Join("\n", problems) };
                        scene = VideoShotsProtocol.ReadScene((await Answer(retry)).Text, retry);
                        candidate.Scenes[candidate.Scenes.FindIndex(s => s.Id == scene.Id)] = scene;
                        await checkpoint();
                    }
                    throw new InvalidOperationException();
                }
                if (job.Translation)
                {
                    var originals = JsonSerializer.Deserialize<List<VideoScenePlan>>(candidate.BaseSnapshot)!;
                    for (var i = 0; i < originals.Count; i++)
                    {
                        var scene = originals[i]; stage?.Invoke("ShotsTranslating", i + 1, originals.Count);
                        var translationId = StableId(id + ":translate:" + scene.Id);
                        if (d.Translations.Any(t => t.Id == translationId)) continue;
                        // A hand-edited scene may contain hundreds of shots. Translate one at a time;
                        // durable receipts allow resuming without repeating completed model requests.
                        VideoScenePlan? translated = null; VideoConceptAnswer? a = null;
                        foreach (var shot in scene.Shots)
                        {
                            var part = VideoShots.Copy(scene); part.Shots = [shot];
                            var r = Request(VideoShotsProtocol.Translate, "translate:" + scene.Id + ":" + shot.Id, JsonSerializer.Serialize(part));
                            a = await Answer(r); var piece = VideoShotsProtocol.ReadScene(a.Text, r);
                            if (translated is null) translated = piece; else translated.Shots.AddRange(piece.Shots);
                        }
                        if (translated is null || a is null) throw new InvalidDataException("VideoPrep.ShotsMissing");
                        var previous = VideoShots.SelectedTranslation(d, scene.Id);
                        d.Translations.Add(new() { Id = translationId, Scene = translated, NativeSnapshot = VideoShots.Snapshot([scene]), Model = a.Model });
                        if (previous is null || !previous.Manual) d.SelectedTranslations[scene.Id] = translationId;
                        await checkpoint();
                    }
                    candidate.Complete = true; await checkpoint(); continue;
                }
                if (job.EditShotId != "")
                {
                    if (candidate.Complete) continue;
                    var all = JsonSerializer.Deserialize<List<VideoScenePlan>>(candidate.BaseSnapshot)!;
                    var scene = all.Single(s => s.Shots.Any(t => t.Id == job.EditShotId)); var input = VideoShots.Copy(scene);
                    input.Shots = input.Shots.Where(t => t.Id == job.EditShotId).ToList();
                    stage?.Invoke("ShotsImproving", 1, 1); var r = Request(VideoShotsProtocol.Edit, "edit", JsonSerializer.Serialize(input)) with
                    { TargetText = JsonSerializer.Serialize(scene.Shots.Where(t => t.Id != job.EditShotId).Select(t => new { t.Name, t.Start, t.End, t.Camera, t.Light })) }; var a = await Answer(r);
                    var reviewed = await ReviewScene(VideoShotsProtocol.ReadScene(a.Text, r), r, 1, 1);
                    var improved = reviewed.Shots.Single(); scene.Shots[scene.Shots.FindIndex(t => t.Id == improved.Id)] = improved;
                    candidate.Scenes = all; candidate.Complete = true;
                }
                else
                {
                    if (candidate.Complete) continue;
                    if (candidate.Scenes.Count == 0)
                    {
                        stage?.Invoke("ShotsOutlining", 0, 0); var r = Request(VideoShotsProtocol.Outline, "outline", "{}");
                        var a = await Answer(r); candidate.Scenes = VideoShotsProtocol.ReadOutline(a.Text, job.Source); await checkpoint();
                    }
                    for (var i = 0; i < candidate.Scenes.Count; i++)
                    {
                        var scene = candidate.Scenes[i];
                        var r = Request(VideoShotsProtocol.Scene, "scene:" + scene.Id, JsonSerializer.Serialize(scene));
                        if (scene.Shots.Count == 0)
                        {
                            stage?.Invoke("ShotsBuildingScene", i + 1, candidate.Scenes.Count);
                            try { scene = VideoShotsProtocol.ReadScene((await Answer(r)).Text, r); }
                            catch (BackgroundOperationFailedException e) when (e.Message == "VideoPrep.ShotsDuplicateReply")
                            {
                                r = r with { Id = StableId(r.Id + ":duplicate-correction"), TargetText = "Удали повторяющиеся планы одного действия. Нужны разные события, не перефразирования одного кадра." };
                                scene = VideoShotsProtocol.ReadScene((await Answer(r)).Text, r);
                            }
                        }
                        candidate.Scenes[i] = scene; await checkpoint();
                        candidate.Scenes[i] = await ReviewScene(scene, r, i + 1, candidate.Scenes.Count); await checkpoint();
                    }
                    candidate.Complete = true;
                }
                if (candidate.Automatic && candidate.Source == VideoShots.Hash(VideoShots.Source(p)) && VideoShots.Snapshot(d.Scenes) == candidate.BaseSnapshot)
                    VideoShots.Select(p, candidate);
                await checkpoint();
            }
            return true;
        }, token, state);
    }
}
