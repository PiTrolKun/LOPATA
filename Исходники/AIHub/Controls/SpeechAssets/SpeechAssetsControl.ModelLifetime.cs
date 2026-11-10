using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.SpeechAssets;

internal sealed partial class SpeechAssetsControl
{
    private readonly SpeechAssetModelSession _resident;
    private readonly Func<VideoModelNeed, CancellationToken, Task<bool>>? _prepareResident;
    private CancellationTokenSource? _scenarioLifetime;
    private Task _warming = Task.CompletedTask, _leaving = Task.CompletedTask;
    private bool _hasEntered;
    internal bool ScenarioActive => _scenarioLifetime is not null;

    internal void EnterScenario()
    {
        if (ScenarioActive) return;
        _hasEntered = true;
        var lifetime = _scenarioLifetime = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        if (_session.Persistent && _session.Rows.Count > 0) _ = ResumeAsync();
        async Task ResumeAsync()
        {
            await _leaving;
            if (ReferenceEquals(_scenarioLifetime, lifetime)) await WarmSelectedModelAsync();
        }
    }

    private void ModelVisibilityChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs args)
    {
        if (!_hasEntered) return;
        if (IsVisible) EnterScenario();
        else if (_owner.IsVisible && _owner.WindowState != System.Windows.WindowState.Minimized
            && !ApplicationBackgroundOperations.PreserveHiddenWork) _ = LeaveScenarioAsync();
    }

    internal Task LeaveScenarioAsync()
    {
        if (_scenarioLifetime is not { } lifetime) return _leaving;
        _scenarioLifetime = null;
        lifetime.Cancel(); _cancel?.Cancel();
        var warm = _warming;
        return _leaving = ReleaseAsync();
        async Task ReleaseAsync()
        {
            try { await warm; await _resident.RetireAsync(); }
            catch (Exception error) { Report(error); }
            finally { lifetime.Dispose(); }
        }
    }

    internal Task WarmSelectedModelAsync()
    {
        if (_scenarioLifetime is not { } lifetime || _busy) return _warming;
        if (ApplicationBackgroundOperations.Current is { } current && (current.HasPending || current.BlocksNewWork))
        { _status.Text = T("Busy"); return Task.CompletedTask; }
        var choice = _session.Voice;
        return _warming = WarmAsync();
        async Task WarmAsync()
        {
            Busy(true); StopAudio(); _status.Text = T("WarmingModel");
            try
            {
                await _leaving;
                lifetime.Token.ThrowIfCancellationRequested();
                if (_resident.IsWarm(choice)) { _status.Text = T("ModelWarm"); return; }
                using var compute = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
                await ApplicationBackgroundOperations.RetireModelsExceptAsync(_resident);
                var need = new VideoModelNeed("speech", [choice.Id]);
                var prepared = _prepareResident is not null ? await _prepareResident(need, lifetime.Token)
                    : await PrepareAsync(need, lifetime.Token) is not null;
                if (!prepared) { _status.Text = T("Stopped"); return; }
                await _resident.WarmAsync(choice, lifetime.Token);
                _status.Text = T("ModelWarm");
            }
            catch (OperationCanceledException) { _status.Text = T("Stopped"); }
            catch (Exception error) { ComponentAuditFailures.Report(error); Report(error); }
            finally { Busy(false); }
        }
    }
}
