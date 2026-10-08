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
    private bool _ready, _starting, _working, _disposed, _checking, _cancelRequested;
    private int _modelCheck;
    public event Action? StateChanged;

    public MusicGenerationSession(MusicWorkspaceControl view, MusicGenerationJobs? jobs = null)
    {
        _jobs = jobs ?? MusicGenerationJobs.Default;
        _view = view; _controller = ApplicationBackgroundOperations.Current;
        _view.Generation.StartPause = StartPauseAsync; _view.Generation.Cancel = CancelAsync;
        _view.Generation.OptionsChanged += RefreshBudget; _view.Wishes.Changed += WishesChanged;
        _view.Editor.ValidityChanged += ValidityChanged;
        if (_controller is not null) _controller.Changed += ControllerChanged;
        _worker.Log += NativeLog;
        if (_worker is MusicYueWorker native) native.HardwareChanged += HardwareChanged;
        _view.Player.ConfigureRepeat(track => _ready && !_working && !_starting && _controller?.HasPending != true && track.JobId is not null,
            track => { _ = RepeatAsync(track); });
    }
    public void Configure(string modelsRoot, Func<string, string> localize)
    {
        _modelsRoot = modelsRoot; _l = localize; RefreshBudget();
        if (!_checking && !_working && File.Exists(Path.Combine(MusicYueRuntime.DirectoryPath, "manifest.json"))) _ = CheckRuntimeAsync();
        RefreshButtons();
    }
    private async Task CheckRuntimeAsync()
    {
        var check = ++_modelCheck; var variation = _view.Generation.Variation;
        _checking = true; _ready = false;
        try
        {
            var token = ApplicationBackgroundOperations.ExitToken;
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
        if (string.IsNullOrWhiteSpace(_modelsRoot)) return;
        var variation = _view.Generation.Variation;
        var cards = MusicModelVariants.Cards(_modelsRoot, variation);
        var card = cards.Single(c => c.ModelArtifactId == variation);
        var tokenizer = variation == MusicModelVariants.Bf16 ? "qwen.tiktoken" : card.Files.Single().RelativePath;
        _view.Editor.ResetTokenizer(); _ = _view.Editor.LoadTokenizerAsync(Path.Combine(card.InstallDirectory, tokenizer));
        if (verifiedHardware is not null) {
            ++_modelCheck; _checking = false; _ready = true;
            _view.Generation.SetHardware("BF16 · " + verifiedHardware.Split(':')[0]); RefreshButtons(); return;
        }
        _ = CheckRuntimeAsync();
    }
    private void WishesChanged(object? sender, EventArgs args) => RefreshBudget();
    private void ValidityChanged(object? sender, EventArgs args) => RefreshButtons();
    public bool HasPendingOrRunning => _working || _controller is { HasPending: true, State.Kind: MusicGenerationRunner.BackgroundKind };
    public bool CanChangeModel => !_starting && !_working &&
        (_controller?.HasPending != true || _controller.State?.Phase == BackgroundOperationPhase.Paused);
    private void RefreshBudget()
    {
        if (_disposed) return;
        var request = new MusicYueRequest("", "", 1, 1, _view.Generation.Options.DurationSeconds ?? 360)
            { Expert = _view.Generation.ExpertSettings };
        _view.Editor.ConfigureRequest(_view.Wishes.RequestStyle, request.Instruction, request.OutputReserve, _view.Wishes.State.Instrumental);
        RefreshButtons();
    }
    private async Task StartPauseAsync()
    {
        if (_disposed || _starting) return;
        MusicGenerationJob? submitted = null;
        try
        {
            if (_controller is { HasPending: true })
            {
                if (_controller.State?.Kind != MusicGenerationRunner.BackgroundKind) { _view.Status.AppendLog(_l("Music.Generation.OtherOperation")); return; }
                if (_controller.State.Phase == BackgroundOperationPhase.Running) await _controller.PauseAsync();
                else if (_controller.State.Phase != BackgroundOperationPhase.Pausing) await _controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
                return;
            }
            if (_working || !_ready || !_view.Editor.TryGetGenerationText(out var lyrics)) return;
            _starting = true; RefreshButtons();
            var options = _view.Generation.Options;
            var job = _jobs.Create(_modelsRoot, _view.Tracks.OutputFolder, options.Title, options.Variants,
                options.DurationSeconds ?? 360, _view.Wishes.RequestStyle, lyrics, expert: _view.Generation.ExpertSettings,
                wishes: MusicWishSnapshot.Capture(_view.Wishes.State), outputSettings: options.Output, artist: options.Artist, comment: options.Comment);
            submitted = job = _view.Projects.Record(job, _view.Projects.Capture());
            await MusicAudioRuntime.Default.PrepareAsync(ApplicationBackgroundOperations.ExitToken);
            _starting = false; await RunAsync(job.Id);
        }
        catch (Exception error) { if (submitted is not null) Outcome(submitted, MusicProjectOutcome.Failed, error.Message); ReportFailure(error); }
        finally { _starting = false; if (!_disposed) RefreshButtons(); }
    }
    private async Task RepeatAsync(MusicTrack track)
    {
        if (_working || _starting || _controller?.HasPending == true || track.JobId is null) return;
        MusicGenerationJob? submitted = null;
        try
        {
            _starting = true; RefreshButtons();
            var original = _jobs.Load(track.JobId); var variant = original.Variants[track.Variant];
            var repeat = _jobs.Create(original.ModelsRoot, _view.Tracks.OutputFolder, original.Title, 1, original.DurationSeconds,
                original.Style, original.Lyrics, variant, original.Expert, original.Wishes, original.Output, original.Artist, original.Comment);
            submitted = repeat = _view.Projects.Record(repeat, MusicProjectSnapshot.FromJob(repeat));
            if (original.Output is not null) await MusicAudioRuntime.Default.PrepareAsync(ApplicationBackgroundOperations.ExitToken);
            // Preserve the exact score used by the completed track, alongside its two seeds.
            repeat = repeat with { RuntimePack = original.RuntimePack };
            if (original.Expert.Cot != "off") {
                var score = _jobs.StagePath(repeat.Id, ".abc"); File.Copy(variant.PlanFile!, score, false);
                repeat = repeat with { Variants = [repeat.Variants[0] with { PlanFile = score, PlanHash = variant.PlanHash, PlanHardware = variant.PlanHardware }] };
            }
            _jobs.Save(repeat);
            _starting = false; await RunAsync(repeat.Id);
        }
        catch (Exception error) { if (submitted is not null) Outcome(submitted, MusicProjectOutcome.Failed, error.Message); ReportFailure(error); }
        finally { _starting = false; if (!_disposed) RefreshButtons(); }
    }
    public Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Kind != MusicGenerationRunner.BackgroundKind) throw new InvalidDataException("Unexpected music operation.");
        var id = state.Input.GetProperty("JobId").GetString() ?? throw new InvalidDataException("Missing music job identifier.");
        return RunAsync(id, state, token);
    }
    private async Task RunAsync(string id, BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_working) throw new InvalidOperationException("Music generation is already active.");
        var job = _jobs.Load(id);
        if (restored is not null) job = _view.Projects.RestoreJob(job);
        _working = true;
        _worker.Log -= NativeLog; if (_worker is MusicYueWorker oldNative) oldNative.HardwareChanged -= HardwareChanged;
        // Re-evaluate hardware on resume, even for a job originally created on CPU.
        _worker = job.Variation == MusicModelVariants.Bf16 ? new MusicBf16Worker() : new MusicYueWorker(MusicYueRuntime.DirectoryPath);
        _worker.Log += NativeLog; if (_worker is MusicYueWorker native) native.HardwareChanged += HardwareChanged;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); RefreshButtons();
        var runner = new MusicGenerationRunner(_jobs, _worker);
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
            var completed = _jobs.Load(id).Variants.All(v => v.Completed);
            Outcome(job, completed ? MusicProjectOutcome.Completed : MusicProjectOutcome.Paused);
            _view.Status.Telemetry.Report(_telemetry, completed ? MusicGenerationStage.Completed : MusicGenerationStage.Paused);
        }
        catch (OperationCanceledException)
        {
            Outcome(job, ApplicationBackgroundOperations.ExitToken.IsCancellationRequested ? MusicProjectOutcome.Paused : MusicProjectOutcome.Cancelled);
            if (!ApplicationBackgroundOperations.ExitToken.IsCancellationRequested && _controller is { IsRunning: false, State.Kind: MusicGenerationRunner.BackgroundKind })
                _controller.DiscardPending(_controller.State!.Id);
            _view.Status.Telemetry.Report(_telemetry, ApplicationBackgroundOperations.ExitToken.IsCancellationRequested
                ? MusicGenerationStage.Paused : MusicGenerationStage.Cancelled);
        }
        catch (Exception error) { Outcome(job, MusicProjectOutcome.Failed, error.Message); ReportFailure(error); }
        finally { _cancel.Dispose(); _cancel = null; _working = false; _cancelRequested = false; if (!_disposed) RefreshButtons(); }
    }
    public void ViewResult(string id)
    { var job = _jobs.Load(id); if (job.ProjectId is not null && !HasPendingOrRunning) _view.Projects.Open(job.ProjectId);
        else foreach (var track in _jobs.Tracks(id)) _view.Tracks.AddTrack(track); }
    private Task CancelAsync()
    {
        if (_controller?.State?.Kind != MusicGenerationRunner.BackgroundKind && !_working) return Task.CompletedTask;
        if (_cancel is not null) { _cancelRequested = true; RefreshButtons(); _cancel.Cancel(); }
        else if (_controller is { IsRunning: false, State: { } state }) {
            Outcome(_jobs.Load(state.Input.GetProperty("JobId").GetString()!), MusicProjectOutcome.Cancelled);
            _controller.DiscardPending(state.Id);
        }
        return Task.CompletedTask;
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
                    Outcome(_jobs.Load(id), outcome);
            }
            RefreshButtons();
        });
    }
    private void RefreshButtons()
    {
        if (_disposed) return;
        var state = _controller?.State; var own = _controller?.HasPending == true && state?.Kind == MusicGenerationRunner.BackgroundKind;
        var paused = own && state!.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown;
        var busy = _working && !paused || own && state!.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing;
        _view.Projects.SetBusy(_starting || _working || own);
        _view.Generation.UpdateState(_view.Editor.CanGenerate && _controller?.HasPending != true && !_starting, busy, paused, _ready,
            _starting || _cancelRequested || own && state!.Phase == BackgroundOperationPhase.Pausing);
        _view.Player.RefreshRepeat();
        StateChanged?.Invoke();
    }
    private void NativeLog(string message)
    {
        if (_disposed) return;
        _view.Status.AppendLog(message); var operation = _telemetry;
        MusicGenerationStage? stage = message.StartsWith("[Load]", StringComparison.Ordinal) ? MusicGenerationStage.Loading
            : message.StartsWith("[ABC]", StringComparison.Ordinal) || message.StartsWith("[AR] Score", StringComparison.Ordinal) ? MusicGenerationStage.Planning
            : message.StartsWith("[AR]", StringComparison.Ordinal) ? MusicGenerationStage.Sequence
            : message.StartsWith("[NAR]", StringComparison.Ordinal) || message.StartsWith("[VAE]", StringComparison.Ordinal) ? MusicGenerationStage.Sound : null;
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
        if (!_view.Status.Telemetry.Report(_telemetry, MusicGenerationStage.Error))
        { _telemetry = _view.Status.Telemetry.Begin(); _view.Status.Telemetry.Report(_telemetry, MusicGenerationStage.Error); }
        _view.Status.AppendLog(_l("Music.Generation.Failed") + " " + error.Message);
    }
    private void Outcome(MusicGenerationJob job, MusicProjectOutcome outcome, string message = "")
    {
        try { _view.Projects.Outcome(job, outcome, message); }
        catch (Exception error) { _view.Status.AppendLog(_l("Music.Projects.WriteError") + " " + error.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel?.Cancel();
        _view.Editor.ValidityChanged -= ValidityChanged; _view.Wishes.Changed -= WishesChanged;
        _view.Generation.OptionsChanged -= RefreshBudget; if (_controller is not null) _controller.Changed -= ControllerChanged;
        _worker.Log -= NativeLog;
        if (_worker is MusicYueWorker native) native.HardwareChanged -= HardwareChanged;
    }
}
