using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class ModelExpertWindow
{
    private readonly ComboBox _presetList = new() { MinWidth = 150, MaxWidth = 280, DisplayMemberPath = "Name" };
    private readonly TextBox _presetName = new() { MinWidth = 130, MaxWidth = 240, MaxLength = 100, Padding = new(4) };
    private readonly List<ModelExpertPreset> _presets = [];
    private ModelExpertPreset? _selected;
    private bool _reloading;
    private StackPanel BuildPresets()
    {
        var panel = new StackPanel();
        panel.Children.Add(MusicWishUi.Text(L("Presets"), true));
        var choices = new WrapPanel { Margin = new(0, 3, 0, 4) };
        _presetList.Margin = new(0, 0, 8, 0); _presetName.ToolTip = L("Name");
        choices.Children.Add(_presetList); choices.Children.Add(_presetName); panel.Children.Add(choices);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_presetList, "Music.Expert.Presets");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_presetName, "Music.Expert.PresetName");
        var actions = new WrapPanel();
        foreach (var (key, action) in new (string, Action)[] {
            ("SaveNew", () => Guard(SaveNew)), ("Update", () => Guard(UpdatePreset)),
            ("Rename", () => Guard(RenamePreset)), ("Delete", () => Guard(DeletePreset)),
            ("Import", () => Guard(ImportPresets)), ("Export", () => Guard(ExportPreset)) })
            actions.Children.Add(MusicWishUi.Button(L(key), "Music.Expert." + key, action));
        panel.Children.Add(actions); panel.Children.Add(_modified);
        panel.Children.Add(MusicWishUi.Text(L("PresetHint")));
        _presets.AddRange(_store.Load());
        var state = MusicAceCatalog.IsAce(_draft.Variation) ? null : MusicTuningProfile.State(_draft);
        var name = _store.Collection == "Expert" ? state?.ExpertPreset : state?.SimplePreset?.Replace("User:", "", StringComparison.Ordinal);
        _selected = _presets.FirstOrDefault(p => p.Name == name) ?? _presets.FirstOrDefault(p => p.Settings.SameAs(_draft));
        _presetName.Text = _selected?.Name ?? "";
        Reload();
        _presetList.SelectionChanged += (_, _) => {
            if (_reloading) return;
            _selected = _presetList.SelectedIndex <= 0 ? null : _presetList.SelectedItem as ModelExpertPreset;
            SelectDraft(_selected?.Settings.Snapshot() ?? MusicModelVariants.Defaults(_draft.Variation)); _presetName.Text = _selected?.Name ?? "";
            Render(); Changed();
        };
        return panel;
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException) { ShowError(error); }
    }
    private string PresetName() => _presetName.Text.Trim();
    private void SelectDraft(MusicExpertSettings settings)
    {
        if (MusicAceCatalog.IsAce(settings.Variation)) { _draft = settings.Snapshot(); return; }
        var state = MusicTuningProfile.State(settings);
        _draft = settings with { Tuning = _store.Collection == "Expert" ? state with {
            ExpertPreset = _selected?.Name, ExpertModified = false, SimpleModified = true } : state with {
            SimplePreset = _selected is null ? "Ordinary" : "User:" + _selected.Name, SimpleModified = false, ExpertModified = true } };
    }
    private void Reload()
    {
        _reloading = true; _presetList.Items.Clear();
        _presetList.Items.Add(new ModelExpertPreset(L("Recommended"), MusicModelVariants.Defaults(_draft.Variation)));
        foreach (var p in _presets) _presetList.Items.Add(p);
        _presetList.SelectedIndex = _selected is null ? 0 : _presets.IndexOf(_selected) + 1;
        _reloading = false;
    }
    private void Commit(List<ModelExpertPreset> next, ModelExpertPreset? selection)
    {
        _store.Save(next); _presets.Clear(); _presets.AddRange(next); _selected = selection;
        if (MusicAceCatalog.IsAce(_draft.Variation)) { _presetName.Text = selection?.Name ?? ""; Reload(); Changed(); return; }
        var state = MusicTuningProfile.State(_draft);
        _draft = _draft with { Tuning = _store.Collection == "Expert" ? state with {
            ExpertPreset = selection?.Name, ExpertModified = selection is not null && !selection.Settings.SameAs(_draft)
        } : state with { SimplePreset = selection is null ? null : "User:" + selection.Name,
            SimpleModified = selection is not null && !selection.Settings.SameAs(_draft) } };
        _presetName.Text = selection?.Name ?? ""; Reload(); Changed();
    }
    private void SaveNew()
    {
        _draft.Validate(); var preset = ModelExpertPresets.Create(PresetName(), _draft, _store.Collection);
        ModelExpertPresets.Validate(preset);
        Commit([.. _presets, preset], preset);
    }
    private void UpdatePreset()
    {
        if (_selected is null) { MessageBox.Show(this, L("Immutable")); return; }
        _draft.Validate(); var updated = _selected with { Settings = _draft.Snapshot(), SchemaVersion = ModelExpertPresets.SchemaFor(_draft.Variation) };
        Commit(_presets.Select(p => p == _selected ? updated : p).ToList(), updated);
    }
    private void RenamePreset()
    {
        if (_selected is null) { MessageBox.Show(this, L("Immutable")); return; }
        var renamed = _selected with { Name = PresetName() }; ModelExpertPresets.Validate(renamed);
        Commit(_presets.Select(p => p == _selected ? renamed : p).ToList(), renamed);
    }
    private void DeletePreset()
    {
        if (_selected is null) { MessageBox.Show(this, L("Immutable")); return; }
        if (MessageBox.Show(this, L("DeleteConfirm") + "\n" + _selected.Name, L("Title"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Commit(_presets.Where(p => p != _selected).ToList(), null);
    }
    private void ImportPresets()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LOPATA preset (*.json)|*.json", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try {
                var preset = ModelExpertPresets.Import(file);
                if (preset.Settings.Variation != _draft.Variation) {
                    if (MusicAceCatalog.IsAce(preset.Settings.Variation) || MusicAceCatalog.IsAce(_draft.Variation))
                        throw new InvalidDataException(_l("Music.Ace.NoTransfer"));
                    if (MessageBox.Show(this, L("TransferConfirm"), L("Import"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) continue;
                    preset = preset with { SchemaVersion = 3, Settings = MusicModelVariants.Transfer(preset.Settings, _draft.Variation), Recipe = null };
                }
                _store.CheckCollection(preset); var next = _presets.ToList();
                var conflict = next.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
                if (conflict >= 0) {
                    var answer = MessageBox.Show(this, preset.Name + "\n" + L("Conflict"), L("Import"), MessageBoxButton.YesNoCancel);
                    if (answer == MessageBoxResult.Cancel) continue;
                    if (answer == MessageBoxResult.Yes) next.RemoveAt(conflict);
                    else {
                        var stem = preset.Name.Length > 85 ? preset.Name[..85] : preset.Name;
                        var i = 2; string copy;
                        do { copy = stem + " (" + i++ + ")"; } while (next.Any(p => string.Equals(p.Name, copy, StringComparison.OrdinalIgnoreCase)));
                        preset = preset with { Name = copy };
                    }
                }
                next.Add(preset); Commit(next, preset); SelectDraft(preset.Settings.Snapshot()); Render(); Changed();
            } catch (Exception e) when (e is IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException) {
                MessageBox.Show(this, Path.GetFileName(file) + "\n" + L("Invalid") + "\n" + e.Message, L("Import"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
    private void ExportPreset()
    {
        var preset = _selected ?? ModelExpertPresets.Create(L("Recommended"), _draft, _store.Collection);
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "LOPATA preset (*.json)|*.json", DefaultExt = ".json",
            FileName = ModelExpertPresets.ExportName(preset.Name, DateTime.Now, preset.Settings.Variation) };
        if (dialog.ShowDialog(this) == true) ModelExpertPresets.Export(dialog.FileName, preset);
    }
}
