using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Size = System.Windows.Size;

namespace AIHub.Controls;

public sealed record MusicGenerationOptions(string Title, int Variants, int? DurationSeconds)
{
    public string Artist { get; init; } = "";
    public string Comment { get; init; } = "";
    public MusicOutputSettings Output { get; init; } = new();
}

public sealed class MusicGenerationControl : UserControl
{
    private readonly MusicIdentityInput _titleInput = new("Music.Generation.Title"), _artistInput = new("Music.Generation.Artist"),
        _commentInput = new("Music.Generation.Comment", true);
    private TextBox _title => _titleInput.Input;
    private readonly MusicOutputControl _output = new();
    private readonly DispatcherTimer _saveOutput = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Exception? _outputError;
    private readonly MusicOutputPreferences _outputPreferences;
    private readonly ComboBox _count = new() { Width = 65, MinHeight = 30 }, _duration = new() { Width = 190, MinHeight = 30 };
    private readonly Button _start, _cancel, _poetry, _expert, _recipes;
    private MusicExpertSettings _expertSettings = new();
    private Exception? _expertLoadError;
    private ModelExpertWindow? _expertWindow;
    public MusicTuningControl Tuning { get; } = new();
    private readonly DispatcherTimer _saveTuning = new() { Interval = TimeSpan.FromMilliseconds(300) };
    public MusicExpertSettings ExpertSettings => _expertSettings.Snapshot();
    private readonly TextBlock _heading = MusicAudioUi.Text(15), _titleLabel = MusicAudioUi.Text(12),
        _countLabel = MusicAudioUi.Text(12), _durationLabel = MusicAudioUi.Text(12), _readiness = MusicAudioUi.Text(11);
    private readonly Grid _settings = new();
    private readonly TextBlock _hardware = MusicAudioUi.Text(11);
    private Func<string, string> _l = key => key;
    private bool _busy, _paused, _runtimeReady;
    public Func<Task>? StartPause { get; set; }
    public Func<Task>? Cancel { get; set; }
    public event Action? OptionsChanged;
    public MusicGenerationOptions Options => new(_title.Text.Trim(), (int)(_count.SelectedItem ?? 1),
        _duration.SelectedIndex <= 0 ? null : (int?)((ComboBoxItem)_duration.SelectedItem).Tag)
        { Artist = _artistInput.Input.Text.Trim(), Comment = _commentInput.Input.Text, Output = _output.Settings };

    public MusicGenerationControl(MusicOutputPreferences? outputPreferences = null)
    {
        _outputPreferences = outputPreferences ?? MusicOutputPreferences.Default;
        AutomationProperties.SetAutomationId(this, "Music.Generation");
        var root = new DockPanel { Margin = new(12) };
        var top = new Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new() { Width = new(70) });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _settings.Margin = new(0, 0, 12, 0);
        _settings.ColumnDefinitions.Add(new()); _settings.ColumnDefinitions.Add(new());
        for (var i = 0; i < 6; i++) _settings.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new());
        _heading.FontWeight = FontWeights.SemiBold; _heading.Margin = new(0, 0, 10, 0); header.Children.Add(_heading);
        Grid.SetColumn(_commentInput, 1); header.Children.Add(_commentInput); Add(header, 0, 0, 2);
        Add(_titleLabel, 0, 1, 2);
        var identity = new Grid(); identity.ColumnDefinitions.Add(new()); identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); identity.ColumnDefinitions.Add(new());
        identity.Children.Add(_artistInput); var separator = MusicAudioUi.Text(); separator.Text = "—"; separator.Margin = new(6, 0, 6, 0);
        Grid.SetColumn(separator, 1); identity.Children.Add(separator); Grid.SetColumn(_titleInput, 2); identity.Children.Add(_titleInput); Add(identity, 0, 2, 2);
        var choices = new WrapPanel();
        var counts = new StackPanel { Margin = new(0, 4, 12, 0) }; counts.Children.Add(_countLabel); counts.Children.Add(_count);
        var durations = new StackPanel { Margin = new(0, 4, 0, 0) }; durations.Children.Add(_durationLabel); durations.Children.Add(_duration);
        _output.Margin = new(0, 4, 0, 0); durations.Margin = new(0, 4, 12, 0);
        choices.Children.Add(counts); choices.Children.Add(durations); choices.Children.Add(_output); Add(choices, 0, 3, 2);
        _settings.SizeChanged += (_, _) => SizeChoices(_settings.ActualWidth - 8);
        _hardware.TextWrapping = TextWrapping.Wrap; Add(_hardware, 0, 4, 2);
        AutomationProperties.SetAutomationId(_hardware, "Music.Generation.Hardware");
        _readiness.TextWrapping = TextWrapping.Wrap; _readiness.Margin = new(0, 8, 0, 0);
        Add(_readiness, 0, 5, 2); top.Children.Add(_settings);
        foreach (var value in Enumerable.Range(1, 8)) _count.Items.Add(value); _count.SelectedIndex = 0;
        _start = new Button { Width = 56, Height = 56, MinWidth = 0, MinHeight = 0, Padding = new(0),
            Background = Brushes.Red, Foreground = Brushes.White, BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Center };
        _start.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Grid><Ellipse Fill="{TemplateBinding Background}"/><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>
              <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.85"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        _cancel = MusicAudioUi.IconButton("CancelGeneration", "M5,5 L19,19 M19,5 L5,19", () => { });
        _cancel.Width = _cancel.Height = 26; _cancel.Padding = new(5); _cancel.Margin = new(0, 8, 0, 0);
        var actions = new StackPanel { Orientation = Orientation.Vertical }; actions.Children.Add(_start); actions.Children.Add(_cancel);
        _cancel.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(actions, 1); top.Children.Add(actions);
        var lower = new Grid(); lower.ColumnDefinitions.Add(new()); lower.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        lower.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _poetry = MusicAudioUi.IconButton("PoetryChat", "M2,3 H22 V16 H10 L5,21 V16 H2 Z M9,12 L16,5 L19,8 L12,15 H9 Z", () => { });
        _poetry.Width = _poetry.Height = 42; _poetry.IsEnabled = false;
        _poetry.HorizontalAlignment = HorizontalAlignment.Left; lower.Children.Add(_poetry);
        _expert = MusicAudioUi.IconButton("Expert", "M12,2 L14,5 L17,5 L19,7 L19,10 L22,12 L19,14 L19,17 L17,19 L14,19 L12,22 L10,19 L7,19 L5,17 L5,14 L2,12 L5,10 L5,7 L7,5 L10,5 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12", OpenExpert);
        _recipes = MusicAudioUi.IconButton("Recipes", "M3,3 H11 L12,5 L13,3 H21 V20 H13 L12,22 L11,20 H3 Z M12,5 V22 M6,7 H9 M6,11 H9 M15,7 H18 M15,11 H18", Tuning.OpenRecipes);
        _recipes.Width = _recipes.Height = 42; Grid.SetColumn(_recipes, 1); lower.Children.Add(_recipes);
        _expert.Width = _expert.Height = 42; Grid.SetColumn(_expert, 2); lower.Children.Add(_expert);
        var toolbar = new Border { Child = lower, CornerRadius = new(10), BorderThickness = new(1),
            Padding = new(3), Margin = new(0, 12, 0, 0) };
        toolbar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(toolbar, Dock.Bottom);
        root.Children.Insert(0, toolbar); root.Children.Add(Tuning);
        try { _expertSettings = ModelExpertPresets.Default.Current(); }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { _expertLoadError = e; }
        Tuning.Refresh(_expertSettings);
        try { _output.Set(_outputPreferences.Load()); }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { _outputError = error; }
        _output.Changed += () => { _saveOutput.Stop(); _saveOutput.Start(); OptionsChanged?.Invoke(); };
        _saveOutput.Tick += (_, _) => { _saveOutput.Stop(); PersistOutput(); };
        Unloaded += (_, _) => { if (_saveOutput.IsEnabled) { _saveOutput.Stop(); PersistOutput(); } };
        Tuning.SettingsChanged += settings => SetExpertSettings(settings);
        Tuning.EditingStarted += FlushTuning;
        _saveTuning.Tick += (_, _) => { _saveTuning.Stop(); PersistTuning(); };
        Unloaded += (_, _) => { if (_saveTuning.IsEnabled) { _saveTuning.Stop(); PersistTuning(); } };
        AutomationProperties.SetAutomationId(_start, "Music.Generation.StartPause");
        AutomationProperties.SetAutomationId(_title, "Music.Generation.Title");
        AutomationProperties.SetAutomationId(_count, "Music.Generation.Variants");
        AutomationProperties.SetAutomationId(_duration, "Music.Generation.Duration");
        ToolTipService.SetShowOnDisabled(_start, true);
        _start.Click += async (_, _) => { if (StartPause is not null) await StartPause(); };
        _cancel.Click += async (_, _) => { if (Cancel is not null) await Cancel(); };
        _title.TextChanged += (_, _) => OptionsChanged?.Invoke(); _count.SelectionChanged += (_, _) => OptionsChanged?.Invoke();
        _artistInput.Input.TextChanged += (_, _) => OptionsChanged?.Invoke(); _commentInput.Input.TextChanged += (_, _) => OptionsChanged?.Invoke();
        _duration.SelectionChanged += (_, _) => OptionsChanged?.Invoke();
        Content = root; UpdateState(false, false, false, false);
    }
    public void SetHardware(string text) => _hardware.Text = text;
    public void SetOptions(MusicGenerationOptions options)
    {
        _title.Text = options.Title; _artistInput.Input.Text = options.Artist; _commentInput.Input.Text = options.Comment;
        _count.SelectedItem = options.Variants;
        _duration.SelectedIndex = Enumerable.Range(0, _duration.Items.Count).FirstOrDefault(i =>
            (int)((ComboBoxItem)_duration.Items[i]).Tag == (options.DurationSeconds ?? 0));
        _output.Set(options.Output); OptionsChanged?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width)) SizeChoices(availableSize.Width - 114);
        return base.MeasureOverride(availableSize);
    }
    private void SizeChoices(double available)
    {
        _duration.Width = available >= 360 ? Math.Clamp(available - 277, 110, 190) : Math.Clamp(available - 89, 70, 190);
        _output.Width = Math.Clamp(available, 149, 185);
    }

    public void Localize(Func<string, string> localize)
    {
        _l = localize; var selected = _duration.SelectedIndex; _duration.Items.Clear();
        _duration.Items.Add(new ComboBoxItem { Content = L("Automatic"), Tag = 0 });
        foreach (var seconds in new[] { 30, 60, 120, 180, 240, 300, 360 })
            _duration.Items.Add(new ComboBoxItem { Content = seconds < 60 ? L("ThirtySeconds") : string.Format(L("Minutes"), seconds / 60), Tag = seconds });
        _duration.SelectedIndex = Math.Max(0, selected);
        _heading.Text = L("Heading"); _titleLabel.Text = L("Title"); _countLabel.Text = L("Variants"); _durationLabel.Text = L("Duration");
        _title.ToolTip = L("TitleHint"); _duration.ToolTip = L("DurationHint");
        _titleInput.Localize(_l("Music.Output.Title")); _artistInput.Localize(_l("Music.Output.Artist"));
        _commentInput.Localize(_l("Music.Output.Comment")); _output.Localize(localize);
        MusicAudioUi.Label(_poetry, L("Poetry")); MusicAudioUi.Label(_cancel, L("Cancel"));
        MusicAudioUi.Label(_expert, _l("Music.Expert.Title"));
        MusicAudioUi.Label(_recipes, _l("Music.Tuning.Recipes"));
        Tuning.Localize(localize);
        UpdateCaption();
    }
    public void UpdateState(bool canStart, bool busy, bool paused, bool runtimeReady, bool commandsDisabled = false)
    {
        _busy = busy; _paused = paused; _runtimeReady = runtimeReady; _settings.IsEnabled = !busy && !paused;
        _expert.IsEnabled = !busy && !paused && !commandsDisabled;
        _recipes.IsEnabled = !busy && !paused && !commandsDisabled;
        Tuning.IsEnabled = !busy && !paused && !commandsDisabled;
        _start.IsEnabled = paused || busy || canStart && runtimeReady && _expertLoadError is null && _outputError is null; _cancel.IsEnabled = busy || paused;
        if (commandsDisabled) _start.IsEnabled = _cancel.IsEnabled = false;
        _readiness.Text = runtimeReady ? "" : L("RuntimeMissing"); UpdateCaption();
    }
    private void UpdateCaption()
    {
        _readiness.Text = _outputError is not null ? _l("Music.Output.Invalid") : _expertLoadError is not null ? _l("Music.Expert.Invalid") : _runtimeReady ? "" : L("RuntimeMissing");
        var key = _paused ? "Resume" : _busy ? "Pause" : "Start";
        _start.Content = _busy && !_paused ? Icon("M7,4 V20 M17,4 V20") : Icon("M7,4 L20,12 L7,20 Z");
        MusicAudioUi.Label(_start, L(key));
    }
    private void OpenExpert()
    {
        if (_expertWindow is not null) { _expertWindow.Activate(); return; }
        FlushTuning(); var before = _expertSettings.Snapshot(); var beforeError = _expertLoadError; var accepted = false;
        try {
            if (_expertLoadError is not null) System.Windows.MessageBox.Show(Window.GetWindow(this),
                _l("Music.Expert.Invalid") + "\n" + _expertLoadError.Message, _l("Music.Expert.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            var window = new ModelExpertWindow(before, _l) { Owner = Window.GetWindow(this) };
            window.PreviewChanged += settings => SetExpertSettings(settings, false);
            _expertWindow = window;
            if (window.ShowDialog() == true) { SetExpertSettings(window.Result); accepted = true; }
        }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Music.Expert.Invalid") + "\n" + error.Message,
                _l("Music.Expert.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally {
            _expertWindow = null;
            if (!accepted) { _expertSettings = before; _expertLoadError = beforeError; Tuning.Refresh(before); OptionsChanged?.Invoke(); UpdateCaption(); }
        }
    }
    private void FlushTuning() { if (_saveTuning.IsEnabled) { _saveTuning.Stop(); PersistTuning(); } }
    public void SetExpertSettings(MusicExpertSettings settings, bool persist = true)
    {
        settings.Validate(); _expertSettings = settings.Snapshot(); _expertLoadError = null;
        Tuning.Refresh(_expertSettings); OptionsChanged?.Invoke();
        if (persist) { _saveTuning.Stop(); _saveTuning.Start(); }
    }
    private void PersistTuning()
    {
        try { ModelExpertPresets.Default.SetCurrent(_expertSettings); }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            _expertLoadError = error; UpdateCaption(); OptionsChanged?.Invoke();
        }
    }
    private void PersistOutput()
    {
        try { _outputPreferences.Save(_output.Settings); _outputError = null; }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { _outputError = error; }
        UpdateCaption(); OptionsChanged?.Invoke();
    }
    private static UIElement Icon(string geometry) => new Viewbox { Width = 22, Height = 22,
        Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = Brushes.White, StrokeThickness = 2.5 } };
    private string L(string key) => _l("Music.Generation." + key);
    private void Add(UIElement child, int column, int row, int span = 1)
    {
        Grid.SetColumn(child, column); Grid.SetRow(child, row); Grid.SetColumnSpan(child, span);
        if (child is FrameworkElement element && child != _readiness) element.Margin = new(0, 4, 8, 0);
        _settings.Children.Add(child);
    }
}
