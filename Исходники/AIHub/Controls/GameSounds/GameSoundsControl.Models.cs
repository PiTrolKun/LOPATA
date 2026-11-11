using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.GameSounds;
using AIHub.Services.OfficeReader;

namespace AIHub.Controls.GameSounds;

internal sealed partial class GameSoundsControl
{
    private BackgroundOperationState? PendingOperation =>
        ApplicationBackgroundOperations.Current?.PendingOperations.FirstOrDefault(s => s.Id == Project.Pending?.Id);

    internal async Task<bool> EnterAsync()
    {
        if (_entered) return true;
        if (_leaving is not null) await _leaving;
        if (_switching) return false;
        _switching = true;
        try
        {
            LoadCards();
            var options = ChooseModel(new());
            if (options is null || !Prepare(options)) return false;
            await WarmAsync(options, initial: true, ApplicationBackgroundOperations.ExitToken);
            if (!Project.Persistent && Project.Number == 0) Project = NewProject();
            if (Project.Options != options && Project.Persistent) Changed();
            Project.Options = options;
            _entered = true; Render();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            Report(error);
            System.Windows.MessageBox.Show(_owner, _status.Text, T("Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _switching = false; UpdateCommands(); }
    }

    internal bool Leave()
    {
        if (!_entered) return true;
        if (!Checkpoint()) return false;
        _entered = false;
        _mixer?.Close();
        StopPlayback();
        ResetThought();
        _warm?.Cancel();
        _leaving = ReleaseOnLeaveAsync();
        return true;
    }

    private async Task ReleaseOnLeaveAsync()
    {
        try
        {
            if (Project.Pending is { } pending && _running is { IsCompleted: false } running)
            {
                if (ApplicationBackgroundOperations.Current is { } controller && PendingOperation?.Phase == BackgroundOperationPhase.Running)
                    await controller.PauseAsync(pending.Id);
                _attempt?.Cancel();
                await running;
            }
            if (_warm is not null && _switching) await _warmFinished;
            await _model.RetireAsync();
        }
        catch (Exception error) { Report(error); }
    }

    private Task _warmFinished = Task.CompletedTask;
    private void LoadCards() => _cards = GameSoundModels.Available(new StorageSettingsStore().LoadOrCreate(), new());

    private ManagedModelArtifactCard Card(GameSoundModelOptions options) =>
        _cards.FirstOrDefault(c => c.ModelArtifactId == options.ModelId) ?? throw new InvalidDataException("GameSounds.NoModel");

    private GameSoundModelOptions? ChooseModel(GameSoundModelOptions current)
    {
        var model = Select(_cards.Select(c => $"{c.DisplayName} · {ReaderAiModelCatalog.Size(c.Files.Sum(f => f.SizeBytes))}"));
        System.Windows.Automation.AutomationProperties.SetName(model, T("Model"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(model, "GameSounds.Model");
        model.SelectedIndex = Math.Max(0, Array.FindIndex(_cards, c => c.ModelArtifactId == current.ModelId));
        var placement = Select(Enum.GetValues<GameSoundPlacement>().Select(value => T("Placement" + value)));
        System.Windows.Automation.AutomationProperties.SetName(placement, T("Placement"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(placement, "GameSounds.Placement");
        placement.SelectedIndex = (int)current.Placement;
        var window = Dialog("ChooseModel", 600, 310);
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(Label("Model", true)); panel.Children.Add(model);
        panel.Children.Add(Label("Placement", true)); panel.Children.Add(placement);
        panel.Children.Add(Label("PlacementHint"));
        panel.Children.Add(MusicWishUi.Footer(_localize, () => window.DialogResult = true, () => window.DialogResult = false));
        window.Content = panel;
        return window.ShowDialog() == true ? current with
        { ModelId = _cards[model.SelectedIndex].ModelArtifactId, Placement = (GameSoundPlacement)placement.SelectedIndex } : null;
    }

    private bool Prepare(GameSoundModelOptions options)
    {
        using var preparation = new ReaderTextPreparation(new());
        var card = Card(options);
        var connections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
        preparation.ParallelConnections = connections;
        var window = new VideoPreparationWindow(_owner, _localize, card.DisplayName, async (download, progress, token) =>
        {
            if (!await preparation.PrepareAsync(card, download, progress, token)) return false;
            var needsGpu = options.Placement is GameSoundPlacement.Vram or GameSoundPlacement.Hybrid;
            var needsCpu = options.Placement == GameSoundPlacement.Cpu;
            if (LlamaRuntimeSelector.InstalledBundles().Any(b => needsGpu ? b.Backend == "Vulkan" : !needsCpu || b.Backend == "CPU"))
                return true;
            if (!download) return false;
            var setId = EngineSetSelection.ActiveId;
            if (setId == NativeRuntimeReleasePolicy.UpdatedSet)
            {
                var sets = EngineSetService.Shared;
                var definition = sets.GetCatalog().Single(s => s.Id == setId);
                await ComponentLicenseGate.EnsureAsync(definition.LicenseIds, token);
                using var installation = ComponentAudit.Installation();
                await sets.DownloadAsync(setId, progress, token);
            }
            else
            {
                var id = needsGpu ? HardwareRuntimeCatalog.LlamaVulkanId : HardwareRuntimeCatalog.LlamaCpuId;
                await new ComponentManager().DownloadAndInstallAsync(id, new Progress<ComponentDownloadProgress>(p =>
                    progress.Report(new(p.ComponentId, "llama.cpp", p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage))), token);
            }
            return LlamaRuntimeSelector.InstalledBundles().Any(b => needsGpu ? b.Backend == "Vulkan" : !needsCpu || b.Backend == "CPU");
        }, ApplicationBackgroundOperations.ExitToken);
        return window.ShowDialog() == true;
    }

    private async Task WarmAsync(GameSoundModelOptions options, bool initial, CancellationToken token)
    {
        using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _warm = cancel; _warmFinished = finished.Task;
        try
        {
            if (initial)
            {
                var progress = Dialog("Warming", 500, 185);
                var content = new StackPanel { Margin = new(20) };
                content.Children.Add(Label("Warming", true));
                content.Children.Add(LabelText(Card(options).DisplayName));
                content.Children.Add(Action("Cancel", () => cancel.Cancel()));
                progress.Content = content;
                Task warmup = Task.CompletedTask;
                progress.Loaded += (_, _) => warmup = RunWarmupAndCloseAsync();
                progress.Closing += (_, _) => { if (!warmup.IsCompleted) cancel.Cancel(); };
                progress.ShowDialog();
                await warmup;

                async Task RunWarmupAndCloseAsync()
                {
                    try { await StartWarmupAsync(); }
                    finally { if (progress.IsVisible) progress.Close(); }
                }
            }
            else { AddLog("Warming"); await StartWarmupAsync(); }
            _status.Text = _model.Session is { } session
                ? F("Ready", T("Placement" + (session.Mode == "Gpu" ? "Vram" : session.Mode)), session.Context) : "";
        }
        finally
        {
            _warm = null; finished.SetResult();
        }

        async Task StartWarmupAsync()
        {
            var card = Card(options);
            await Task.Run(() => _model.WarmAsync(card, options.Placement, cancel.Token), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task ChangeModelAsync()
    {
        if (_switching) return;
        LoadCards();
        var selected = ChooseModel(Project.Options);
        if (selected is null || selected == Project.Options && _model.Session is not null) return;
        _switching = true; UpdateCommands();
        var retry = Project.Pending is not null;
        try
        {
            await StopAttemptAsync();
            await _model.RetireAsync();
            Project.Options = selected; Changed(); RenderLeft();
            if (!Checkpoint()) return;
            if (!Prepare(selected)) return;
            await WarmAsync(selected, initial: false, ApplicationBackgroundOperations.ExitToken);
        }
        finally { _switching = false; UpdateCommands(); }
        if (retry && _entered && Project.Pending is not null) { AddLog("Repeating"); await StartPendingAsync(); }
    }

    private async Task StopAttemptAsync()
    {
        _attempt?.Cancel();
        if (_running is { } running) await running;
        if (Project.Pending is { } pending) ApplicationBackgroundOperations.Current?.DiscardPending(pending.Id);
    }

    private async Task PauseAsync()
    {
        if (Project.Pending is { } pending && ApplicationBackgroundOperations.Current is { } controller)
            await controller.PauseAsync(pending.Id);
        UpdateCommands();
    }

    private async Task ContinueAsync()
    {
        if (Project.Pending is not { } pending || _switching) return;
        if (PendingOperation is not null && ApplicationBackgroundOperations.Current is { } controller)
        {
            await controller.ResumeAsync(pending.Id, ApplicationBackgroundOperations.ExitToken); UpdateCommands(); return;
        }
        if (_running is not { IsCompleted: false } && (_model.Session is not null || Prepare(Project.Options)))
            await StartPendingAsync();
    }

    private async Task CancelAsync()
    {
        await StopAttemptAsync();
        Project.Pending = null;
        Changed(); Checkpoint(); UpdateCommands();
    }

    private async Task SendAsync()
    {
        var prompt = Project.Draft.Trim();
        if (prompt.Length == 0 || Project.Pending is not null || _switching || _running is { IsCompleted: false }) return;
        if (_model.Session is null && !Prepare(Project.Options)) return;
        var target = Project.Generate && (!Project.Orchestrate || Project.Options.EditSelectedLayer) ? Project.Selected : null;
        if (Project.Generate && Project.Orchestrate && Project.Options.EditSelectedLayer && target is null)
            throw new InvalidDataException("GameSounds.ChooseLayer");
        Project.Pending = new(Guid.NewGuid().ToString("N"), prompt, Project.Generate, Project.Orchestrate,
            target, _language(),
            Project.Orchestrate ? Project.Sources(true).ToArray() : null);
        var message = new GameSoundMessage("user", prompt);
        Project.Messages.Add(message); Project.Draft = "";
        AddMessage(message); Changed(); Render(refreshHistory: false); ScrollChatToEnd();
        if (Checkpoint(force: true)) await StartPendingAsync();
    }

    private async Task StartPendingAsync(BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_running is { IsCompleted: false } || Project.Pending is not { } request || !Checkpoint(force: true)) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _attempt = cancel;
        _running = ExecuteAsync(request, restored, cancel.Token);
        UpdateCommands();
        try { await _running; }
        finally { _running = null; _attempt = null; UpdateCommands(); }
    }

    private async Task ExecuteAsync(GameSoundRequest request, BackgroundOperationState? restored, CancellationToken token)
    {
        var pointer = new GameSoundPointer(Project.Id, request.Id);
        var state = restored ?? new BackgroundOperationState { Id = request.Id, Kind = GameSoundModelSession.Kind,
            Title = T("Title"), Project = Project.Id, Input = JsonSerializer.SerializeToElement(pointer) };
        var options = Project.Options;
        var card = Card(options);
        var history = Project.Messages.Take(Project.Messages.FindLastIndex(h => h.Kind == "user")).ToArray();
        var sources = request.Layers ?? [];
        try
        {
            await ApplicationBackgroundOperations.RunAsync(state.Kind, state.Title, state.Project, pointer, async ct =>
            {
                var complete = await Dispatcher.InvokeAsync(() => Project.LastCompletedRequest == request.Id);
                if (complete) return true;
                await Dispatcher.InvokeAsync(() =>
                {
                    AddLog("Processing"); ResetThought(); _status.Text = T("Processing"); UpdateCommands();
                });
                var result = await Task.Run(() => _model.AskAsync(card, options, request, history, sources,
                    update => Dispatcher.Invoke(() => { if (!ct.IsCancellationRequested) OnThought(update); }),
                    detail => OwnedProcessRegistry.Log("game_sounds_model_validation", "GameSounds", detail: detail), ct), ct);
                await Dispatcher.InvokeAsync(() => Commit(pointer, request, result, ct));
                return true;
            }, token, state, _model.ReleaseOtherModelsAsync);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AddLog("RunFailed"); Checkpoint(); Report(error);
        }
        finally { ResetThought(); }
    }

    private void Commit(GameSoundPointer pointer, GameSoundRequest request, GameSoundAnswer result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Project.Id != pointer.ProjectId || Project.Pending?.Id != pointer.RequestId ||
            Project.LastCompletedRequest == pointer.RequestId) return;
        var prior = request.Target;
        if (prior is not null && Project.Recipes.FirstOrDefault(r => r.Id == prior.Id) != prior &&
            !Equivalent(Project.Recipes.FirstOrDefault(r => r.Id == prior.Id), prior))
            throw new InvalidDataException("GameSounds.TargetChanged");
        if (_thought is { Text.Length: > 0 }) Project.Messages.Add(new("thought", _thought.Text));
        GameSoundMessage message;
        if (result.Recipe is { } recipe)
        {
            if (prior is not null) _previous[prior.Id] = prior;
            Project.Replace(recipe); Project.SelectedId = recipe.Id;
            if (request.Orchestrate && prior is null) Project.Tracks.Add(new(recipe.Id));
            message = new(prior is null ? "made" : "updated", recipe.Name);
        }
        else message = new("answer", result.Text);
        Project.Messages.Add(message);
        Project.Pending = null; Project.LastCompletedRequest = pointer.RequestId;
        try { _projects.Save(Project); _dirty = false; }
        catch { Project = _projects.Load(pointer.ProjectId); Render(); throw; }
        if (_thinking is not null) _thinking.IsExpanded = false;
        _thinking = null; _thought = null;
        AddMessage(message);
        _status.Text = T("Done"); Render(refreshHistory: false); ScrollChatToEnd();
    }

    private static bool Equivalent(GameSoundRecipe? current, GameSoundRecipe previous) =>
        current is not null && current.Name == previous.Name && current.Sound == previous.Sound
        && current.Models.SequenceEqual(previous.Models);

    private void ResetThought()
    {
        if (_thinking is not null) _history.Children.Remove(_thinking);
        _thinking = null; _thought = null;
    }
    private void OnThought(MusicPoetryStreamUpdate update)
    {
        if (update.Text.Length > 0 && _thought is null)
        {
            _thought = SelectableChatText("");
            _thinking = new Expander { Header = T("Thinking"), Content = _thought, IsExpanded = true };
            _history.Children.Add(_thinking);
        }
        if (_thought is { } thought && update.Text.Length > 0)
        {
            var start = thought.SelectionStart;
            var length = thought.SelectionLength;
            thought.AppendText(update.Text);
            if (length > 0) thought.Select(start, length);
        }
        if (update.ReasoningEnded && _thinking is not null) _thinking.IsExpanded = false;
        if (_thinking is not null) ScrollChatToEnd();
    }

    internal async Task RestoreAsync(BackgroundOperationState state, CancellationToken token)
    {
        var pointer = state.Input.Deserialize<GameSoundPointer>() ?? throw new InvalidDataException("GameSounds.InvalidProject");
        if (_leaving is not null) await _leaving;
        if (Project.Id != pointer.ProjectId)
        {
            if (!Checkpoint()) throw new IOException("GameSounds.ProjectSaveFailed");
            Project = _projects.Load(pointer.ProjectId); _previous.Clear(); _dirty = false;
        }
        if (Project.LastCompletedRequest == pointer.RequestId)
        {
            await ApplicationBackgroundOperations.RunAsync(state.Kind, state.Title, state.Project, pointer,
                _ => Task.FromResult(true), token, state);
            return;
        }
        if (Project.Pending?.Id != pointer.RequestId) throw new InvalidDataException("GameSounds.InvalidProject");
        LoadCards(); _entered = true; Render();
        await StartPendingAsync(state, token);
    }

    private Window Dialog(string key, double width, double height)
    {
        var window = new Window { Owner = _owner };
        MusicWishUi.PrepareWindow(window, T(key), "GameSounds." + key + ".Window", width);
        window.MinHeight = Math.Min(350, height); window.Height = height;
        return window;
    }

    private GameSoundProject NewProject()
    {
        var project = _projects.Create();
        project.OutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LOPATA", "GameSounds");
        return project;
    }
}
