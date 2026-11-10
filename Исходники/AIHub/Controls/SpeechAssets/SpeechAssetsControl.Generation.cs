using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.SpeechAssets;

internal sealed partial class SpeechAssetsControl
{
    private readonly ManagedModelLibraryStore _library = new();
    private string ModelsRoot => new StorageSettingsStore().LoadOrCreate().Models.Locations
        .Select(location => location.Path).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
        ?? throw new InvalidOperationException("VideoEditor.StorageRequired");

    private Task<VideoPreparedModels?> PrepareAsync(VideoModelNeed need, CancellationToken token, Window? owner = null)
    {
        var card = _library.Load(VideoNarratorCatalog.ArtifactId(need.Voices[0]));
        var root = string.IsNullOrWhiteSpace(card?.ModelsRoot) ? ModelsRoot : card.ModelsRoot;
        using var preparation = new VideoModelPreparation(_library);
        preparation.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
        var window = new VideoPreparationWindow(owner ?? _owner, _localize, VideoNarratorCatalog.Get(need.Voices[0]).Name,
            (download, progress, ct) => preparation.PrepareAsync(root, need, download, progress, ct), token);
        return Task.FromResult<VideoPreparedModels?>(window.ShowDialog() == true ? new(root) : null);
    }
    private void ChooseModel()
    {
        StopAudio();
        VideoVoiceWindow? window = null;
        async Task<VideoPreparedModels?> Prepare(VideoModelNeed need, CancellationToken token) => await PrepareAsync(need, token, window);
        var extension = new VideoVoiceWindowExtension(T("Model"), "", language => T(language == "ru" ? "TestRu" : "TestEn"),
            async (choice, text, token) =>
            {
                if (ApplicationBackgroundOperations.Current?.HasPending == true) throw new InvalidOperationException("SpeechAssets.Busy");
                await ApplicationBackgroundOperations.RetireModelsExceptAsync(_resident);
                if (await Prepare(new("speech", [choice.Id]), token) is null) throw new OperationCanceledException(token);
                var sample = new SpeechAssetSession { SourceName = "preview", Voice = choice,
                    Rows = [new() { Text = text, SourceText = text, FileName = "preview" }] };
                _store.Save(sample);
                await new SpeechAssetWork(_store, resident: _resident).RunAsync(new(sample.Id, choice, [new(sample.Rows[0].Id, Guid.NewGuid().ToString("N"), text)]), T("Model"), null, token);
                var result = _store.Load(sample.Id); return _store.FileFor(sample.Id, result.Rows[0].Takes[0].File);
            }, () => { Directory.CreateDirectory(_store.Root);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { _store.Root }, UseShellExecute = true }); },
            ApplicationBackgroundOperations.RetireModelsAsync, _session.Profiles, ShowToggle: false, DefaultModelId: "qwen3-17b",
            ModelSize: id => ReaderAiModelCatalog.Size((_library.Load(VideoNarratorCatalog.ArtifactId(id)) ?? VideoNarratorCatalog.Card(ModelsRoot, id)).TotalBytes),
            FilesPresent: id => ReaderAiModelCatalog.Downloaded(_library.Load(VideoNarratorCatalog.ArtifactId(id)) ?? VideoNarratorCatalog.Card(ModelsRoot, id)));
        window = new VideoVoiceWindow(_owner, _localize, _session.Voice, false, Prepare, _library, extension);
        if (window.ShowDialog() == true && window.Choice is { } choice)
        {
            _session.Voice = choice; _session.Profiles = new(window.Profiles); TrySave(); Refresh();
            _ = WarmSelectedModelAsync();
        }
    }
    internal async Task GenerateAsync(string? rowId)
    {
        if (_busy) return;
        try
        {
            _saveTimer.Stop(); _store.Save(_session);
            var rows = _session.Rows.Where(r => r.Included && (rowId is null
                ? !r.Confirmed && r.Takes.All(t => t.Text != r.Text) : r.Id == rowId)).ToArray();
            if (rows.Length == 0) { _status.Text = T("NothingToGenerate"); return; }
            if (rows.Any(r => string.IsNullOrWhiteSpace(r.Text))) throw new InvalidDataException("SpeechAssets.NoText");
            if (ApplicationBackgroundOperations.Current?.HasPending == true) throw new InvalidOperationException("SpeechAssets.Busy");
            var previous = _session.PendingJob;
            var job = new SpeechAssetJob(_session.Id, _session.Voice, rows.Select(r =>
                previous?.Voice == _session.Voice && previous.Items.FirstOrDefault(i => i.RowId == r.Id && i.Text == r.Text) is { } unfinished
                    ? unfinished : new SpeechAssetJobItem(r.Id, Guid.NewGuid().ToString("N"), r.Text)).ToArray(), _store.Root,
                    ContinueOnError: rowId is null, RunId: previous?.Voice == _session.Voice
                        && rows.All(r => previous.Items.Any(i => i.RowId == r.Id && i.Text == r.Text))
                            ? previous.RunId ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"));
            await RunJobAsync(job, null, CancellationToken.None);
        }
        catch (Exception error) { _status.Text = error is OperationCanceledException ? T("Stopped") : Error(error); }
    }
    internal async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var job = state.Input.Deserialize<SpeechAssetJob>() ?? throw new InvalidDataException("SpeechAssets.Invalid");
        if (job.StoreRoot is not null) _store = new SpeechAssetStore(job.StoreRoot);
        MergeSession(await Task.Run(() => _store.Load(job.SessionId), token)); Refresh(); await RunJobAsync(job, state, token);
    }
    private async Task RunJobAsync(SpeechAssetJob job, BackgroundOperationState? state, CancellationToken token)
    {
        _saveTimer.Stop(); Busy(true); StopAudio();
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _changedRows.Clear(); _updateTimer.Start();
        try
        {
            MergeSession(await Task.Run(() => _store.BeginRun(job), _cancel.Token)); RememberProject();
            await ApplicationBackgroundOperations.RetireModelsExceptAsync(_resident);
            var need = new VideoModelNeed("speech", [job.Voice.Id]);
            if (_prepareResident is not null ? !await _prepareResident(need, _cancel.Token)
                : await PrepareAsync(need, _cancel.Token) is null) return;
            _status.Text = T("Generating");
            var title = T("Title"); var generating = T("GeneratingLine"); var retrying = T("RetryingLine");
            await new SpeechAssetWork(_store, resident: _resident).RunAsync(job, title, QueueRowUpdate, _cancel.Token, state,
                (current, total) => QueueStatus(string.Format(generating, current, total)),
                (line, retry) => QueueStatus(string.Format(retrying, line, retry)));
            await FinishUpdatesAsync();
            var failed = (await Task.Run(() => _store.Load(job.SessionId))).Rows.Count(r => r.Included && !string.IsNullOrEmpty(r.LastError));
            _status.Text = failed == 0 ? T("ReviewRequired") : string.Format(T("QueueFinishedWithErrors"), failed);
        }
        finally
        {
            _cancel.Dispose(); _cancel = null;
            await FinishUpdatesAsync();
            var latest = await Task.Run(() =>
            {
                var saved = _store.Load(job.SessionId);
                if (saved.Runs.FirstOrDefault(r => r.Id == job.RunId)?.State == "Pending")
                { _store.RunState(job, "Interrupted"); saved = _store.Load(job.SessionId); }
                return saved;
            });
            MergeSession(latest); Busy(false);
        }
    }
}
