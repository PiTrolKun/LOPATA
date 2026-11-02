using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    internal void StopGeneration()
    {
        try
        {
            if (_cancel is not null) { _stopRequested = true; _cancel.Cancel(); }
            else if (OwnOperationState is { } own && ApplicationBackgroundOperations.Current is { } controller
                && !controller.HasLiveOperation(own.Id))
            {
                controller.DiscardPending(own.Id);
                _project.PendingAttemptId = "";
                _store.Save(_project);
                _stage = _l("SongClip.Canceled"); _status.Text = _stage;
                RefreshActions();
            }
        }
        catch (Exception error) { _status.Text = FormatEngineProgress(error.Message); }
    }

    // Reject invalid input before creating an immutable attempt or entering the model gate.
    internal bool TryValidateStartAttempt(SongClipAttempt attempt)
    {
        if(SongClipVariantCatalog.IsRetiredModel(attempt.Model)) {
            _stage=_l(SongClipVariantCatalog.RetiredMessageKey);_status.Text=_stage;return false;
        }
        try { SongClipReferences.ValidateNewAttempt(attempt); }
        catch (InvalidDataException error) {
            _stage = FormatEngineProgress(error.Message); _status.Text = _stage;
            return false;
        }
        if (attempt.Model == InfiniteTalkRunner.ModelId) {
            try {
                if (SongClipReferences.For(attempt) is not [{ Kind: "performer" }])
                    throw new InvalidDataException("SongClip.InfiniteReferenceRequired");
                InfiniteTalkRunner.ValidateSettings(attempt); return true;
            }
            catch (InvalidDataException error) { _stage = FormatEngineProgress(error.Message); _status.Text = _stage; return false; }
        }
        if (attempt.Model == GeekatplayRunner.ModelId) {
            try { GeekatplayRunner.ValidateSettings(attempt); return true; }
            catch (InvalidDataException error) { _stage = FormatEngineProgress(error.Message); _status.Text = _stage; return false; }
        }
        if (attempt.Model != H3MusicRunner.ModelId) return true;
        if (attempt.PlanningVersion == 0 && string.IsNullOrWhiteSpace(attempt.Settings.Prompt))
        {
            _stage = _l("SongClip.PromptRequired");
            _status.Text = _stage;
            _prompt.Focus();
            return false;
        }
        try { H3MusicRunner.ValidateSettings(attempt); return true; }
        catch (InvalidDataException error)
        {
            _stage = FormatEngineProgress(error.Message);
            _status.Text = _stage;
            return false;
        }
    }

    // Older builds put preflight failures in Waiting. Recover only a positively
    // identified invalid input with no model work; keep the old snapshot as history.
    internal bool RecoverInvalidWaitingAttempt()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller is null || OwnOperationState is not
            { Kind: H3MusicRunner.Kind, Phase: BackgroundOperationPhase.Waiting,
              RequiresDecision: true, UserPaused: false } state || state.Project != _project.Id)
            return false;
        if (controller.HasLiveOperation(state.Id)) return false;
        SongClipAttempt? attempt;
        try { attempt = state.Input.Deserialize<SongClipAttempt>(); }
        catch (JsonException) { return false; }
        if (attempt is null || attempt.Model != H3MusicRunner.ModelId || attempt.ProjectId != _project.Id)
            return false;
        if (!Guid.TryParseExact(attempt.Id, "N", out _)) return false;
        try { H3MusicRunner.ValidateSettings(attempt); return false; }
        catch (InvalidDataException) { }
        var folder = _store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id);
        if (!Directory.Exists(folder)
            || Directory.EnumerateFileSystemEntries(folder).Any(path => Path.GetFileName(path) != "snapshot.json"))
            return false;
        try
        {
            using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "snapshot.json")));
            if (!JsonElement.DeepEquals(snapshot.RootElement, state.Input)) return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
        if (!controller.FailWaiting(state.Id, "song_clip_invalid_settings")) return false;
        if (_project.PendingAttemptId == attempt.Id)
        {
            _project.PendingAttemptId = "";
            _store.Save(_project);
        }
        _stage = _l("SongClip.InvalidAttemptRecovery");
        _status.Text = _stage;
        RefreshActions();
        return true;
    }
}
