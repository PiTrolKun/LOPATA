using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicCoverController
{
    private MusicCoverPreparationWindow? _preparation;
    private string? _checkedPreparationKey;
    private int _downloadConnections;
    private string? _preparationRoot;
    private void CheckPreparationWhenEnabled()
    {
        if (_disposed || _storage is null || !_view.IsLoaded || !_view.IsVisible) return;
        var entry = Current;
        if (!entry.IsEnabled) { _checkedPreparationKey = null; return; }
        var root = ModelsRoot();
        var key = root + "|" + MusicCoverStore.Key(entry.Artist, entry.Title);
        if (_checkedPreparationKey == key) return;
        _checkedPreparationKey = key;
        _view.Dispatcher.BeginInvoke(CheckEnabledComponentsAsync);
    }
    private async void CheckEnabledComponentsAsync()
    {
        try { if (!_disposed && Current.IsEnabled) await EnsureComponentsAsync(); }
        catch (Exception error) { if (!_disposed) Report(error); }
    }
    private string ModelsRoot() => _storage?.Models.Locations.Select(l => l.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
    private async Task<bool> EnsureComponentsAsync(string? root = null)
    {
        if (_disposed || _storage is null) return false;
        root ??= ModelsRoot();
        if (_preparation is not null)
        {
            var existing = _preparation; var existingRoot = _preparationRoot; existing.Activate();
            if (!await existing.Completion) return false;
            return string.Equals(root, existingRoot, StringComparison.OrdinalIgnoreCase) || await EnsureComponentsAsync(root);
        }
        using (var installation = new ImageGenerationInstallation())
            if (installation.IsReady(root, "z-image")) return true;
        var control = new ImageGenerationControl();
        var storage = new StorageSettings { Models = new() { Locations = [new() { Path = root }] }, Results = _storage.Results };
        control.Configure(_l, storage, _downloadConnections);
        var window = new MusicCoverPreparationWindow(control, _l, _window ?? Window.GetWindow(_view));
        window.Closed += (_, _) => { if (ReferenceEquals(_preparation, window)) _preparation = null; };
        _preparation = window; _preparationRoot = root; Error = null; Refresh();
        try { window.Show(); return await window.Completion; }
        finally { if (ReferenceEquals(_preparation, window)) _preparation = null; if (!_disposed) Refresh(); }
    }
}
