using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed partial class MusicTuningControl : UserControl
{
    private readonly ModelBubbleControl _bubble = new();
    private readonly CheckBox _auto = new();
    private readonly Slider _guidance = new() { Minimum = 0, Maximum = 3, TickFrequency = .01, IsSnapToTickEnabled = true };
    private readonly TextBlock _heading = Text(), _profileState = Text(), _x = Text(), _y = Text(),
        _status = Text(), _guidanceLabel = Text(), _guidanceValue = Text();
    private readonly Button _reset, _guidanceHelp;
    private IReadOnlyList<ModelExpertPreset> _userPresets = [];
    private ModelExpertPresets _store;
    public void ConfigureVariation(string variation) { _store = ModelExpertPresets.For(variation, "Simple"); _userPresets = []; ReloadProfiles(); }
    private readonly Grid _root = new(), _middle = new();
    private readonly StackPanel _side = new() { Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _axes = new(), _guidancePanel = new() { Margin = new(0, 8, 0, 0) };
    private bool _wide;
    private MusicExpertSettings _settings = new();
    private Func<string, string> _l = key => key;
    private bool _refreshing;
    private readonly Button _circleHelp;
    public event Action<MusicExpertSettings>? SettingsChanged;
    public event Action? EditingStarted;
    public MusicTuningControl(ModelExpertPresets? store = null)
    {
        _store = store ?? ModelExpertPresets.Simple;
        AutomationProperties.SetAutomationId(this, "Music.Tuning");
        var root = _root;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        _circleHelp = Help("CircleHelp", () => MusicAceCatalog.IsAce(_settings.Variation)
            ? _l("Music.Ace.Tuning.Hint") : L("MoveHint"));
        Add(HelpHeading(_heading, _circleHelp), 0); _heading.FontWeight = FontWeights.SemiBold;
        Add(_profileState, 1);
        var level = new Grid(); level.Children.Add(_bubble);
        _reset = MusicAudioUi.IconButton("Tuning.Reset", "M5,8 A8,8 0 1 1 4,16 M5,3 V8 H10", () => Change(MusicTuningProfile.ResetCircle(_settings)));
        _reset.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; _reset.VerticalAlignment = VerticalAlignment.Bottom;
        level.Children.Add(_reset); _middle.Children.Add(level); Add(_middle, 3);
        var directions = new WrapPanel(); _x.Margin = new(0, 2, 12, 2);
        directions.Children.Add(_x); directions.Children.Add(_y);
        _axes.Children.Add(directions); _axes.Children.Add(_status); Add(_axes, 4);
        _guidanceHelp = Help("GuidanceHelp", () => L("GuidanceHint"));
        var guidance = _guidancePanel; guidance.Children.Add(HelpHeading(_guidanceLabel, _guidanceHelp));
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_auto); Grid.SetColumn(_guidance, 1); row.Children.Add(_guidance); Grid.SetColumn(_guidanceValue, 2); row.Children.Add(_guidanceValue);
        guidance.Children.Add(row); Add(guidance, 5);
        AutomationProperties.SetAutomationId(_auto, "Music.Tuning.Guidance.Auto");
        AutomationProperties.SetAutomationId(_guidance, "Music.Tuning.Guidance");
        _bubble.PositionChanged += (x, y) => Change(MusicTuningProfile.Move(_settings, x, y));
        _guidance.ValueChanged += (_, _) => { if (!_refreshing) Change(MusicTuningProfile.Guidance(_settings, Math.Round(_guidance.Value, 2))); };
        _auto.Checked += (_, _) => { if (!_refreshing) Change(MusicTuningProfile.Guidance(_settings, null)); };
        _auto.Unchecked += (_, _) => { if (!_refreshing) Change(MusicTuningProfile.Guidance(_settings, _settings.Guidance)); };
        SizeChanged += (_, _) => ArrangeLayout(ActualWidth >= 440);
        Content = root;
        void Add(UIElement child, int rowIndex) { Grid.SetRow(child, rowIndex); root.Children.Add(child); }
    }
    public void Localize(Func<string, string> localize)
    {
        _l = localize; _heading.Text = L("Title"); _guidanceLabel.Text = L("Guidance");
        _auto.Content = L("Automatic"); MusicAudioUi.Label(_reset, L("Reset")); _bubble.ToolTip = L("MoveHint");
        _guidanceHelp.ToolTip = L("GuidanceHint");
        MusicAudioUi.Label(_guidanceHelp, L("GuidanceHelp"));
        AutomationProperties.SetName(_bubble, L("Title")); AutomationProperties.SetName(_guidance, L("Guidance"));
        ReloadProfiles(); Refresh(_settings);
    }
    public void Refresh(MusicExpertSettings settings)
    {
        _refreshing = true;
        try {
            _settings = settings.Snapshot(); var position = MusicTuningProfile.Position(settings); var state = MusicTuningProfile.State(settings);
            var ace = MusicAceCatalog.IsAce(settings.Variation);
            _heading.Text = ace ? _l("Music.Ace.Tuning.Title") : L("Title");
            _bubble.ToolTip = ace ? _l("Music.Ace.Tuning.Hint") : L("MoveHint");
            AutomationProperties.SetName(_bubble, _heading.Text);
            _circleHelp.ToolTip = _bubble.ToolTip;
            _bubble.IsEnabled = !ace || position.CompositionEnabled;
            _reset.IsEnabled = !ace || position.CompositionEnabled;
            _guidancePanel.Visibility = ace ? Visibility.Collapsed : Visibility.Visible;
            _bubble.SetPosition(position.X, position.Y, position.CompositionEnabled);
            _x.Text = ace ? _l("Music.Ace.Tuning.X") + ": " + _l("Music.Ace.Tuning." + (Math.Abs(position.X) < .015 ? "Center" : position.X < 0 ? "Restrained" : "Free"))
                : position.CompositionEnabled ? Axis("Composition", position.X) : L("CompositionOff");
            _y.Text = ace ? _l("Music.Ace.Tuning.Y") + ": " + _l("Music.Ace.Tuning." + (Math.Abs(position.Y) < .015 ? "Center" : position.Y < 0 ? "Softer" : "Stricter"))
                : Axis("Performance", position.Y);
            _status.Text = (position.Approximate ? "≈ " + L("Approximate") + " · " : "") + string.Format(L("Pins"), state.Pins.Length);
            if (ace) {
                if (!position.CompositionEnabled) _status.Text += "\n" + _l("Music.Ace.Tuning.Off");
                else if (settings.Get("thinking") == 0) _status.Text += "\n" + _l("Music.Ace.Tuning.MetadataOnly");
                if (MusicAceTuningProfile.Fields.Any(state.Pins.Contains)) _status.Text += "\n" + _l("Music.Ace.Tuning.PartialPins");
            }
            AutomationProperties.SetHelpText(_bubble, _x.Text + "; " + _y.Text + "; " + _status.Text);
            _auto.IsChecked = !ace && settings.Get("cfg_scale") == -1; _guidance.IsEnabled = _auto.IsChecked != true;
            _guidance.Value = Math.Clamp(settings.Guidance, 0, 3);
            _guidanceValue.Text = settings.Guidance.ToString("0.###", CultureInfo.CurrentCulture);
            _guidance.ToolTip = L("GuidanceHint") + "\nCFG: " + _guidanceValue.Text;
            var selectedRecipe = MusicTuningRecipes.For(settings.Variation).FirstOrDefault(r => r.Id == state.SimplePreset);
            var selectedPreset = _userPresets.FirstOrDefault(p => "User:" + p.Name == state.SimplePreset);
            var modified = selectedRecipe is not null ? !selectedRecipe.Matches(settings)
                : selectedPreset is not null ? !selectedPreset.Settings.SameAs(settings) : state.SimpleModified;
            _profileState.Text = state.SimplePreset is null ? L(settings.SameAs(MusicModelVariants.Defaults(settings.Variation)) && state.Pins.Length == 0 ? "Ordinary" : "Own")
                : NameFor(state.SimplePreset) + (modified ? " · " + L("Modified") : "");
            _status.ToolTip = string.Join("\n", settings.Values.Select(p => p.Key + " = " + p.Value.ToString("0.######", CultureInfo.CurrentCulture) +
                (state.Pins.Contains(p.Key) ? " · " + _l("Music.Expert.Pinned") : "")));
        } finally { _refreshing = false; }
    }
    private void ArrangeLayout(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        _middle.Children.Remove(_side); _side.Children.Clear();
        _root.Children.Remove(_axes); _root.Children.Remove(_guidancePanel); _middle.ColumnDefinitions.Clear();
        if (wide) {
            _middle.ColumnDefinitions.Add(new()); _middle.ColumnDefinitions.Add(new());
            _side.Children.Add(_axes); _side.Children.Add(_guidancePanel); Grid.SetColumn(_side, 1); _middle.Children.Add(_side);
        } else {
            Grid.SetRow(_axes, 4); Grid.SetRow(_guidancePanel, 5); _root.Children.Add(_axes); _root.Children.Add(_guidancePanel);
        }
    }
    private void Change(MusicExpertSettings settings) { settings.Validate(); Refresh(settings); SettingsChanged?.Invoke(settings.Snapshot()); }
    private string Axis(string name, double value) => L(name) + ": " + L(Math.Abs(value) < .015 ? "Ordinary" : value < 0 ? "Predictable" : "Free");
    private string L(string key) => _l("Music.Tuning." + key);
    private static TextBlock Text() { var text = MusicAudioUi.Text(12); text.TextWrapping = TextWrapping.Wrap; text.Margin = new(0, 2, 0, 2); return text; }
}
