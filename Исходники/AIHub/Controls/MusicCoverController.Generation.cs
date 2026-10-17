using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicCoverController
{
    private async void MusicCompleted(string id)
    {
        await _view.Dispatcher.InvokeAsync(() => { });
        if (_disposed || _active is not null) return;
        try
        {
            var job = _jobs.Load(id); var entry = _store.Read(job.Artist, job.Title);
            if (!entry.IsEnabled || entry.Selected is not null || job.Title.Length == 0 || string.IsNullOrWhiteSpace(job.Lyrics)) return;
            if (_view.IsVisible) Open(job.Artist, job.Title);
            await GenerateAsync(artist: job.Artist, title: job.Title, jobId: id);
        }
        catch (Exception error) { Report(error); }
    }
    internal async Task GenerateHandledAsync(string? prompt = null, bool force = false)
    { try { await GenerateAsync(prompt, force); } catch (Exception error) { Report(error); } }
    private async Task GenerateAsync(string? prompt = null, bool force = false, string? artist = null, string? title = null, string? jobId = null)
    {
        if (_disposed || _active is not null) return;
        if (ApplicationBackgroundOperations.Current?.BlocksNewWork == true) throw new InvalidOperationException("Music.Poetry.OtherOperation");
        var options = _view.Generation.Options;
        artist ??= _window?.Artist ?? options.Artist; title ??= _window?.SongTitle ?? options.Title;
        var previous = _store.Latest(artist, title);
        if (!force && previous is { Stage: "Ready", Ready.Length: 4 }) { Changed?.Invoke(); return; }
        if (_storage is null || _models is null) throw new InvalidOperationException("Music.Rhyme.Unavailable");
        var textModel = MusicPoetryModelCatalog.Choices(_storage).FirstOrDefault(c => c.Candidate.Id == "core")?.Model;
        if (string.IsNullOrWhiteSpace(prompt) && textModel is null) throw new InvalidOperationException("Music.Poetry.ModelMissing");
        var root = ModelsRoot();
        var id = MatchingJob(artist, title, jobId);
        var job = id is null ? null : _jobs.Load(id);
        var current = MusicCoverStore.Key(artist, title) == MusicCoverStore.Key(options.Artist, options.Title);
        var lyrics = jobId is not null ? job!.Lyrics : current ? _view.Editor.Lyrics : job?.Lyrics ?? previous?.Input.Lyrics ?? "";
        var style = jobId is not null ? job!.Style : current ? _view.Wishes.RequestStyle : job?.Style ?? previous?.Input.Style ?? "";
        var input = new MusicCoverInput(Guid.NewGuid().ToString("N"), artist, title, lyrics, style, root, textModel?.Path ?? "", id, prompt);
        if (!await EnsureComponentsAsync(root) || _disposed || _active is not null) return;
        if (ApplicationBackgroundOperations.Current?.BlocksNewWork == true) throw new InvalidOperationException("Music.Poetry.OtherOperation");
        _store.Create(input); await RunAsync(input, ApplicationBackgroundOperations.ExitToken);
    }
    public async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var input = state.Input.Deserialize<MusicCoverInput>() ?? throw new InvalidDataException("Music.Cover.Invalid");
        if (!await EnsureComponentsAsync(input.ModelsRoot)) throw new OperationCanceledException(token);
        await RunAsync(input, token, state);
    }
    public void ViewResult(BackgroundOperationState state)
    {
        var input = state.Input.Deserialize<MusicCoverInput>() ?? throw new InvalidDataException("Music.Cover.Invalid");
        Open(input.Artist, input.Title);
    }
    private async Task RunAsync(MusicCoverInput input, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_active is not null) throw new InvalidOperationException("Music.Cover.Busy");
        _active = input; Error = null; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); Refresh();
        try
        {
            await new MusicCoverWork(_store, _models ?? throw new InvalidOperationException("Music.Rhyme.Unavailable"))
                .RunAsync(input, _ => {
                    if (!_disposed && !_view.Dispatcher.HasShutdownStarted) _view.Dispatcher.Invoke(Refresh);
                }, _l("Music.Cover.Title"), _cancel.Token, restored);
        }
        catch (OperationCanceledException) { Error = _l("Music.Cover.Stopped"); }
        catch (Exception error) { Report(error); }
        finally { _cancel.Dispose(); _cancel = null; _active = null; if (!_disposed) Refresh(); }
    }
    public async Task PauseResumeAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.FindOperation(MusicCoverWork.Kind) is not { } state) return;
        if (state.Phase == BackgroundOperationPhase.Running) await controller.PauseAsync();
        else if (state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting) await controller.ResumeAsync(state.Id, ApplicationBackgroundOperations.ExitToken);
        Refresh();
    }
    public void Cancel()
    {
        if (_preparation is not null) { _preparation.Close(); return; }
        if (_cancel is not null) _cancel.Cancel();
        else if (ApplicationBackgroundOperations.Current?.FindOperation(MusicCoverWork.Kind) is { } state)
            ApplicationBackgroundOperations.Current.DiscardPending(state.Id);
        Refresh();
    }
}
