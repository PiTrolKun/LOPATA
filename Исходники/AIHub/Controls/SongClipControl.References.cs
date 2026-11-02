using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private readonly ComboBox _referenceChoice = new() { Margin = new(3), MinHeight = 32 };
    private readonly ComboBox _referenceKind = new() { Margin = new(3), MinHeight = 32 };
    private ExampleTextInput? _referenceRoleInput;
    private Grid? _promptAndReferences;
    private FrameworkElement? _referenceMeaningPanel;
    private int _referenceIndex;
    private bool _loadingReference;

    private FrameworkElement BuildReferenceMeaning()
    {
        var panel = new DockPanel { Margin = new(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(panel, "SongClip.ReferenceMeaningPanel");
        var title = Label("ReferenceExplanation"); title.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var type = new DockPanel();
        var question = Label("ReferenceKindQuestion"); question.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(question, Dock.Left); type.Children.Add(question);
        foreach (var kind in new[] { "", "performer", "world", "object" })
            _referenceKind.Items.Add(new ComboBoxItem { Tag = kind, Content = ReferenceKindName(kind) });
        AutomationProperties.SetAutomationId(_referenceKind, "SongClip.ReferenceKind");
        AutomationProperties.SetName(_referenceKind, _l("SongClip.ReferenceKindQuestion"));
        type.Children.Add(_referenceKind); DockPanel.SetDock(type, Dock.Top); panel.Children.Add(type);
        var role = Label("ReferenceRoleQuestion"); DockPanel.SetDock(role, Dock.Top); panel.Children.Add(role);
        _referenceRoleInput = new(_l("SongClip.ReferenceRoleQuestion"), _l("SongClip.ReferenceRoleExample"),
            "SongClip.ReferenceRole", multiline: true);
        _referenceRoleInput.Input.Height = double.NaN;
        _referenceRoleInput.Input.MaxLength = SongClipReferences.MaximumDescription;
        _referenceRoleInput.Input.Loaded += (sender, _) => LiterarySpellChecking.Enable((TextBox)sender, _l("VideoPrep.SpellingLanguage"));
        panel.Children.Add(_referenceRoleInput);
        AutomationProperties.SetAutomationId(_referenceChoice, "SongClip.ReferenceSelection");
        AutomationProperties.SetName(_referenceChoice, _l("SongClip.ReferenceSelection"));
        _referenceChoice.SelectionChanged += (_, _) => {
            if (_loadingReference || _referenceChoice.SelectedIndex < 0) return;
            _referenceIndex = _referenceChoice.SelectedIndex; RefreshReference();
        };
        _referenceKind.SelectionChanged += (_, _) => CaptureReferenceMeaning();
        _referenceRoleInput.Input.TextChanged += (_, _) => CaptureReferenceMeaning();
        return panel;
    }

    private string ReferenceKindName(string kind) => _l("SongClip." + (kind switch {
        "performer" => "ReferencePerformer", "world" => "ReferenceWorld", "object" => "ReferenceObject",
        _ => "ReferenceUnspecified"
    }));

    private void LoadReferenceMeaning()
    {
        var references = SongClipReferences.For(_project);
        var selected = references.Length == 0 ? null : references[_referenceIndex];
        _referenceKind.SelectedItem = _referenceKind.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == (selected?.Kind ?? ""));
        if (_referenceRoleInput is not null) _referenceRoleInput.Input.Text = selected?.Description ?? "";
    }

    private void CaptureReferenceMeaning()
    {
        if (_loading || _loadingReference || _busy || _referenceRoleInput is null) return;
        var references = SongClipReferences.For(_project).ToArray();
        if (_referenceIndex < 0 || _referenceIndex >= references.Length) return;
        var kind = (_referenceKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        references[_referenceIndex] = references[_referenceIndex] with { Kind = kind, Description = _referenceRoleInput.Input.Text };
        _project.References = references; _project.Reference = references[0].File;
        _reference.Text = $"{_referenceIndex + 1}. {ReferenceKindName(kind)}";
        if (_referenceChoice.Items.Count > _referenceIndex && _referenceChoice.Items[_referenceIndex] is ComboBoxItem item)
            item.Content = _reference.Text;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void RefreshReferenceSplit()
    {
        if (_promptAndReferences is null || _referenceMeaningPanel is null) return;
        var split = SongClipReferences.For(_project).Length != 0;
        _promptAndReferences.ColumnDefinitions[1].Width = split ? new(1, GridUnitType.Star) : new(0);
        _referenceMeaningPanel.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetReferenceInputs(bool enabled)
    {
        _referenceChoice.IsEnabled = enabled; _referenceKind.IsEnabled = enabled;
        if (_referenceRoleInput is not null) _referenceRoleInput.Input.IsEnabled = enabled;
    }

    private void RemoveSelectedReference()
    {
        if (_busy || !Checkpoint()) return;
        var references = SongClipReferences.For(_project);
        if (references.Length == 0) return;
        _project.References = references.Where((_, index) => index != _referenceIndex).ToArray();
        _project.Reference = _project.References.FirstOrDefault()?.File ?? "";
        RefreshReference(); Checkpoint();
    }

    private async Task ImportReference()
    {
        if (_busy) return;
        var files = new Microsoft.Win32.OpenFileDialog { Filter = "Image|*.png;*.jpg;*.jpeg;*.webp", Multiselect = true };
        if (files.ShowDialog(Window.GetWindow(this)) != true || !Checkpoint()) return;
        _busy = true; SetInputs(false); RefreshActions();
        try {
            foreach (var file in files.FileNames) {
                if (SongClipReferences.For(_project).Length >= SongClipReferences.MaximumCount) {
                    _status.Text = _l("SongClip.ReferenceLimit"); break;
                }
                var question = new SongClipReferenceWindow(Window.GetWindow(this), _l, file);
                if (question.ShowDialog() != true) break;
                await AddReferenceAsync(file, question.ChosenKind, question.Description, ApplicationBackgroundOperations.ExitToken);
            }
        }
        catch (Exception error) { _status.Text = FormatEngineProgress(error.Message); }
        finally { _busy = false; SetInputs(true); LoadFields(); }
    }

    internal async Task AddReferenceAsync(string file, string kind, string description, CancellationToken token)
    {
        if (kind is not ("performer" or "world" or "object") || string.IsNullOrWhiteSpace(description)
            || description.Length > SongClipReferences.MaximumDescription) throw new InvalidDataException(SongClipReferences.Invalid);
        var references = SongClipReferences.For(_project).ToList();
        if (references.Count >= SongClipReferences.MaximumCount) throw new InvalidDataException("SongClip.ReferenceLimit");
        var relative = await _store.ImportAsync(_project.Id, file, token);
        var index = references.FindIndex(reference => reference.File == relative);
        if (index < 0) { index = references.Count; references.Add(new(relative, kind, description.Trim())); }
        else references[index] = new(relative, kind, description.Trim());
        _project.References = references.ToArray(); _project.Reference = references[0].File;
        _referenceIndex = index; _store.Save(_project); RefreshReference();
    }
}
