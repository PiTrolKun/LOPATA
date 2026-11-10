using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using ComboBox = System.Windows.Controls.ComboBox;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls.SpeechAssets;

internal sealed partial class SpeechAssetsControl
{
    private readonly TextBlock _projectLabel = new();
    private readonly ComboBox _format = new() { MinWidth = 100, Margin = new(4) };
    private readonly ComboBox _bitrate = new() { MinWidth = 100, Margin = new(4) };
    private bool _updatingSettings, _confirming;
    internal bool IsWorking => _busy;
    internal SpeechAssetSession CurrentProject => _session;
    internal string ProjectText(string key) => _localize("Music.Projects." + key);
    internal string ProjectName(SpeechAssetSession session) => session.Name.Length > 0 ? session.Name
        : string.Format(ProjectText("DefaultName"), session.Number, session.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));
    internal IReadOnlyList<SpeechAssetSession> Projects() => _store.ListProjects();
    private void ShowProjects() => new SpeechAssetProjectsWindow(this) { Owner = _owner }.ShowDialog();

    internal void FixProject()
    {
        if (_busy) return;
        _saveTimer.Stop(); _store.FixProject(_session); RememberProject(); Refresh();
    }
    internal void RenameProject(string name)
    {
        if (_busy) return;
        _saveTimer.Stop(); _store.RenameProject(_session, name); Refresh();
    }
    internal void NewProject()
    {
        if (_busy) return;
        _saveTimer.Stop(); if (!TrySave()) return;
        StopAudio(); _session = _store.CreateDraft(_session); TrySave(); Refresh();
    }
    private void AddExportSettings(Panel top)
    {
        var panel = new WrapPanel(); top.Children.Add(panel);
        var label = new TextBlock { Text = T("OutputFormat"), Margin = new(5), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); panel.Children.Add(label);
        _format.ItemsSource = Enum.GetValues<MusicAudioFormat>(); _bitrate.ItemsSource = SpeechAssetEncoder.Bitrates;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_format, "SpeechAssets.Format");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_bitrate, "SpeechAssets.Bitrate");
        panel.Children.Add(_format); panel.Children.Add(_bitrate);
        var units = new TextBlock { Text = "kbps", Margin = new(5), VerticalAlignment = VerticalAlignment.Center };
        units.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); panel.Children.Add(units);
        _format.SelectionChanged += (_, _) =>
        {
            if (_updatingSettings || _busy || _format.SelectedItem is not MusicAudioFormat format) return;
            _session.ExportFormat = format; _bitrate.IsEnabled = MusicOutputSettings.Lossy(format); Changed();
        };
        _bitrate.SelectionChanged += (_, _) =>
        {
            if (!_updatingSettings && !_busy && _bitrate.SelectedItem is int bitrate) { _session.ExportBitrate = bitrate; Changed(); }
        };
    }
    private void RefreshProject()
    {
        _updatingSettings = true;
        _format.SelectedItem = _session.ExportFormat; _bitrate.SelectedItem = _session.ExportBitrate;
        _bitrate.IsEnabled = !_busy && MusicOutputSettings.Lossy(_session.ExportFormat); _updatingSettings = false;
        _projectLabel.Text = ProjectName(_session) + " — " + ProjectText(_session.Persistent ? "Saved" : "Temporary");
    }
    private void RememberProject()
    {
        Directory.CreateDirectory(_store.Root);
        SpeechAssetStore.WriteAtomic(Path.Combine(_store.Root, "last.txt"), _session.Id);
    }
    private async void ProjectKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!IsVisible || e.Key != Key.F1) return;
        e.Handled = true; await ConfirmAllAsync();
    }
    internal async Task ConfirmAllAsync()
    {
        if (_confirming) return;
        _confirming = true; var wasBusy = _busy;
        try
        {
            _saveTimer.Stop(); if (!TrySave()) return;
            if (!wasBusy) Busy(true);
            var count = await Task.Run(() => _store.ConfirmAll(_session.Id));
            MergeSession(await Task.Run(() => _store.Load(_session.Id))); Refresh(); _status.Text = string.Format(T("BulkConfirmed"), count);
        }
        catch (Exception error) { Report(error); }
        finally { _confirming = false; if (!wasBusy) Busy(false); }
    }
}
