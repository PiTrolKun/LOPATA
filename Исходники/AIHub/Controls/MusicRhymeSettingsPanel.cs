using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

/// <summary>Settings stay inside the workshop; applying them only affects future task snapshots.</summary>
public sealed class MusicRhymeSettingsPanel : DockPanel
{
    public static readonly string[] GroupIds = ["base", "analysis", "search", "program", "quality", "rag", "model", "execution", "history"];
    public static readonly string[] SimpleSettingIds = ["analysis.max_fragments", "analysis.line_share", "program.changes_per_fragment", "Temperature"];
    private readonly MusicRhymeController _owner;
    private readonly Func<string, string> _l;
    private readonly Action _close;
    private readonly StackPanel _groups = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, Func<double>> _read = new(StringComparer.Ordinal);
    private MusicRhymeOptions _draft;
    private CheckBox _selection = null!, _auto = null!, _personalLyrics = null!;
    private readonly CheckBox _expert;
    private bool _switchingMode;
    private ComboBox _models = null!;
    public MusicRhymeSettingsPanel(MusicRhymeController owner, Func<string, string> l, Action close)
    {
        _owner = owner; _l = l; _close = close; _draft = owner.Options;
        AutomationProperties.SetAutomationId(this, "Music.Rhyme.Settings.Panel");
        var intro = MusicRhymeSettingRows.Text(l("Music.Rhyme.Settings.Intro"), "TextSecondaryBrush");
        SetDock(intro, Dock.Top); Children.Add(intro);
        _expert = new CheckBox { Content = l("Music.Rhyme.Settings.ExpertMode"), IsChecked = _draft.ExpertMode,
            Margin = new(0, 8, 0, 4), ToolTip = l("Music.Rhyme.Settings.ExpertMode.Hint") };
        _expert.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_expert, "Music.Rhyme.Settings.ExpertMode");
        SetDock(_expert, Dock.Top); Children.Add(_expert);
        _expert.Checked += (_, _) => SwitchMode(true); _expert.Unchecked += (_, _) => SwitchMode(false);
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) }; SetDock(footer, Dock.Bottom); Children.Add(footer);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(actions);
        AddButton(actions, "Defaults", () => { _draft = new(); Build(); });
        AddButton(actions, "Cancel", close);
        AddButton(actions, "Apply", Apply);
        var scroll = new ScrollViewer { Content = _groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 8, 0, 0) };
        Children.Add(scroll); Build();
    }
    private void AddButton(Panel panel, string action, Action click)
    {
        var button = new Button { Content = _l("Music.Rhyme.Settings." + action), Padding = new(12, 7, 12, 7), Margin = new(5, 0, 0, 0) };
        AutomationProperties.SetAutomationId(button, "Music.Rhyme.Settings." + action);
        button.Click += (_, _) => click(); panel.Children.Add(button);
    }
    private void Build()
    {
        _read.Clear(); _groups.Children.Clear(); _error.Text = "";
        _switchingMode = true; _expert.IsChecked = _draft.ExpertMode; _switchingMode = false;
        if (!_draft.ExpertMode)
        {
            var simple = new StackPanel { Margin = new(12) }; Basics(simple);
            foreach (var id in SimpleSettingIds)
            {
                var setting = MusicRhymeSettingCatalog.Find(id);
                _read[id] = MusicRhymeSettingRows.AddSlider(simple, setting, _draft.Tuning.Number(id), _l,
                    id == "analysis.line_share");
            }
            _groups.Children.Add(Cloud(simple)); return;
        }
        foreach (var id in GroupIds)
        {
            var content = new StackPanel { Margin = new(12, 4, 12, 12) };
            if (id == "base") Basics(content);
            foreach (var setting in MusicRhymeSettingCatalog.All.Where(s => s.Group == id))
                _read[setting.Id] = MusicRhymeSettingRows.Add(content, setting, _draft.Tuning.Number(setting.Id), _l);
            var expander = new Expander { Header = _l("Music.Rhyme.Settings.Group." + id), Content = content,
                IsExpanded = false, Padding = new(6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            expander.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(expander, "Music.Rhyme.Settings.Group." + id);
            _groups.Children.Add(Cloud(expander));
        }
    }
    private static Border Cloud(UIElement content)
    {
        var cloud = new Border { CornerRadius = new(12), BorderThickness = new(1), Child = content, Margin = new(0, 0, 0, 8) };
        cloud.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        cloud.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return cloud;
    }
    private void SwitchMode(bool expert)
    {
        if (_switchingMode) return;
        try { _draft = CaptureOptions() with { ExpertMode = expert }; Build(); }
        catch (Exception error)
        {
            _switchingMode = true; _expert.IsChecked = _draft.ExpertMode; _switchingMode = false;
            ShowError(error);
        }
    }
    private void Basics(Panel content)
    {
        content.Children.Add(MusicRhymeSettingRows.Text(_l("Music.Rhyme.Model"), "TextPrimaryBrush"));
        _models = new ComboBox { Margin = new(0, 4, 0, 6), MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(_models, "Music.Rhyme.Settings.Model");
        FillModels(); content.Children.Add(_models);
        var catalog = new Button { Content = _l("Music.Rhyme.ModelCatalog"), HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new(10, 5, 10, 5), Margin = new(0, 0, 0, 10) };
        catalog.Click += (_, _) => _owner.ShowModelCatalog(id => { _draft = _draft with { ModelId = id }; FillModels(); });
        content.Children.Add(catalog);
        _selection = Check(content, "Music.Rhyme.SelectionOnly", _draft.SelectionOnly);
        _auto = Check(content, "Music.Rhyme.AutoAnalyze", _draft.AutoAnalyze);
        _personalLyrics = Check(content, "Music.Library.UseExamples", _draft.UsePersonalLyricsLibrary);
        content.Children.Add(MusicRhymeSettingRows.Text(_l("Music.Rhyme.Settings.Color"), "TextPrimaryBrush"));
        var palette = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 10) }; content.Children.Add(palette);
        foreach (var color in MusicRhymePreferences.Palette)
        {
            var swatch = new Button { Width = 28, Height = 28, Margin = new(2), ToolTip = color,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), BorderThickness = new(color == _draft.Color ? 3 : 1) };
            AutomationProperties.SetAutomationId(swatch, "Music.Rhyme.Color." + color[1..]);
            AutomationProperties.SetName(swatch, _l("Music.Rhyme.Settings.Color") + " " + color);
            swatch.Click += (_, _) => { _draft = _draft with { Color = color };
                foreach (Button other in palette.Children) other.BorderThickness = new(other == swatch ? 3 : 1); };
            palette.Children.Add(swatch);
        }
    }
    private CheckBox Check(Panel panel, string key, bool value)
    {
        var check = new CheckBox { Content = _l(key), IsChecked = value, Margin = new(0, 3, 0, 8) };
        check.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(check, "Music.Rhyme.Settings." + key.Split('.').Last());
        panel.Children.Add(check); return check;
    }
    private void FillModels()
    {
        _models.Items.Clear();
        foreach (var choice in _owner.ModelChoices)
        {
            var item = new ComboBoxItem { Content = choice.Candidate.DisplayName ?? choice.Candidate.FileName,
                Tag = choice.Candidate.Id, IsEnabled = choice.Model is not null };
            _models.Items.Add(item); if (choice.Candidate.Id == _draft.ModelId) _models.SelectedItem = item;
        }
    }
    private void Apply()
    {
        try
        {
            var options = CaptureOptions();
            if (_owner.ApplyOptions(options)) _close(); else _error.Text = _l("Music.Rhyme.Settings.SaveFailed");
        }
        catch (Exception error) { ShowError(error); }
    }
    internal MusicRhymeOptions CaptureOptions()
    {
        // Hidden expert values remain part of this draft when the simple controls are applied.
        var values = new Dictionary<string, double>(_draft.Tuning.Values, StringComparer.Ordinal);
        foreach (var (id, read) in _read)
            if (MusicRhymeSettingCatalog.Find(id).Kind != MusicRhymeSettingKind.Information) values[id] = read();
        var options = _draft with { SelectionOnly = _selection.IsChecked == true, AutoAnalyze = _auto.IsChecked == true,
            UsePersonalLyricsLibrary = _personalLyrics.IsChecked == true,
            ModelId = (_models.SelectedItem as ComboBoxItem)?.Tag as string ?? _draft.ModelId, Tuning = new() { Values = values } };
        options.Validate(); return options;
    }
    private void ShowError(Exception error) => _error.Text = error is ArgumentException
        ? string.Format(_l("Music.Rhyme.Settings.InvalidField"), error.Message) : _owner.Error(error.Message);
}
