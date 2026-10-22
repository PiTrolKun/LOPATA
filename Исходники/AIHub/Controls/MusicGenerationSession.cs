using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

/// <summary>Connects the page to the application's single durable operation; no separate scheduler.</summary>
public sealed class MusicGenerationSession : IDisposable
{
    private readonly MusicWorkspaceControl _view;
    private readonly MusicGenerationJobs _jobs;
    private IMusicYueWorker _worker = new MusicYueWorker(MusicYueRuntime.DirectoryPath);
    private readonly BackgroundOperationController? _controller;
    private string _modelsRoot = "";
    private Func<string, string> _l = key => key;
    private CancellationTokenSource? _cancel;
    private Guid _telemetry;
    private Guid _loraWarningOperation;
    private bool _ready, _starting, _working, _disposed, _checking, _cancelRequested;
    private bool _aceIdeaPreview;
    public async Task StartAceIdeasAsync(bool preview) {
        if(_starting||_working)throw new InvalidOperationException("Music.Generation.OtherOperation");
        _aceIdeaPreview=preview;try{await StartPauseAsync();}finally{_aceIdeaPreview=false;}
    }
    private int _modelCheck;
    public event Action? StateChanged;
    public event Action<string>? GenerationCompleted;

    public MusicGenerationSession(MusicWorkspaceControl view, MusicGenerationJobs? jobs = null)
    {
        _jobs = jobs ?? MusicGenerationJobs.Default;
        _view = view; _controller = ApplicationBackgroundOperations.Current;
        _view.Generation.StartPause = StartPauseAsync; _view.Generation.Cancel = CancelAsync;
        _view.Generation.PrepareSeparation = () => MusicStemsPreparationWindow.EnsureAsync(_view, _l, ApplicationBackgroundOperations.ExitToken);
        _view.Generation.OptionsChanged += RefreshBudget; _view.Wishes.Changed += WishesChanged;
        _view.Editor.ValidityChanged += ValidityChanged;
        if (_controller is not null) _controller.Changed += ControllerChanged;
        _worker.Log += NativeLog;
        if (_worker is MusicYueWorker native) native.HardwareChanged += HardwareChanged;
        _view.Player.ConfigureRepeat(track => _ready && !_working && !_starting && _controller?.IsRunning != true && _controller?.BlocksNewWork != true && CanRepeat(track),
            track => { _ = RepeatAsync(track); });
    }
    public void Configure(string modelsRoot, Func<string, string> localize)
    {
        _modelsRoot = modelsRoot; _l = localize; RefreshBudget();
        if (MusicHeartMuLaCatalog.NoCircle(_view.Generation.Variation)) EnableDiffStart();
        else if (!_checking && !_working && _controller?.BlocksNewWork != true && (MusicModelVariants.ExternalPipeline(_view.Generation.Variation) ||
            File.Exists(Path.Combine(MusicYueRuntime.DirectoryPath, "manifest.json")))) _ = CheckRuntimeAsync();
        RefreshButtons();
    }
    internal Task EnsureRuntimeReadyAsync()=>_ready?Task.CompletedTask:CheckRuntimeAsync();
    private async Task CheckRuntimeAsync()
    {
        var check = ++_modelCheck; var variation = _view.Generation.Variation;
        _checking = true; _ready = false;
        try
        {
            var token = ApplicationBackgroundOperations.ExitToken;
            if (MusicAceCatalog.IsAce(variation)) {
                var hardware = await MusicAceWorker.ProbeAsync(_modelsRoot, token,
                    line => { if (!_disposed && check == _modelCheck) NativeLog(line); });
                if (!_disposed && check == _modelCheck) { _view.Generation.SetHardware("ACE · " + hardware); _ready = true; }
                return;
            }
            if (variation == MusicStudioRuntime.Variation) {
                await ComponentLicenseGate.EnsureAsync(MusicModelVariants.Components(variation), token);
                await MusicStudioRuntime.VerifyAsync(token); await MusicStudioRuntime.VerifyModelsAsync(_modelsRoot, token);
                var studioRequest = new MusicYueRequest(_view.Wishes.RequestStyle, _view.Editor.Lyrics, 1, 1, _view.Generation.Options.DurationSeconds ?? 360)
                    { Expert = _view.Generation.ExpertSettings };
                var studioChoice = await MusicHardwareProbe.CheckAsync(MusicYueRuntime.DirectoryPath,
                    MusicModelVariants.Artifact(_modelsRoot, variation, MusicComponentCatalog.ModelId),
                    MusicModelVariants.Artifact(_modelsRoot, variation, MusicComponentCatalog.DecoderId), studioRequest, true, token, NativeLog);
                if (!_disposed && check == _modelCheck) { _view.Generation.SetHardware("Studio · " + studioChoice.Device.Backend); _ready = true; }
                return;
            }
            if (variation == MusicModelVariants.Bf16) {
                var hardware = await MusicBf16Worker.ProbeAsync(_modelsRoot, token,
                    line => { if (!_disposed && check == _modelCheck) NativeLog(line); });
                if (!_disposed && check == _modelCheck) { _view.Generation.SetHardware("BF16 · " + hardware.Split(':')[0]); _ready = true; }
                return;
            }
            var directory = MusicYueRuntime.DirectoryPath;
            await ComponentLicenseGate.EnsureAsync(MusicYueRuntime.IsCuda(directory)
                ? [MusicYueRuntime.ComponentId, MusicYueRuntime.CudaComponentId, MusicYueRuntime.VulkanComponentId, .. MusicComponentCatalog.ComponentIds]
                : [MusicYueRuntime.ComponentId, .. MusicComponentCatalog.ComponentIds], token);
            await MusicYueRuntime.VerifyAsync(directory, token);
            var cards = MusicComponentCatalog.CreateCards(_modelsRoot);
            string Artifact(string id) { var card = cards.Single(c => c.ModelArtifactId == id); return Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath); }
            var request = new MusicYueRequest(_view.Wishes.RequestStyle, _view.Editor.Lyrics, 1, 1, _view.Generation.Options.DurationSeconds ?? 360)
                { Expert = _view.Generation.ExpertSettings };
            var choice = await MusicHardwareProbe.CheckAsync(directory, Artifact(MusicComponentCatalog.ModelId),
                Artifact(MusicComponentCatalog.DecoderId), request, true, token, NativeLog);
            if (!_disposed && check == _modelCheck) { HardwareChanged(choice); _ready = true; }
        }
        catch (InsufficientMemoryException error)
        {
            // Entry is an estimate for the current duration; a shorter request may fit. Execution checks again.
            if (!_disposed && check == _modelCheck) { _ready = true; _view.Generation.SetHardware(_l("Music.Hardware.MemoryLow")); NativeLog("[Hardware] " + error.Message); }
        }
        catch (Exception error) { if (!_disposed && check == _modelCheck) _view.Status.AppendLog(_l("Music.Generation.RuntimeMissing") + " " + error.Message); }
        finally { if (check == _modelCheck) { _checking = false; if (!_disposed) RefreshButtons(); } }
    }
    public void ModelChanged(string? verifiedHardware = null)
    {
        _ready = false; RefreshBudget();
        if (MusicHeartMuLaCatalog.NoCircle(_view.Generation.Variation)) {
            EnableDiffStart();
            _view.Generation.SetHardware(verifiedHardware is null ? _l(MusicHeartMuLaCatalog.IsHeart(_view.Generation.Variation) ? "Music.HeartMuLa.HardwareOnStart" : "Music.Diff.HardwareOnStart")
                : MusicModelVariants.Family(_view.Generation.Variation) + " · " + verifiedHardware);
            RefreshButtons(); return;
        }
        if (string.IsNullOrWhiteSpace(_modelsRoot) || _controller?.BlocksNewWork == true) { RefreshButtons(); return; }
        var variation = _view.Generation.Variation;
        if (MusicModelVariants.ExternalPipeline(variation)) {
            if (verifiedHardware is not null) { ++_modelCheck; _checking = false; _ready = true; _view.Generation.SetHardware(MusicModelVariants.Family(variation) + " · " + verifiedHardware); RefreshButtons(); }
            else _ = CheckRuntimeAsync();
            return;
        }
        var cards = MusicModelVariants.Cards(_modelsRoot, variation);
        var card = cards.Single(c => c.ModelArtifactId == MusicModelVariants.Weights(variation));
        var tokenizer = variation == MusicModelVariants.Bf16 ? "qwen.tiktoken" : card.Files.Single().RelativePath;
        _view.Editor.ResetTokenizer(); _ = _view.Editor.LoadTokenizerAsync(Path.Combine(card.InstallDirectory, tokenizer));
        if (verifiedHardware is not null) {
            ++_modelCheck; _checking = false; _ready = true;
            _view.Generation.SetHardware("BF16 · " + verifiedHardware.Split(':')[0]); RefreshButtons(); return;
        }
        _ = CheckRuntimeAsync();
    }
    private void EnableDiffStart()
    {
        // This enables the command, not a verified-runtime claim. The durable worker
        // checks files, licenses and hardware during launch, with cancellation and logs.
        ++_modelCheck; _checking = false; _ready = !string.IsNullOrWhiteSpace(_modelsRoot);
    }
    private void WishesChanged(object? sender, EventArgs args) => RefreshBudget();
    private void ValidityChanged(object? sender, EventArgs args) => RefreshButtons();
    public bool HasPendingOrRunning => _working || _controller is { HasPending: true, State.Kind: MusicGenerationRunner.BackgroundKind };
    public bool CanChangeModel => !_starting && !_working &&
        (_controller?.IsRunning != true || _controller.State?.Phase == BackgroundOperationPhase.Paused) &&
        (_controller?.BlocksNewWork != true || _controller.State?.Phase == BackgroundOperationPhase.Paused);
    private void RefreshBudget()
    {
        if (_disposed) return;
        var request = new MusicYueRequest("", "", 1, 1, _view.Generation.Options.DurationSeconds ?? 360)
            { Expert = _view.Generation.ExpertSettings };
        _view.Editor.ConfigureRequest(_view.Wishes.RequestStyle, request.Instruction, request.OutputReserve, _view.Wishes.State.Instrumental,
            MusicModelVariants.ExternalPipeline(_view.Generation.Variation),
            MusicHeartMuLaCatalog.IsHeart(_view.Generation.Variation) ? "Music.HeartMuLa.EditorHint" : MusicDiffRhythmCatalog.IsDiff(_view.Generation.Variation) ? "Music.Diff.EditorHint" : "Music.Ace.EditorHint");
        RefreshButtons();
    }
    private async Task StartPauseAsync()
    {
        if (_disposed || _starting) return;
        MusicGenerationJob? submitted = null;
        try
        {
            if (_controller is { HasPending: true, State.Kind: MusicGenerationRunner.BackgroundKind } || _controller?.BlocksNewWork == true)
            {
                if (_controller.State?.Kind != MusicGenerationRunner.BackgroundKind) { _view.Status.AppendLog(_l("Music.Generation.OtherOperation")); return; }
                if (_controller.State.Phase == BackgroundOperationPhase.Running) await _controller.PauseAsync();
                else if (_controller.State.Phase != BackgroundOperationPhase.Pausing) await _controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
                return;
            }
            var processingOnly = _view.Generation.AudioTask is { Enabled: true, Mode: MusicAudioTaskMode.Restore };
            if (_working || _controller?.IsRunning == true || !_ready && !processingOnly) return;
            var lyrics = _view.Editor.Lyrics;
            if (!processingOnly && !_view.Editor.TryGetGenerationText(out lyrics)) return;
            if (!processingOnly && _view.Generation.Variation == MusicStudioRuntime.Variation
                && _view.Generation.ExpertSettings.StudioSolver == "ab2" && !MusicStudioRuntime.SelectedProfile.SupportsAb2)
            {
                _view.Status.AppendLog(_l("Music.Expert.UnsupportedSolver"));
                return;
            }
            _starting = true; RefreshButtons();
            var options = _view.Generation.Options;
            var scores=_view.Generation.Variation==MusicStudioRuntime.Variation?_view.Generation.Scores:new AIHub.Models.MusicScoreWorkspace();
            AIHub.Services.MusicScore.MusicScorePipeline.Validate(scores,_view.Generation.Variation,_view.Generation.ExpertSettings,options.AudioTask,options.Melody,lyrics);
            var ideas=MusicAceCatalog.IsAce(_view.Generation.Variation)?_view.Generation.AceIdeas:new AIHub.Services.MusicAceIdeas.MusicAceIdeaWorkspace();
            AIHub.Services.MusicAceIdeas.MusicAceIdeaPipeline.Validate(ideas,_view.Generation.Variation,options.AudioTask,options.Melody);
            if(_aceIdeaPreview)ideas=ideas with {Selected=ideas.Selected.Take(1).ToArray()};
            var totalVariants=_aceIdeaPreview?1:options.Variants*(scores.Enabled?scores.Selected.Length:ideas.Enabled?ideas.Selected.Length:1);
            if(totalVariants>8)throw new InvalidDataException("Music.Score.TooManySongs");
            AIHub.Services.MusicMidi.MusicMelodyPipeline.Validate(options.Melody, options.AudioTask, _view.Generation.Variation);
            if (options.Melody.Enabled && System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view),
                string.Format(_l("Music.Midi.BeforeStart"), options.Melody.Notes.Length, options.Melody.Seconds.ToString("0.##")) + "\n\n" +
                _l(_view.Generation.Variation == MusicStudioRuntime.Variation ? "Music.Midi.Studio" : "Music.Midi.Ace"),
                _l("Music.Midi.Title"), System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information) != System.Windows.MessageBoxResult.OK) return;
            if (options.SeparateTracks && _view.Projects.Current.Steps.Any(s => s.Snapshot.SeparateTracks)) {
                var choice = ConfirmSeparation(); if (choice is null) return;
                _view.Generation.SeparateTracks = choice.Value; options = _view.Generation.Options;
            }
            if (options.SeparateTracks && !await MusicStemsPreparationWindow.EnsureAsync(_view, _l, ApplicationBackgroundOperations.ExitToken)) return;
            if (!await MusicAudioTaskPreparationWindow.EnsureAsync(_view, options.AudioTask, _view.Generation.Variation, _l, ApplicationBackgroundOperations.ExitToken)) return;
            if (options.AudioTask.Enabled && options.AudioTask.NeedsSeparation && (string.IsNullOrWhiteSpace(options.AudioTask.SourceLyrics) || string.IsNullOrWhiteSpace(lyrics))) throw new InvalidDataException("Music.Transform.AlignmentLyrics");
            if (options.AudioTask.Enabled && !ConfirmAudioTask(options.AudioTask)) return;
            var loras = processingOnly ? Array.Empty<AIHub.Services.MusicLora.MusicLoraUse>() : AIHub.Services.MusicLora.MusicLoraCatalog.Capture(_view.Lora.Selection, _view.Generation.Variation);
            if (loras.Length > 0) await AIHub.Services.MusicLora.MusicLoraAssets.EnsureAsync(_modelsRoot, loras, _view.Generation.Variation, ApplicationBackgroundOperations.ExitToken);
            var job = _jobs.Create(_modelsRoot, _view.Tracks.OutputFolder, _aceIdeaPreview?options.Title+" · "+_l("Music.AceIdeas.PreviewName"):options.Title, totalVariants,
                (options.DurationSeconds ?? (MusicDiffRhythmCatalog.IsDiff(_view.Generation.Variation) ? MusicDiffRhythmCatalog.MaximumDuration : 360)), _view.Wishes.RequestStyle, lyrics, expert: _view.Generation.ExpertSettings,
                wishes: MusicWishSnapshot.Capture(_view.Wishes.State), outputSettings: options.Output, artist: options.Artist, comment: options.Comment, durationAutomatic: options.DurationSeconds is null,
                cover: _view.Covers.CaptureSelection(options.Artist, options.Title), loras: loras, separateTracks: options.SeparateTracks);
            job = await CaptureAudioTaskAsync(job, options.AudioTask, ApplicationBackgroundOperations.ExitToken);
            job = AIHub.Services.MusicMidi.MusicMelodyPipeline.Capture(job, options.Melody);
            job=await AIHub.Services.MusicScore.MusicScorePipeline.CaptureAsync(_jobs,job,scores,ApplicationBackgroundOperations.ExitToken);
            job=AIHub.Services.MusicAceIdeas.MusicAceIdeaPipeline.Capture(job,ideas,_aceIdeaPreview);
            if(_aceIdeaPreview)job=job with {DurationAutomatic=false,DurationSeconds=(int)Math.Ceiling(job.Variants[0].AceIdea!.EffectiveData.Duration)};
            _jobs.Save(job);
            submitted = job = _view.Projects.Record(job, _view.Projects.Capture());
            await MusicAudioRuntime.Default.PrepareAsync(ApplicationBackgroundOperations.ExitToken);
            _starting = false; await RunAsync(job.Id);
        }
        catch (Exception error) { if (submitted is not null) Outcome(submitted, MusicProjectOutcome.Failed, error.Message); ReportFailure(error); }
        finally { _starting = false; if (!_disposed) RefreshButtons(); }
    }
    private async Task RepeatAsync(MusicTrack track)
    {
        if (_working || _starting || _controller?.IsRunning == true || _controller?.BlocksNewWork == true || track.JobId is null) return;
        MusicGenerationJob? submitted = null;
        try
        {
            _starting = true; RefreshButtons();
            var original = _jobs.Load(track.JobId); var variant = original.Variants[track.Variant];
            if (MusicModelVariants.WorkspaceVariation(original.Variation) != original.Variation) return;
            var split = original.SeparateTracks;
            if (split) { var choice = ConfirmSeparation(); if (choice is null) return; split = choice.Value; }
            _view.Generation.SeparateTracks = split;
            if (split && !await MusicStemsPreparationWindow.EnsureAsync(_view, _l, ApplicationBackgroundOperations.ExitToken)) return;
            var repeat = _jobs.Create(original.ModelsRoot, _view.Tracks.OutputFolder, original.Title, 1, original.DurationSeconds,
                original.Style, original.Lyrics, variant, original.Expert, original.Wishes, original.Output, original.Artist, original.Comment, original.DurationAutomatic,
                _view.Covers.CaptureSelection(original.Artist, original.Title), original.Loras.ToArray(), split);
            if (!await MusicAudioTaskPreparationWindow.EnsureAsync(_view, original.AudioTask, original.Variation, _l, ApplicationBackgroundOperations.ExitToken)) return;
            repeat = await CaptureAudioTaskAsync(repeat, original.AudioTask ?? new(), ApplicationBackgroundOperations.ExitToken);
            repeat = AIHub.Services.MusicMidi.MusicMelodyPipeline.Capture(repeat, original.Melody ?? new());
            submitted = repeat = _view.Projects.Record(repeat, MusicProjectSnapshot.FromJob(repeat));
            if (original.Output is not null) await MusicAudioRuntime.Default.PrepareAsync(ApplicationBackgroundOperations.ExitToken);
            // Preserve the exact score used by the completed track, alongside its two seeds.
            repeat = repeat with { RuntimePack = original.RuntimePack, RuntimeRevision = original.RuntimeRevision, EngineSetId = original.EngineSetId,
                Scores=original.Scores?.Snapshot() };
            if(variant.AceIdea is {} aceIdea)repeat=repeat with {Variants=[repeat.Variants[0] with {AceIdea=aceIdea}]};
            if (original.Expert.Cot != "off" && variant.PlanFile is not null) {
                var score = _jobs.StagePath(repeat.Id, ".abc"); File.Copy(variant.EffectivePlanFile??variant.PlanFile!, score, false);
                repeat = repeat with { Variants = [repeat.Variants[0] with { PlanFile = score, PlanHash = variant.EffectivePlanHash??variant.PlanHash,
                    ScoreVersionId=variant.ScoreVersionId, PlanHardware = variant.PlanHardware }] };
            }
            _jobs.Save(repeat);
            _starting = false; await RunAsync(repeat.Id);
        }
        catch (Exception error) { if (submitted is not null) Outcome(submitted, MusicProjectOutcome.Failed, error.Message); ReportFailure(error); }
        finally { _starting = false; if (!_disposed) RefreshButtons(); }
    }
    private bool CanRepeat(MusicTrack track)
    {
        if (track.JobId is null) return false;
        try { var variation = _jobs.Load(track.JobId).Variation; return MusicModelVariants.WorkspaceVariation(variation) == variation; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
    public Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Kind != MusicGenerationRunner.BackgroundKind) throw new InvalidDataException("Unexpected music operation.");
        var id = state.Input.GetProperty("JobId").GetString() ?? throw new InvalidDataException("Missing music job identifier.");
        return RunAsync(id, state, token);
    }
    internal void RestorePendingJob()
    {
        if (_working || _starting || _controller is not { HasPending: true, State.Kind: MusicGenerationRunner.BackgroundKind }) return;
        var id = _controller.State!.Input.GetProperty("JobId").GetString() ?? throw new InvalidDataException("Missing music job identifier.");
        if (!_view.Projects.Current.Steps.Any(s => s.JobId == id)) _view.Projects.RestoreJob(_jobs.Load(id));
        RefreshButtons();
    }
    private async Task RunAsync(string id, BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_working) throw new InvalidOperationException("Music generation is already active.");
        var job = _jobs.Load(id);
        if (!await MusicAudioTaskPreparationWindow.EnsureAsync(_view, job.AudioTask, job.Variation, _l, token)) return;
        if (job.SeparateTracks && !await MusicStemsPreparationWindow.EnsureAsync(_view, _l, token)) return;
        if (restored is not null) job = _view.Projects.RestoreJob(job);
        _working = true;
        _worker.Log -= NativeLog; if (_worker is MusicYueWorker oldNative) oldNative.HardwareChanged -= HardwareChanged;
        if (_worker is MusicStudioWorker oldStudio) oldStudio.HardwareChanged -= HardwareChanged;
        // Re-evaluate hardware on resume, even for a job originally created on CPU.
        _worker = MusicHeartMuLaCatalog.IsHeart(job.Variation) ? new MusicHeartMuLaWorker() : MusicDiffRhythmCatalog.IsDiff(job.Variation) ? new MusicDiffRhythmWorker() : MusicAceCatalog.IsAce(job.Variation) ? new MusicAceWorker() : job.Variation == MusicStudioRuntime.Variation ? new MusicStudioWorker()
            : job.Variation == MusicModelVariants.Bf16 ? new MusicBf16Worker() : new MusicYueWorker(MusicYueRuntime.DirectoryPath);
        _worker.Log += NativeLog; if (_worker is MusicYueWorker native) native.HardwareChanged += HardwareChanged;
        if (_worker is MusicStudioWorker studio) studio.HardwareChanged += HardwareChanged;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); RefreshButtons();
        var runner = new MusicGenerationRunner(_jobs, _worker, synchronizeLyrics: _view.LyricsPlayback.SynchronizeGeneratedAsync, log: NativeLog);
        runner.Stage += stage => _view.Status.Telemetry.Report(_telemetry, stage);
        runner.TrackReady += track => _view.Dispatcher.Invoke(() => { var present = _view.Tracks.HasTrack(track.Path!); _view.Tracks.AddTrack(track);
            if (!present) _view.Status.AppendLog(_l("Music.Audio.Saved") + ": " + track.Title); });
        try
        {
            _view.Projects.Outcome(job, MusicProjectOutcome.Running);
            await ApplicationBackgroundOperations.RunAsync(MusicGenerationRunner.BackgroundKind,
                string.IsNullOrWhiteSpace(job.Title) ? Path.GetFileNameWithoutExtension(job.Variants[0].ResultPath) : job.Title, id, new { JobId = id }, async attempt =>
            {
                _telemetry = _view.Status.Telemetry.Begin(TimeSpan.FromSeconds(_controller?.State?.ElapsedSeconds ?? 0));
                await ApplicationBackgroundOperations.RetireModelsAsync();
                await runner.RunAsync(id, attempt); return id;
            }, _cancel.Token, restored);
            var completed = _jobs.Load(id).Variants.All(MusicGenerationJobs.ResultVisible);
            Outcome(job, completed ? MusicProjectOutcome.Completed : MusicProjectOutcome.Paused);
            _view.Status.Telemetry.Report(_telemetry, completed ? MusicGenerationStage.Completed : MusicGenerationStage.Paused);
            if (completed) {
                GenerationCompleted?.Invoke(id);
                if (_jobs.Load(id).Variants.Any(v => v.AudioTask?.Alignment?.Approximate == true))
                    _view.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view), _l("Music.Transform.ApproximateFit"), _l("Music.Transform.Title"),
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information));
            }
        }
        catch (OperationCanceledException)
        {
            var explicitCancel = _cancelRequested && !ApplicationBackgroundOperations.ExitToken.IsCancellationRequested;
            if (explicitCancel) {
                _view.LyricsPlayback.GenerationCanceled(job);
                await PublishCanceledKaraokeAsync(id);
            }
            var settled = _jobs.Load(id).Variants.All(MusicGenerationJobs.ResultVisible);
            Outcome(job, settled ? MusicProjectOutcome.Completed : explicitCancel ? MusicProjectOutcome.Cancelled : MusicProjectOutcome.Paused);
            if (explicitCancel && _controller is { IsRunning: false, State.Kind: MusicGenerationRunner.BackgroundKind })
                _controller.DiscardPending(_controller.State!.Id);
            _view.Status.Telemetry.Report(_telemetry, settled ? MusicGenerationStage.Completed : explicitCancel ? MusicGenerationStage.Cancelled : MusicGenerationStage.Paused);
        }
        catch (Exception error) { Outcome(job, MusicProjectOutcome.Failed, error.Message); ReportFailure(error); }
        finally { _cancel.Dispose(); _cancel = null; _working = false; _cancelRequested = false; if (!_disposed) RefreshButtons(); }
    }
    public void ViewResult(string id)
    { var job = _jobs.Load(id); if (job.ProjectId is not null && !HasPendingOrRunning) _view.Projects.Open(job.ProjectId);
        else foreach (var track in _jobs.Tracks(id)) _view.Tracks.AddTrack(track); }
    private async Task CancelAsync()
    {
        if (_controller?.State?.Kind != MusicGenerationRunner.BackgroundKind && !_working) return;
        if (_cancel is not null) { _cancelRequested = true; RefreshButtons(); _cancel.Cancel(); }
        else if (_controller is { IsRunning: false, State: { } state }) {
            var job = _jobs.Load(state.Input.GetProperty("JobId").GetString()!);
            _view.LyricsPlayback.GenerationCanceled(job);
            await PublishCanceledKaraokeAsync(job.Id); job = _jobs.Load(job.Id);
            Outcome(job, job.Variants.All(MusicGenerationJobs.ResultVisible) ? MusicProjectOutcome.Completed : MusicProjectOutcome.Cancelled);
            _controller.DiscardPending(state.Id);
        }
    }
    private async Task PublishCanceledKaraokeAsync(string id)
    {
        var tracks = await _jobs.CancelPendingKaraokeAsync(id, CancellationToken.None);
        await _view.Dispatcher.InvokeAsync(() => {
            if (_disposed) return;
            foreach (var track in tracks) _view.Tracks.AddTrack(track);
        });
    }
    private void ControllerChanged()
    {
        if (_disposed || _view.Dispatcher.HasShutdownStarted) return;
        _view.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_controller?.State is { Kind: MusicGenerationRunner.BackgroundKind, Phase: BackgroundOperationPhase.Paused })
                _view.Status.Telemetry.Report(_telemetry, MusicGenerationStage.Paused);
            if (_controller?.State is { Kind: MusicGenerationRunner.BackgroundKind } state
                && state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Running
                && state.Input.TryGetProperty("JobId", out var identifier)) {
                var outcome = state.Phase == BackgroundOperationPhase.Paused ? MusicProjectOutcome.Paused : MusicProjectOutcome.Running;
                var id = identifier.GetString();
                if (id is not null && _view.Projects.Current.Steps.Any(s => s.JobId == id && s.Outcome != outcome))
                    { var job = _jobs.Load(id); Outcome(job, job.Variants.All(MusicGenerationJobs.ResultVisible) ? MusicProjectOutcome.Completed : outcome); }
            }
            RefreshButtons();
        });
    }
    private void RefreshButtons()
    {
        if (_disposed) return;
        _view.Generation.CanSeparate = !_view.Wishes.State.Instrumental;
        var state = _controller?.State; var own = _controller?.HasPending == true && state?.Kind == MusicGenerationRunner.BackgroundKind;
        var paused = own && state!.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown;
        var busy = _working && !paused || own && state!.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing;
        var planning=_controller?.IsRunning==true&&(state?.Kind==AIHub.Services.MusicScore.MusicScorePlanWork.Kind||state?.Kind==AIHub.Services.MusicAceIdeas.MusicAceIdeaWork.Kind);
        _view.Projects.SetBusy(_starting || _working || own || planning);
        _view.Generation.UpdateState((_view.Editor.CanGenerate || _view.Generation.AudioTask is { Enabled: true, Mode: MusicAudioTaskMode.Restore, Source: not null }) && _controller?.IsRunning != true && _controller?.BlocksNewWork != true && !_starting, busy, paused, _ready || _view.Generation.AudioTask is { Enabled: true, Mode: MusicAudioTaskMode.Restore },
            _starting || planning || _cancelRequested || own && state!.Phase == BackgroundOperationPhase.Pausing);
        _view.Player.RefreshRepeat();
        StateChanged?.Invoke();
    }
    private void NativeLog(string message)
    {
        if (_disposed) return;
        _view.Status.AppendLog(message.StartsWith("Music.Lora.", StringComparison.Ordinal) ? _l(message) : message); var operation = _telemetry;
        if (message == "Music.Lora.PlannerCombinationWarning" && !_view.Dispatcher.HasShutdownStarted)
            _view.Dispatcher.BeginInvoke(() => {
                if (_disposed || _loraWarningOperation == operation) return;
                _loraWarningOperation = operation; MusicLoraNotices.Show(_view, message, _l);
            });
        if ((_worker is MusicDiffRhythmWorker or MusicHeartMuLaWorker) && message.StartsWith("[Hardware]", StringComparison.Ordinal)
            && !_view.Dispatcher.HasShutdownStarted)
            _view.Dispatcher.Invoke(() => { if (!_disposed) _view.Generation.SetHardware(MusicModelVariants.Family(_view.Generation.Variation) + " · " + message[10..].Trim()); });
        MusicGenerationStage? stage = message.StartsWith("[Stems]", StringComparison.Ordinal) ? MusicGenerationStage.Separating
            : message.StartsWith("[Load]", StringComparison.Ordinal) ? MusicGenerationStage.Loading
            : message.StartsWith("[Plan]", StringComparison.Ordinal) || message.StartsWith("[ABC]", StringComparison.Ordinal) || message.StartsWith("[AR] Score", StringComparison.Ordinal) ? MusicGenerationStage.Planning
            : message.StartsWith("[AR]", StringComparison.Ordinal) ? MusicGenerationStage.Sequence
            : message.StartsWith("[Synth]", StringComparison.Ordinal) || message.StartsWith("[NAR]", StringComparison.Ordinal) || message.StartsWith("[VAE]", StringComparison.Ordinal) ? MusicGenerationStage.Sound : null;
        if (stage is { } value) _view.Status.Telemetry.Report(operation, value);
    }
    private void HardwareChanged(MusicHardwareChoice choice)
    {
        if (_disposed || _view.Dispatcher.HasShutdownStarted) return;
        _view.Dispatcher.Invoke(() =>
        {
            if (_disposed) return;
            var name = _l(choice.Device.Backend == "CPU" ? "Music.Hardware.Cpu" : "Music.Hardware.Gpu");
            var reason = choice.Device.Backend == "CPU" ? " — " + _l("Music.Hardware." + choice.Reason) : "";
            _view.Generation.SetHardware(name + reason);
        });
    }
    private void ReportFailure(Exception error)
    {
        if (error is OperationCanceledException) return;
        if (error.Message.StartsWith("Music.Midi.", StringComparison.Ordinal))
            _view.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view), _l(error.Message), _l("Music.Midi.Title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning));
        if (error.Message.StartsWith("Music.Transform.", StringComparison.Ordinal))
            _view.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view), _l(error.Message), _l("Music.Transform.Title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning));
        if (error is MusicStemsException) {
            _view.Status.AppendLog(_l("Music.Stems.Failed") + " " + error.InnerException?.Message);
            _view.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view), _l("Music.Stems.Failed"), _l("Music.Stems.Title"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning));
        }
        if (!_view.Status.Telemetry.Report(_telemetry, MusicGenerationStage.Error))
        { _telemetry = _view.Status.Telemetry.Begin(); _view.Status.Telemetry.Report(_telemetry, MusicGenerationStage.Error); }
        _view.Status.AppendLog(_l("Music.Generation.Failed") + " " + (error is MusicStemsException || (error.Message.StartsWith("Music.Lora.", StringComparison.Ordinal) || error.Message.StartsWith("Music.Transform.", StringComparison.Ordinal) || error.Message.StartsWith("Music.Midi.", StringComparison.Ordinal)) ? _l(error.Message) : error.Message));
        if (error.Message is "Music.Lora.ScoreCombinationFailure" or "Music.Lora.ScoreFailure") MusicLoraNotices.Show(_view, error.Message, _l);
    }
    private void Outcome(MusicGenerationJob job, MusicProjectOutcome outcome, string message = "")
    {
        try { _view.Projects.Outcome(job, outcome, message); }
        catch (Exception error) { _view.Status.AppendLog(_l("Music.Projects.WriteError") + " " + error.Message); }
    }
    private async Task<MusicGenerationJob> CaptureAudioTaskAsync(MusicGenerationJob job, MusicAudioTask task, CancellationToken token)
    {
        if (task.Enabled) await ApplicationBackgroundOperations.RetireModelsAsync();
        var captured = await MusicAudioTaskFiles.CaptureAsync(task, _jobs.Folder(job.Id), job.Variation, token);
        if (captured is null) return job;
        var expert = job.Expert.Snapshot();
        if (job.Variation == MusicStudioRuntime.Variation && captured.NeedsSource && captured.UsesGenerator)
            expert.Values["cot"] = captured.KeepHarmony ? 0 : 1;
        job = job with { AudioTask = captured, Expert = expert,
            DurationSeconds = captured.UsesGenerator && captured.Source?.End is { } end ? Math.Clamp((int)Math.Ceiling(end), 1, 360) : job.DurationSeconds,
            DurationAutomatic = captured.NeedsSource ? false : job.DurationAutomatic };
        _jobs.Save(job); return job;
    }
    private bool ConfirmAudioTask(MusicAudioTask task)
    {
        var summary = _l("Music.Transform.Mode." + task.Mode) + "\n" + _l("Music.Transform.Summary." + task.Mode)
            + "\n" + _l("Music.Transform.References") + ": " + MusicAudioInputPolicy.ActiveReferences(task, _view.Generation.Variation).Count()
            + "\n\n" + _l(MusicAudioInputPolicy.ReferenceHintKey(_view.Generation.Variation, task.Mode));
        return System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view), summary, _l("Music.Transform.BeforeStart"),
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.OK;
    }
    private bool? ConfirmSeparation()
    {
        var result = System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(_view), _l("Music.Stems.KeepEnabled"), _l("Music.Stems.Title"),
            System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);
        return result == System.Windows.MessageBoxResult.Cancel ? null : result == System.Windows.MessageBoxResult.Yes;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel?.Cancel();
        _view.Generation.PrepareSeparation = null;
        _view.Editor.ValidityChanged -= ValidityChanged; _view.Wishes.Changed -= WishesChanged;
        _view.Generation.OptionsChanged -= RefreshBudget; if (_controller is not null) _controller.Changed -= ControllerChanged;
        _worker.Log -= NativeLog;
        if (_worker is MusicYueWorker native) native.HardwareChanged -= HardwareChanged;
        if (_worker is MusicStudioWorker studio) studio.HardwareChanged -= HardwareChanged;
    }
}
