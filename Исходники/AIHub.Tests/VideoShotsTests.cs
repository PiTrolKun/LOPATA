using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoShotsTests
{
    internal static VideoPreparationProject Root()
    {
        var p = VideoWorldTests.Root(); p.CurrentStep = "shots"; p.LastAssistantModelId = "core";
        VideoReferences.Ensure(p).Choice = "skip"; return p;
    }
    internal static VideoShotsJob Job(VideoPreparationProject p, string path, bool translation = false)
    {
        var d = VideoShots.Ensure(p); var source = VideoShots.Source(p);
        var c = new VideoShotCandidate { Source = VideoShots.Hash(source), BaseSnapshot = VideoShots.Snapshot(d.Scenes), Automatic = !translation };
        d.Candidates.Add(c); d.Attempted = true;
        return new(Guid.NewGuid().ToString("N"), p.Id, path, source, p.Form, "core", "test.gguf", EngineSetSelection.ActiveId, "ru-RU", [c.Id], translation);
    }
    internal static VideoConceptAnswer Answer(VideoConceptRequest r)
    {
        if (r.Purpose == VideoShotsQuality.Purpose) return new("{\"valid\":true,\"issues\":[]}", "", "", VideoConceptTests.Model);
        object body;
        if (r.Purpose == VideoShotsProtocol.Outline) body = new { scenes = new[] {
            new { id = "", name = "Часы", intent = "Кот завершает работу", cardIds = Array.Empty<string>(), referenceIds = Array.Empty<string>() },
            new { id = "", name = "Финал", intent = "Кот говорит «Снято!»", cardIds = Array.Empty<string>(), referenceIds = Array.Empty<string>() } } };
        else
        {
            var scene = JsonSerializer.Deserialize<VideoScenePlan>(r.Text)!;
            if (r.Purpose == VideoShotsProtocol.Translate)
            {
                var input = JsonNode.Parse(VideoShotsProtocol.Input(r))!["scene"]!;
                return new(new JsonObject { ["name"] = input["name"]!.DeepClone(), ["intent"] = input["intent"]!.DeepClone(), ["shots"] = input["shots"]!.DeepClone() }.ToJsonString(), "", "", VideoConceptTests.Model);
            }
            var shot = r.Purpose == VideoShotsProtocol.Edit ? scene.Shots.Single() : new VideoShotPlan { Id = "", Name = "Ключ", Start = "Кот вставляет ключ", End = "Механизм движется", Duration = 5 };
            if (r.Purpose == VideoShotsProtocol.Edit) shot.Camera = "Плавное приближение";
            body = new { name = scene.Name, intent = scene.Intent, shots = new[] { shot } };
        }
        return new(VideoShotsProtocol.Serialize(body), "", "", VideoConceptTests.Model);
    }
    [TestMethod]
    public async Task OutlineThenSceneReceiptsResumeWithoutRepeatingCompletedStages()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var job = Job(p, files.File("test.lopataAIVid"));
        var receipts = new VideoConceptReceiptStore(files.File("receipts")); var count = 0;
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => {
            if (++count == 3) throw new OperationCanceledException(); return Task.FromResult(Answer(r)); } };
        Task Save() { VideoShots.Capture(p); VideoPreparationArchive.Save(p, job.ProjectPath); return Task.CompletedTask; }
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoShotsWork.RunAsync(job, p, assistant, receipts, Save, null, null, default));
        Assert.HasCount(2, p.Shots!.Candidates[0].Scenes); Assert.HasCount(1, p.Shots.Candidates[0].Scenes[0].Shots); Assert.IsEmpty(p.Shots.Scenes);
        var loaded = VideoPreparationArchive.Load(job.ProjectPath, files.File("opened")); assistant.Run = (r, _) => Task.FromResult(Answer(r));
        await VideoShotsWork.RunAsync(job, loaded, assistant, receipts, () => Task.CompletedTask, null, null, default);
        Assert.AreEqual(6, assistant.Calls); Assert.IsTrue(VideoShots.Ready(loaded)); Assert.HasCount(2, loaded.Shots!.Scenes); loaded.Validate();
        await VideoShotsWork.RunAsync(job, loaded, assistant, receipts, () => Task.CompletedTask, null, null, default);
        Assert.AreEqual(6, assistant.Calls); Assert.HasCount(1, loaded.Shots.History);
    }
    [TestMethod]
    public async Task LateReplyAndExplicitSelectionKeepManualDraftAndUndoHistory()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var job = Job(p, files.File("test.lopataAIVid"));
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => {
            if (r.Purpose == VideoShotsProtocol.Outline) p.Shots!.Scenes.Add(new() { Name = "Моя сцена", Shots = [new() { Name = "Мой план", Start = "Начало", End = "Конец" }] });
            return Task.FromResult(Answer(r)); } };
        await VideoShotsWork.RunAsync(job, p, assistant, new(files.File("receipts")), () => Task.CompletedTask, null, null, default);
        Assert.AreEqual("Моя сцена", p.Shots!.Scenes[0].Name); Assert.IsTrue(p.Shots.Candidates[0].Complete);
        Assert.IsTrue(VideoShots.Select(p, p.Shots.Candidates[0])); Assert.AreEqual("Часы", p.Shots.Scenes[0].Name);
        Assert.IsTrue(VideoShots.MoveVersion(p, -1)); Assert.AreEqual("Моя сцена", p.Shots.Scenes[0].Name);
        Assert.IsTrue(VideoShots.MoveVersion(p, 1)); Assert.AreEqual("Часы", p.Shots.Scenes[0].Name); p.Validate();
    }
    [TestMethod]
    public async Task TranslationIsBoundedPerShotKeepsLiteralsStructureAndManualVersion()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var d = VideoShots.Ensure(p);
        var s = new VideoScenePlan { Name = "Кот", Intent = "LOPATA говорит «Снято!»", Shots = Enumerable.Range(0, 12).Select(i =>
            new VideoShotPlan { Name = "План " + i, Start = "Начало", End = "Говорит «Снято!»", Duration = i + 1 }).ToList() };
        d.Scenes.Add(s); d.Source = VideoShots.Hash(VideoShots.Source(p)); VideoShots.Capture(p);
        var job = Job(p, files.File("test.lopataAIVid"), true); var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => {
            Assert.HasCount(1, JsonSerializer.Deserialize<VideoScenePlan>(r.Text)!.Shots); return Task.FromResult(Answer(r)); } };
        var receipts = new VideoConceptReceiptStore(files.File("receipts"));
        await VideoShotsWork.RunAsync(job, p, assistant, receipts, () => Task.CompletedTask, null, null, default);
        var t = VideoShots.SelectedTranslation(d, s.Id)!;
        Assert.IsTrue(VideoShots.SameStructure(s, t.Scene)); Assert.IsTrue(VideoShots.TranslationCurrent(d, s));
        Assert.AreEqual(s.Intent, t.Scene.Intent); Assert.AreEqual(s.Shots[0].End, t.Scene.Shots[0].End); Assert.AreEqual(12, assistant.Calls);
        t.Manual = true; t.Scene.Shots[0].Camera = "My own wording";
        var again = Job(p, job.ProjectPath, true);
        await VideoShotsWork.RunAsync(again, p, assistant, receipts, () => Task.CompletedTask, null, null, default);
        Assert.AreSame(t, VideoShots.SelectedTranslation(d, s.Id)); Assert.HasCount(2, d.Translations);
        VideoPreparationArchive.Save(p, job.ProjectPath); var loaded = VideoPreparationArchive.Load(job.ProjectPath, files.File("opened"));
        Assert.AreEqual("My own wording", VideoShots.SelectedTranslation(loaded.Shots!, s.Id)!.Scene.Shots[0].Camera);
        Assert.IsTrue(loaded.Provenance.Models.Any(m => m.Id == VideoConceptTests.Model.Id)); loaded.Validate();
    }
    [TestMethod]
    public void ProtocolRejectsForeignLinksDuplicateKeysIdsAndChangedTranslationTiming()
    {
        var p = Root(); var job = Job(p, "test.lopataAIVid"); var r = new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, job.ProjectPath,
            job.CandidateIds[0], "{}", p.Form, "core", "test.gguf", 1, VideoShotsProtocol.Version, EngineSetSelection.ActiveId, VideoShotsProtocol.Outline,
            ContextText: job.Source, ResponseLanguage: "ru");
        var raw = JsonNode.Parse(Answer(r).Text)!;
        raw["scenes"]![0]!["cardIds"] = new JsonArray(Guid.NewGuid().ToString("N"));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoShotsProtocol.ReadOutline(raw.ToJsonString(), job.Source));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoShotsProtocol.ReadOutline("{\"scenes\":[],\"scenes\":[]}", job.Source));
        var scene = new VideoScenePlan { Name = "Cat", Shots = [new() { Name = "Clock", Start = "A", End = "B", Duration = 4 }] };
        r = r with { Purpose = VideoShotsProtocol.Translate, Text = JsonSerializer.Serialize(scene) };
        raw = JsonNode.Parse(Answer(r).Text)!; raw["shots"]![0]!["duration"] = 7;
        Assert.ThrowsExactly<InvalidDataException>(() => VideoShotsProtocol.ReadScene(raw.ToJsonString(), r));
        raw = JsonNode.Parse(Answer(r).Text)!; raw["shots"]!.AsArray().Add(raw["shots"]![0]!.DeepClone());
        Assert.ThrowsExactly<InvalidDataException>(() => VideoShotsProtocol.ReadScene(raw.ToJsonString(), r));
        r = r with { Purpose = VideoShotsProtocol.Scene };
        foreach (var invalid in new[] { Guid.NewGuid().ToString("N"), ", ", "], " })
        { raw = JsonNode.Parse(Answer(r).Text)!; raw["shots"]![0]!["start"] = invalid;
            Assert.ThrowsExactly<InvalidDataException>(() => VideoShotsProtocol.ReadScene(raw.ToJsonString(), r)); }
        var prompt = VideoShotsProtocol.Instructions(r with { Purpose = VideoShotsProtocol.Scene, Freedom = 2 });
        StringAssert.Contains(prompt, "Свобода: развивай"); Assert.IsFalse(prompt.Contains("Свобода: бережно"));
    }
    [TestMethod]
    public async Task StreamSeparatesActualReasoningAndRejectsTruncatedOutput()
    {
        var reasoning = new System.Text.StringBuilder();
        var events = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Проверяю события\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"scenes\\\":[]}\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(events));
        var result = await LlamaServerRuntimeService.ReadPrivateStreamAsync(input, null, s => reasoning.Append(s), default);
        Assert.AreEqual("Проверяю события", reasoning.ToString()); Assert.IsFalse(result.Contains("Проверяю"));
        StringAssert.Contains(result, "scenes");
        using var truncated = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(events.Replace("stop", "length")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LlamaServerRuntimeService.ReadPrivateStreamAsync(truncated, null, null, default));
        using var cut = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(events.Replace("data: [DONE]", "")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LlamaServerRuntimeService.ReadPrivateStreamAsync(cut, null, null, default));
    }
    [TestMethod]
    public async Task ThreeIndependentLevelsKeepReadyFirstCandidateWhenSecondIsCanceled()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var job = Job(p, files.File("test.lopataAIVid")); var d = p.Shots!;
        d.Candidates[0].Automatic = false;
        foreach (var level in new[] { 2, 3 }) d.Candidates.Add(new() { Source = d.Candidates[0].Source, BaseSnapshot = "[]", Freedom = level });
        job = job with { CandidateIds = d.Candidates.Select(c => c.Id).ToArray() }; var levels = new List<int>();
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => { levels.Add(r.Freedom);
            if (r.Freedom == 2) throw new OperationCanceledException(); return Task.FromResult(Answer(r)); } };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoShotsWork.RunAsync(job, p, assistant, new(files.File("receipts")), () => Task.CompletedTask, null, null, default));
        Assert.IsTrue(d.Candidates[0].Complete); Assert.IsFalse(d.Candidates[1].Complete); Assert.IsEmpty(d.Scenes);
        Assert.IsTrue(VideoShots.Select(p, d.Candidates[0])); Assert.IsTrue(VideoShots.Ready(p)); Assert.IsFalse(levels.Contains(3)); p.Validate();
    }
    [TestMethod]
    public void UndoLastSceneDeletionAndRedoEmptyDocumentBothSurviveArchive()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var d = VideoShots.Ensure(p);
        d.Scenes.Add(new() { Name = "Last scene", Shots = [new() { Name = "Shot", Start = "A", End = "B" }] });
        VideoShots.Capture(p); d.Scenes.Clear(); VideoShots.Capture(p);
        Assert.IsTrue(VideoShots.MoveVersion(p, -1)); Assert.HasCount(1, d.Scenes);
        Assert.IsTrue(VideoShots.MoveVersion(p, 1)); Assert.IsEmpty(d.Scenes);
        VideoPreparationArchive.Save(p, files.File("empty.lopataAIVid")); var loaded = VideoPreparationArchive.Load(files.File("empty.lopataAIVid"), files.File("opened"));
        Assert.IsTrue(VideoShots.MoveVersion(loaded, -1)); Assert.HasCount(1, loaded.Shots!.Scenes);
    }
    [TestMethod]
    public async Task CommonPauseResumeReusesOutlineAndKeepsSingleCompletedRevision()
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = Root(); var job = Job(p, files.File("paused.lopataAIVid")); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var assistant = new VideoConceptTests.FakeAssistant { Run = async (r, token) => {
                if (r.Purpose == VideoShotsProtocol.Scene && !entered.Task.IsCompleted) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
                return Answer(r); } };
            var run = VideoShotsWork.RunAsync(job, p, assistant, new(files.File("receipts")), () => Task.CompletedTask, null, null, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(6)); await controller.PauseAsync();
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.HasCount(2, p.Shots!.Candidates[0].Scenes);
            await controller.ResumeAsync(default); await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(VideoShots.Ready(p)); Assert.HasCount(1, p.Shots.History); Assert.AreEqual(6, assistant.Calls);
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Completed, controller.State.Phase);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    [TestMethod]
    public async Task BadModelReplyIsTerminalAndDoesNotSuggestComponentReinstallation()
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = Root(); var job = Job(p, files.File("bad.lopataAIVid")); var assistant = new VideoConceptTests.FakeAssistant
            { Run = (_, _) => Task.FromResult(new VideoConceptAnswer("{\"scenes\":[]}", "", "", VideoConceptTests.Model)) };
            var error = await Assert.ThrowsExactlyAsync<AIHub.Models.BackgroundOperationFailedException>(() => VideoShotsWork.RunAsync(job, p, assistant, new(files.File("receipts")), () => Task.CompletedTask, null, null, default));
            Assert.AreEqual("VideoPrep.ShotsInvalidReply", error.Message); Assert.IsFalse(ComponentAuditFailures.IsEligible(error));
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Failed, controller.State!.Phase); Assert.IsFalse(controller.BlocksNewWork);
            Assert.IsEmpty(p.Shots!.Scenes); Assert.AreEqual(1, assistant.Calls);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    [TestMethod]
    public void KnownReferenceAndCardLinksArePreservedAndMissingBindingBlocksContinuation()
    {
        var p = Root(); var refs = p.References!; var image = new VideoReferenceImage { Name = "Cat portrait" }; refs.Images.Add(image);
        var card = p.World!.Cards[0]; var binding = VideoReferences.Bind(p, image.Id, card.Id, "appearance", "Round glasses");
        var job = Job(p, "test.lopataAIVid"); var scene = new VideoScenePlan { Name = "Workshop", Intent = "Winding a clock", CardIds = [card.Id], ReferenceIds = [image.Id] };
        var r = new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, job.ProjectPath, job.CandidateIds[0], JsonSerializer.Serialize(scene), p.Form,
            "core", "test.gguf", 1, VideoShotsProtocol.Version, EngineSetSelection.ActiveId, VideoShotsProtocol.Scene, ContextText: job.Source, ResponseLanguage: "ru");
        var raw = JsonNode.Parse(Answer(r).Text)!; raw["shots"]![0]!["cardIds"] = new JsonArray(card.Id); raw["shots"]![0]!["referenceIds"] = new JsonArray(image.Id);
        var filled = VideoShotsProtocol.ReadScene(raw.ToJsonString(), r); p.Shots!.Scenes.Add(filled); Assert.IsTrue(VideoShots.Ready(p));
        Assert.AreEqual(image.Id, filled.Shots[0].ReferenceIds.Single()); Assert.AreEqual(card.Id, filled.Shots[0].CardIds.Single());
        refs.Bindings.Remove(binding); Assert.IsFalse(VideoShots.Ready(p));
    }
}
