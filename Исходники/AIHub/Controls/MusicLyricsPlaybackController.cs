using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

/// <summary>One current playback binding. Missing analysis joins the application's durable operation gate.</summary>
public sealed class MusicLyricsPlaybackController : IDisposable
{
    private readonly MusicWorkspaceControl _view;
    private readonly MusicGenerationJobs _jobs;
    private readonly IMusicLyricsSynchronization _sync;
    private readonly BackgroundOperationController? _background;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _attempted = [];
    private CancellationTokenSource? _reading;
    private MusicTimedLyrics? _timing;
    private MusicTrack? _selection;
    private long _selectionRevision;
    private bool _enabled, _disposed, _syncing, _deferred;
    private Func<string, string> _l = key => key;

    public MusicLyricsPlaybackController(MusicWorkspaceControl view, MusicGenerationJobs jobs, IMusicLyricsSynchronization? synchronize = null)
    {
        _view = view; _jobs = jobs; _sync = synchronize ?? new MusicLyricsSynchronization(jobs); _background = ApplicationBackgroundOperations.Current;
        _sync.Log += view.Status.AppendLog;
        view.Player.TrackChanged += TrackSelected;
        view.Player.PositionChanged += Refresh;
        view.Player.PlaybackSeeked += FollowPlayback;
        view.Editor.LyricsChanged += TextChanged;
        view.Projects.LyricsContextChanged += Refresh;
        if (_background is not null) _background.Changed += BackgroundChanged;
    }

    public void Configure(Func<string, string> localize)
    {
        _l = localize; _enabled = true;
        if (_view.Player.CurrentTrack is { } selected) ReadSelected(selected);
    }
    public void Localize(Func<string, string> localize) => _l = localize;

    public void GenerationCanceled(MusicGenerationJob job)
    {
        for (var index = 0; index < job.Variants.Length; index++) _attempted.Add(job.Id + ":" + index);
        if (_selection?.JobId == job.Id) _deferred = false;
    }

    private void TrackSelected(MusicTrack? track)
    {
        _view.Editor.ResumePlaybackFollow();
        if (track is not null) _attempted.Remove(Key(track)); // Explicit selection may retry; internal rereads never loop.
        _selectionRevision++;
        _selection = track; _timing = null; _deferred = false;
        _view.Editor.ClearPlaybackHighlight();
        _reading?.Cancel(); _reading?.Dispose(); _reading = null;
        if (track is not null) ReadSelected(track);
    }

    private void ReadSelected(MusicTrack track)
    {
        if (_disposed) return;
        // Refreshing a cache must not erase a verified clock while another operation finishes.
        _selection = track;
        if (!track.IsExample && track.JobId is not null && _timing is null)
            _view.Player.SetLyricsPreparation(track, pending: true);
        _reading?.Cancel(); _reading?.Dispose();
        _reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ApplicationBackgroundOperations.ExitToken);
        _ = ReadSelectionAsync(track, _selectionRevision, _reading.Token);
    }

    private async Task ReadSelectionAsync(MusicTrack track, long revision, CancellationToken token)
    {
        if (track.IsExample || track.JobId is null || track.Path is null) return;
        try {
            var (timing, readiness, needsLyrics) = await Task.Run(async () => {
                var job = _jobs.Load(track.JobId);
                if (track.Variant < 0 || track.Variant >= job.Variants.Length) throw new InvalidDataException("Unknown playback variant.");
                var primary = string.Equals(track.Path, job.Variants[track.Variant].ResultPath, StringComparison.OrdinalIgnoreCase);
                var result = primary ? await _sync.ReadAsync(job, track.Variant, token) : await _sync.ReadAdditionalAsync(job, track.Variant, token);
                var fresh = _jobs.Load(job.Id);
                await MusicLyricsSynchronization.VerifyTrackAsync(fresh, track.Variant, track.Path, token);
                return (result, fresh.Variants[track.Variant].KaraokeReadiness,
                    fresh.Wishes?.Instrumental != true && !string.IsNullOrWhiteSpace(fresh.Lyrics));
            }, token);
            token.ThrowIfCancellationRequested();
            if (_view.Dispatcher.HasShutdownStarted) return;
            await _view.Dispatcher.InvokeAsync(() => {
                if (_disposed || token.IsCancellationRequested || revision != _selectionRevision || _selection != track) return;
                _timing = timing; if (timing is not null) _deferred = false; Refresh();
                if (timing is not null || !needsLyrics)
                    _view.Player.SetLyricsPreparation(track, pending: false);
                if (readiness == MusicKaraokeReadiness.Unavailable || timing is not null && readiness == MusicKaraokeReadiness.Ready)
                    _view.Tracks.RefreshKaraokeReadiness(track.JobId, track.Variant, readiness.Value);
                // A legacy unavailable flag must not veto a repaired/new provider; its durable negative cache governs retries.
                if (timing is null && needsLyrics && _enabled) RequestAnalysis(track);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) {
            if (!_view.Dispatcher.HasShutdownStarted) await _view.Dispatcher.InvokeAsync(() => {
                if (!_disposed && !token.IsCancellationRequested && revision == _selectionRevision && _selection == track) {
                    _timing = null;
                    _view.Editor.ClearPlaybackHighlight();
                    _view.Player.SetLyricsPreparation(track, pending: false, unavailable: true);
                    _view.Status.AppendLog(_l("Music.LyricsSync.Unavailable") + " " + error.Message);
                }
            });
        }
    }

    private void RequestAnalysis(MusicTrack track)
    {
        if (_disposed || !_enabled || track.JobId is null) return;
        if (_attempted.Contains(Key(track))) {
            // A deliberate pause or unresolved retirement is still pending, not a completed refusal.
            var state = _background?.State;
            if (_background?.HasPending == true && state?.Kind == MusicLyricsSynchronization.BackgroundKind &&
                state.Project == track.JobId) { _deferred = true; return; }
            _deferred = false;
            _view.Player.SetLyricsPreparation(track, pending: false, unavailable: true);
            return;
        }
        if (_syncing || _background?.IsRunning == true || _background?.HasPending == true) { _deferred = true; return; }
        _deferred = false; _ = AnalyzeSelectedAsync(track);
    }

    private async Task AnalyzeSelectedAsync(MusicTrack track, BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_syncing || track.JobId is null) return;
        _syncing = true; _attempted.Add(Key(track));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var operation = _view.Status.Telemetry.Begin();
        try {
            await ApplicationBackgroundOperations.RunAsync(MusicLyricsSynchronization.BackgroundKind,
                _l("Music.LyricsSync.Title") + ": " + track.Title, track.JobId,
                new { JobId = track.JobId, Variant = track.Variant }, async attempt => {
                    _view.Status.Telemetry.Report(operation, MusicGenerationStage.Synchronizing);
                    var timing = await SynchronizeSelectedAsync(track, attempt);
                    if (ApplicationBackgroundOperations.Current is { IsInOperationScope: true } controller)
                        controller.SaveCheckpoint(new { JobId = track.JobId, Variant = track.Variant, AnalysisFinished = true });
                    return timing;
                }, lifetime.Token, restored);
            _view.Status.Telemetry.Report(operation, MusicGenerationStage.Completed);
        }
        catch (OperationCanceledException) { _view.Status.Telemetry.Report(operation,
            ApplicationBackgroundOperations.ExitToken.IsCancellationRequested ? MusicGenerationStage.Paused : MusicGenerationStage.Cancelled); }
        catch (Exception error) {
            _view.Status.Telemetry.Report(operation, MusicGenerationStage.Error);
            if (!_disposed) _view.Status.AppendLog(_l("Music.LyricsSync.Unavailable") + " " + error.Message);
            OwnedProcessRegistry.Log("music_lyrics_sync_failed", "Music.LyricsAlignment", detail: error.GetType().Name);
        }
        finally {
            _syncing = false;
            if (!_disposed && _selection is { } current) {
                if (Key(current) == Key(track)) ReadSelected(current);
                else if (_deferred) RequestAnalysis(current);
            }
        }
    }

    private async Task<MusicTimedLyrics?> SynchronizeSelectedAsync(MusicTrack track, CancellationToken token)
    {
        try { return await Task.Run(() => _sync.SynchronizeAsync(track.JobId!, track.Variant, token), token); }
        // Cancellation keeps pause/exit semantics. Registry retirement failures are AggregateException and cannot release the gate.
        catch (Exception error) when (error is not OperationCanceledException and not AggregateException) {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            token.ThrowIfCancellationRequested();
            _view.Status.AppendLog(_l("Music.LyricsSync.Unavailable") + " " + error.Message);
            OwnedProcessRegistry.Log("music_lyrics_sync_failed", "Music.LyricsAlignment", detail: error.GetType().Name);
            var verified = false;
            try {
                var fresh = _jobs.Load(track.JobId!);
                if (track.Variant < 0 || track.Variant >= fresh.Variants.Length) throw new InvalidDataException("Unknown playback variant.");
                await MusicLyricsSynchronization.VerifyTrackAsync(fresh, track.Variant, fresh.Variants[track.Variant].ResultPath, token);
                verified = true;
            }
            catch (Exception receiptError) when (receiptError is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) {
                // A damaged source is not an unresolved model operation; reject timing without rewriting its receipt/readiness.
                _view.Status.AppendLog(_l("Music.LyricsSync.Unavailable") + " " + receiptError.Message);
            }
            token.ThrowIfCancellationRequested();
            if (verified) {
                _jobs.SetKaraokeReadiness(track.JobId!, track.Variant, MusicKaraokeReadiness.Unavailable);
                if (!_view.Dispatcher.HasShutdownStarted) await _view.Dispatcher.InvokeAsync(() => {
                    if (!_disposed) _view.Tracks.RefreshKaraokeReadiness(track.JobId!, track.Variant, MusicKaraokeReadiness.Unavailable);
                });
            }
            return null;
        }
    }

    public async Task SynchronizeGeneratedAsync(MusicGenerationJob job, int index, CancellationToken token)
    {
        if (!_enabled) { _jobs.SetKaraokeReadiness(job.Id, index, MusicKaraokeReadiness.Unavailable); return; }
        try {
            token.ThrowIfCancellationRequested();
            if (job.Wishes?.Instrumental == true || string.IsNullOrWhiteSpace(job.Lyrics)) {
                _jobs.SetKaraokeReadiness(job.Id, index, MusicKaraokeReadiness.Ready); return;
            }
            _view.Status.AppendLog(_l("Music.LyricsSync.Title"));
            var timing = await Task.Run(() => _sync.SynchronizeAsync(job.Id, index, token), token);
            token.ThrowIfCancellationRequested(); timing?.Validate();
            if (timing is not null && (timing.JobId != job.Id || timing.Variant != index ||
                timing.SourceLyrics != MusicTimedLyrics.Normalize(job.Lyrics) || timing.ProjectId != job.ProjectId || timing.ProjectStep != job.ProjectStep))
                throw new InvalidDataException("Karaoke preparation returned an unrelated lyric snapshot.");
            _jobs.SetKaraokeReadiness(job.Id, index, timing is { Lines.Length: > 0 } ? MusicKaraokeReadiness.Ready : MusicKaraokeReadiness.Unavailable);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) {
            _jobs.SetKaraokeReadiness(job.Id, index, MusicKaraokeReadiness.Unavailable);
            _view.Status.AppendLog(_l("Music.LyricsSync.Unavailable") + " " + error.Message);
            OwnedProcessRegistry.Log("music_lyrics_sync_failed", "Music.LyricsAlignment", detail: error.GetType().Name);
        }
        finally {
            await _view.Dispatcher.InvokeAsync(() => {
                _attempted.Add(job.Id + ":" + index);
                if (!_disposed && _selection is { } track && track.JobId == job.Id && track.Variant == index) ReadSelected(track);
            });
        }
    }

    public Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Kind != MusicLyricsSynchronization.BackgroundKind) throw new InvalidDataException("Unexpected lyrics operation.");
        var jobId = state.Input.GetProperty("JobId").GetString() ?? throw new InvalidDataException("Missing lyrics job.");
        var index = state.Input.GetProperty("Variant").GetInt32();
        var job = _jobs.Load(jobId);
        if (index < 0 || index >= job.Variants.Length || !job.Variants[index].Completed) throw new InvalidDataException("Missing completed music variant.");
        return AnalyzeSelectedAsync(MusicGenerationJobs.Track(job, job.Variants[index], index), state, token);
    }

    private static string Key(MusicTrack track) => track.JobId + ":" + track.Variant;
    private void TextChanged(object? sender, EventArgs args) => Refresh();
    private void FollowPlayback() { _view.Editor.ResumePlaybackFollow(); Refresh(); }
    private void BackgroundChanged()
    {
        if (_disposed || _view.Dispatcher.HasShutdownStarted) return;
        _view.Dispatcher.BeginInvoke(() => {
            if (_disposed) return;
            Refresh();
            if (_deferred && _background?.IsRunning != true && _background?.HasPending != true && _selection is { } track)
                ReadSelected(track);
        });
    }
    private void Refresh()
    {
        if (_disposed) return;
        if (_timing is { } timing && _view.Player.CanHighlightPlayback && _view.Player.CurrentTrack is { } track &&
            MusicLyricsPlaybackBinding.TryGetRange(timing, track, _view.Editor.Lyrics,
                _view.Player.PlaybackPosition.TotalSeconds) is { } range)
            _view.Editor.SetPlaybackHighlight(range.Start, range.Length, _view.Player.IsPlaying);
        else _view.Editor.ClearPlaybackHighlight();
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel(); _reading?.Cancel(); _reading?.Dispose(); _lifetime.Dispose();
        _view.Player.TrackChanged -= TrackSelected; _view.Player.PositionChanged -= Refresh;
        _view.Player.PlaybackSeeked -= FollowPlayback;
        _view.Editor.LyricsChanged -= TextChanged; _view.Projects.LyricsContextChanged -= Refresh;
        if (_background is not null) _background.Changed -= BackgroundChanged;
        _sync.Log -= _view.Status.AppendLog; _view.Editor.ClearPlaybackHighlight();
    }
}
