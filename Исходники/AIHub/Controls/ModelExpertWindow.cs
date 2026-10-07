using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

/// <summary>Shared expert surface: parameter descriptions and controls are driven by an engine catalog.</summary>
public sealed partial class ModelExpertWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly ModelExpertPresets _store;
    private readonly StackPanel _parameters = new();
    private readonly TextBlock _modified = MusicWishUi.Text("");
    private MusicExpertSettings _draft;
    private readonly Dictionary<string, Button> _pinButtons = new();
    public event Action<MusicExpertSettings>? PreviewChanged;
    public MusicExpertSettings Result { get; private set; }
    public ModelExpertWindow(MusicExpertSettings settings, Func<string, string> localize, ModelExpertPresets? store = null)
    {
        _l = localize; _store = store ?? ModelExpertPresets.Default;
        _draft = settings.Snapshot(); Result = settings.Snapshot();
        MusicWishUi.PrepareWindow(this, (_store.Collection == "Simple" ? _l("Music.Tuning.Library") : L("Title")) + " · " + MusicExpertCatalog.Model, "Music.Expert.Window", 880);
        var root = new DockPanel { Margin = new(16), LastChildFill = true };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        footer.Children.Add(MusicWishUi.Button(L("Reset"), "Music.Expert.Reset", () => { _draft = new(); _selected = null; Reload(); Render(); Changed(); }));
        footer.Children.Add(MusicWishUi.Button(L("Apply"), "Music.Expert.Apply", Apply));
        footer.Children.Add(MusicWishUi.Button(L("Cancel"), "Music.Expert.Cancel", () => { DialogResult = false; }));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var header = BuildPresets(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root; Render(); Changed();
    }
    private string L(string key) => _l("Music.Expert." + key);
    private string ParameterKey(string key) => key[(key.LastIndexOf('.') + 1)..];
    private void Render()
    {
        _parameters.Children.Clear();
        _pinButtons.Clear();
        if (_store.Collection == "Simple") {
            _parameters.Children.Add(MusicWishUi.Text(_l("Music.Tuning.FullSnapshot")));
            foreach (var p in MusicExpertCatalog.Parameters) _parameters.Children.Add(MusicWishUi.Text(
                L("Parameter." + ParameterKey(p.Key)) + " (" + p.Key + "): " + Number(_draft.Get(p.Key))));
            if (_selected?.Recipe is { } recipe) _parameters.Children.Add(MusicWishUi.Text(recipe.Source + "\n" + recipe.Adaptation));
            return;
        }
        _parameters.Children.Add(MusicWishUi.Text(L("Warning")));
        foreach (var group in MusicExpertCatalog.Parameters.GroupBy(p => p.Group))
        {
            _parameters.Children.Add(MusicWishUi.Text(L("Group." + group.Key), true));
            foreach (var parameter in group) AddParameter(parameter);
        }
        _parameters.Children.Add(MusicWishUi.Text(L("Managed")));
    }
    private void AddParameter(ExpertParameter p)
    {
        var row = new Grid { Margin = new(0, 3, 8, 8) };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(1.2, GridUnitType.Star) });
        var editor = new StackPanel { Margin = new(0, 0, 18, 0) };
        editor.IsEnabled = p.Group != "PlanSampling" || _draft.Cot != "off";
        var label = MusicWishUi.Text(L("Parameter." + ParameterKey(p.Key)));
        label.ToolTip = p.Key; editor.Children.Add(label);
        var pin = MusicWishUi.Button("", "Music.Expert.Auto." + p.Key, () => Guard(() => {
            _draft = MusicTuningProfile.Release(_draft, p.Key); Render(); Changed();
        }));
        pin.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _pinButtons[p.Key] = pin; editor.Children.Add(pin); UpdatePin(p.Key);
        var description = MusicWishUi.Text(L("Description." + ParameterKey(p.Key)));
        description.ToolTip = p.Key; Grid.SetColumn(description, 1); row.Children.Add(description);
        row.Children.Add(editor); _parameters.Children.Add(row);
        if (p.Choices is not null)
        {
            var box = new ComboBox { MinHeight = 30, MaxWidth = 300, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            foreach (var choice in p.Choices) box.Items.Add(L("Choice." + choice));
            box.SelectedIndex = _draft.Integer(p.Key); box.ToolTip = p.Key;
            AutomationProperties.SetAutomationId(box, "Music.Expert." + p.Key);
            box.SelectionChanged += (_, _) => { _draft.Values[p.Key] = box.SelectedIndex; Edited(p.Key); if (p.Key == "cot") Render(); };
            editor.Children.Add(box); return;
        }
        var auto = p.Key is "seed" or "lm_seed" or "cfg_scale";
        CheckBox? automatic = null;
        if (auto) {
            automatic = MusicWishUi.Check(L("Automatic"), _draft.Get(p.Key) == -1, _ => { });
            editor.Children.Add(automatic);
        }
        var input = new TextBox { Text = Number(_draft.Get(p.Key) < 0 ? p.Key == "cfg_scale" ? 1 : 0 : _draft.Get(p.Key)),
            MinWidth = 85, MaxWidth = 150, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Padding = new(4), ToolTip = p.Key };
        AutomationProperties.SetAutomationId(input, "Music.Expert." + p.Key);
        Slider? slider = null;
        if (p.Key is not ("seed" or "lm_seed")) {
            slider = new Slider { Minimum = auto ? 0 : p.Minimum, Maximum = p.Maximum, TickFrequency = p.Step,
                IsSnapToTickEnabled = true, Value = Math.Max(auto ? 0 : p.Minimum, _draft.Get(p.Key)), MinWidth = 120,
                Margin = new(0, 2, 8, 2), ToolTip = p.Key };
            AutomationProperties.SetAutomationId(slider, "Music.Expert.Slider." + p.Key);
            editor.Children.Add(slider);
        }
        // Exact entry supplements sliders for large ranges; it never rounds silently on Apply.
        editor.Children.Add(input);
        var updating = false;
        input.TextChanged += (_, _) => {
            if (updating) return;
            if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
                double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) {
                _draft.Values[p.Key] = v;
                if (slider is not null) { updating = true; slider.Value = v; updating = false; }
            } else _draft.Values[p.Key] = double.NaN;
            Edited(p.Key);
        };
        if (slider is not null) slider.ValueChanged += (_, _) => {
            if (updating) return; updating = true;
            var v = Math.Round(slider.Value, 3); _draft.Values[p.Key] = v; input.Text = Number(v);
            updating = false; Edited(p.Key);
        };
        if (automatic is not null) {
            input.IsEnabled = automatic.IsChecked != true; if (slider is not null) slider.IsEnabled = input.IsEnabled;
            void Switch() {
                input.IsEnabled = automatic.IsChecked != true; if (slider is not null) slider.IsEnabled = input.IsEnabled;
                _draft.Values[p.Key] = automatic.IsChecked == true ? -1 :
                    double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : double.NaN;
                Edited(p.Key);
            }
            automatic.Checked += (_, _) => Switch(); automatic.Unchecked += (_, _) => Switch();
        }
    }
    private static string Number(double number) => number.ToString("0.###", CultureInfo.CurrentCulture);
    private void Edited(string key)
    {
        _draft = MusicTuningProfile.Pin(_draft, key);
        if (_draft.Get(key) == -1 && key is "cfg_scale" or "seed" or "lm_seed")
            _draft = _draft with { Tuning = MusicTuningProfile.State(_draft) with { Pins = MusicTuningProfile.State(_draft).Pins.Except([key]).ToArray() } };
        UpdatePin(key); Changed();
    }
    private void UpdatePin(string key)
    {
        if (!_pinButtons.TryGetValue(key, out var button)) return;
        var pinned = MusicTuningProfile.State(_draft).Pins.Contains(key);
        button.Content = L(pinned ? "Pinned" : "Following");
        button.ToolTip = L("FollowHint"); button.IsEnabled = pinned;
    }
    private void Changed()
    {
        var reference = _selected is null ? new MusicExpertSettings() : _selected.Settings;
        _modified.Text = (_draft.SameAs(reference) ? "" : L("Modified")) +
            " · " + string.Format(L("PinCount"), MusicTuningProfile.State(_draft).Pins.Length);
        try { _draft.Validate(); PreviewChanged?.Invoke(_draft.Snapshot()); }
        catch (System.IO.InvalidDataException) { /* Incomplete text edits do not replace the effective preview. */ }
    }
    private void Apply()
    {
        try { _draft.Validate(); if (_store.Collection == "Expert") _store.SetCurrent(_draft); Result = _draft.Snapshot(); DialogResult = true; }
        catch (Exception error) when (error is System.IO.IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { ShowError(error); }
    }
    private void ShowError(Exception error) => MessageBox.Show(this, L("Invalid") + "\n" + error.Message,
        L("Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
