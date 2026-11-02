using System.Text.Json;
using System.Windows.Automation;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    // The scheduler can select another parked job. Resolve this project's immutable
    // attempt rather than treating the tray's last selected operation as its owner.
    private BackgroundOperationState? OwnOperationState
    {
        get
        {
            if (_project.PendingAttemptId.Length == 0 || ApplicationBackgroundOperations.Current is not { } controller)
                return null;
            foreach (var kind in new[] { SongClipAttempt.Kind, H3MusicRunner.Kind, InfiniteTalkRunner.Kind })
            {
                foreach (var state in controller.FindOperations(kind, _project.Id))
                {
                    try
                    {
                        var attempt = state.Input.Deserialize<SongClipAttempt>();
                        if (attempt?.Id == _project.PendingAttemptId && attempt.ProjectId == _project.Id)
                            return state;
                    }
                    catch (JsonException) { }
                }
            }
            return null;
        }
    }

    private bool CanResume(BackgroundOperationState state) =>
        state.Phase == BackgroundOperationPhase.Paused
        || state.Phase == BackgroundOperationPhase.Waiting
            && ApplicationBackgroundOperations.Current?.HasLiveOperation(state.Id) != true;

    private void RefreshGenerationActions()
    {
        var own = OwnOperationState;
        var resume = own is not null && CanResume(own);
        _model.IsEnabled = !_busy && own is null;
        // A parked operation resumes its immutable input, rather than the draft.
        // Keep its visible H3 contract fixed until it is finished or stopped.
        foreach (var choice in new[] { _h3Mode, _motionSize, _motionSteps, _montageClipSeconds, _geekSize, _geekSteps, _geekCardSteps, _geekSeconds, _geekStrength, _infiniteSize, _infiniteSteps, _infiniteAudioCfg, _infiniteVramReserve })
            choice.IsEnabled = !_busy && own is null;
        if (_generate is not null)
        {
            _generate.IsEnabled = SongClipVariantCatalog.CanGenerateModel(_project.Model)
                && _project.Song.Length > 0 && (resume || !_busy && own is null);
            var text = _l(resume ? "SongClip.Resume" : "SongClip.Start");
            if (_generate.Content is System.Windows.Controls.TextBlock label) label.Text = text;
            AutomationProperties.SetName(_generate, text);
        }
        if (_pause is not null)
        {
            _pause.IsEnabled = !SongClipVariantCatalog.IsRetiredModel(_project.Model) && own is not null && own.Phase != BackgroundOperationPhase.Pausing;
            _pause.Content = resume ? "▶" : "Ⅱ";
            _pause.ToolTip = _l(resume ? "SongClip.Resume" : "SongClip.PauseAction");
            AutomationProperties.SetName(_pause, (string)_pause.ToolTip);
        }
        if (_stop is not null) _stop.IsEnabled = own is not null || _cancel is not null;
    }

    private async Task PauseResume()
    {
        try
        {
            if(SongClipVariantCatalog.IsRetiredModel(_project.Model)){_status.Text=_l(SongClipVariantCatalog.RetiredMessageKey);return;}
            if (OwnOperationState is not { } own || ApplicationBackgroundOperations.Current is not { } controller) return;
            if (CanResume(own))
            {
                await controller.RetryPausedRetirementAsync(ApplicationBackgroundOperations.RetireModelsAsync);
                await controller.ResumeAsync(own.Id, ApplicationBackgroundOperations.ExitToken);
            }
            else if (controller.State?.Id == own.Id) await controller.PauseAsync();
        }
        catch (Exception error) { _status.Text = FormatEngineProgress(error.Message); }
    }
}
