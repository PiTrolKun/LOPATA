using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private readonly MusicPoetryPreferences _preferences;
    private readonly Func<IReadOnlyList<MusicPoetryModelChoice>> _modelChoices;
    private CancellationTokenSource? _modelEntry;
    private Task _modelEntryTask = Task.CompletedTask;
    private int _modelEntryVersion;
    public bool IsModelDownloadActive => _modelSelector?.IsDownloading == true || _modelSelector?.IsRemoving == true;

    private void CancelModelEntry() { ++_modelEntryVersion; _modelEntry?.Cancel(); _modelEntry?.Dispose(); _modelEntry = null; }

    private void BeginModelEntry()
    {
        if (_viewClosed || !_view.IsLoaded || !_view.IsVisible) return;
        CancelModelEntry();
        _modelEntry = new();
        _modelEntryTask = EnterModelsAsync(_modelEntry.Token);
    }

    private async Task EnterModelsAsync(CancellationToken token)
    {
        var delay = Task.Delay(TimeSpan.FromSeconds(1), token);
        try
        {
            var choices = await Task.Run(_modelChoices, token);
            if (token.IsCancellationRequested || _viewClosed || !_view.IsVisible || IsWorking) return;
            _viewInitialized = true;
            string? favorite = null;
            try { favorite = _preferences.Load().FavoriteId; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { _status.Text = L("Selection.PreferencesError") + " " + ex.Message; }
            var preferred = choices.FirstOrDefault(choice => choice.Candidate.Id == favorite)?.Model;
            if (preferred is not null)
            {
                if (_model is null && string.IsNullOrEmpty(_session.ModelPath)) { _model = preferred; ModelCaption(); Recount(); Persist(); }
                else SelectModel(preferred);
                Availability(); return;
            }
            if (_model is null)
            {
                _model = string.IsNullOrEmpty(_session.ModelPath) ? choices.FirstOrDefault(choice => choice.Candidate.Id == "core")?.Model
                    : choices.FirstOrDefault(choice => choice.Model?.Path.Equals(_session.ModelPath, StringComparison.OrdinalIgnoreCase) == true)?.Model;
                ModelCaption(); Recount();
                if (_model is null) _status.Text = L("ModelMissing");
            }
            Availability();
            await delay;
            if (!token.IsCancellationRequested && !_viewClosed && _view.IsLoaded && _view.IsVisible && !IsWorking)
                OpenModelPicker(choices);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && !_viewClosed) _status.Text = L("Error") + " " + ex.Message; }
    }

    private void OpenModelPicker(IReadOnlyList<MusicPoetryModelChoice> choices)
    {
        if (IsWorking || _viewClosed || !_view.IsLoaded || !_view.IsVisible) return;
        if (_modelSelector is not null) { _modelSelector.Activate(); return; }
        var window = new MusicPoetryModelWindow(choices, _model?.Path, _l, model => {
            if (IsWorking) return;
            if (!MusicPoetryModelCatalog.IsCompatible(model)) { _status.Text = L("ModelUnsupported"); return; }
            SelectModel(model); _modelSelector?.Close();
        }, _preferences, async (candidate, progress, token) => {
            using var lease = _background?.BeginAuxiliary();
            return await MusicPoetryProfileInstallation.DownloadAsync(candidate, _storage, progress, token);
        }, candidate => {
            if (IsWorking || _background?.IsRunning == true || _background?.HasPending == true)
                throw new InvalidOperationException("Music.Poetry.Selection.RemoveBlocked");
            using var lease = _background?.BeginAuxiliary();
            try {
                return new MusicPoetryProfileRemoval(new ManagedModelLibraryStore(), new DelegateModelUsageGuard(_ => IsWorking))
                    .Remove(candidate, _storage);
            }
            finally {
                if (_model is not null && !System.IO.File.Exists(_model.Path)) {
                    _model = null; ModelCaption(); Recount(); _status.Text = L("ModelMissing"); Persist();
                }
                Availability();
            }
        }, candidate => MusicPoetryProfileRemoval.HasFiles(candidate, _storage)) { Owner = DialogOwner, Resources = Resources };
        _modelSelector = window; window.Closed += (_, _) => _modelSelector = null;
        window.Show();
    }
}
