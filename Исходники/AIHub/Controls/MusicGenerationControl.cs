using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed record MusicGenerationOptions(string Title, int Variants, int? DurationSeconds);

public sealed class MusicGenerationControl : UserControl
{
    private readonly TextBox _title = new() { MinWidth = 70, Padding = new(5), MaxLength = 160 };
    private readonly ComboBox _count = new() { Width = 65, MinHeight = 30 }, _duration = new() { Width = 190, MinHeight = 30 };
    private readonly Button _start, _cancel, _poetry;
    private readonly TextBlock _heading = MusicAudioUi.Text(15), _titleLabel = MusicAudioUi.Text(12),
        _countLabel = MusicAudioUi.Text(12), _durationLabel = MusicAudioUi.Text(12), _readiness = MusicAudioUi.Text(11);
    private readonly Grid _settings = new();
    private Func<string, string> _l = key => key;
    private bool _busy, _paused, _runtimeReady;
    public Func<Task>? StartPause { get; set; }
    public Func<Task>? Cancel { get; set; }
    public event Action? OptionsChanged;
    public MusicGenerationOptions Options => new(_title.Text.Trim(), (int)(_count.SelectedItem ?? 1),
        _duration.SelectedIndex <= 0 ? null : (int?)((ComboBoxItem)_duration.SelectedItem).Tag);

    public MusicGenerationControl()
    {
        AutomationProperties.SetAutomationId(this, "Music.Generation");
        var root = new DockPanel { Margin = new(12) };
        var top = new Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new() { Width = new(70) });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _settings.Margin = new(0, 0, 12, 0);
        _settings.ColumnDefinitions.Add(new()); _settings.ColumnDefinitions.Add(new());
        for (var i = 0; i < 6; i++) _settings.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Add(_heading, 0, 0, 2); _heading.FontWeight = FontWeights.SemiBold;
        Add(_titleLabel, 0, 1, 2); Add(_title, 0, 2, 2);
        var choices = new WrapPanel();
        var counts = new StackPanel { Margin = new(0, 4, 12, 0) }; counts.Children.Add(_countLabel); counts.Children.Add(_count);
        var durations = new StackPanel { Margin = new(0, 4, 0, 0) }; durations.Children.Add(_durationLabel); durations.Children.Add(_duration);
        choices.Children.Add(counts); choices.Children.Add(durations); Add(choices, 0, 3, 2);
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
        var lower = new StackPanel { Margin = new(0, 20, 0, 0) };
        _poetry = MusicAudioUi.IconButton("PoetryChat", "M2,3 H22 V16 H10 L5,21 V16 H2 Z M9,12 L16,5 L19,8 L12,15 H9 Z", () => { });
        _poetry.Width = _poetry.Height = 42; _poetry.IsEnabled = false; lower.Children.Add(_poetry); root.Children.Add(lower);
        AutomationProperties.SetAutomationId(_start, "Music.Generation.StartPause");
        AutomationProperties.SetAutomationId(_title, "Music.Generation.Title");
        AutomationProperties.SetAutomationId(_count, "Music.Generation.Variants");
        AutomationProperties.SetAutomationId(_duration, "Music.Generation.Duration");
        ToolTipService.SetShowOnDisabled(_start, true);
        _start.Click += async (_, _) => { if (StartPause is not null) await StartPause(); };
        _cancel.Click += async (_, _) => { if (Cancel is not null) await Cancel(); };
        _title.TextChanged += (_, _) => OptionsChanged?.Invoke(); _count.SelectionChanged += (_, _) => OptionsChanged?.Invoke();
        _duration.SelectionChanged += (_, _) => OptionsChanged?.Invoke();
        Content = root; UpdateState(false, false, false, false);
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
        MusicAudioUi.Label(_poetry, L("Poetry")); MusicAudioUi.Label(_cancel, L("Cancel"));
        UpdateCaption();
    }
    public void UpdateState(bool canStart, bool busy, bool paused, bool runtimeReady, bool commandsDisabled = false)
    {
        _busy = busy; _paused = paused; _runtimeReady = runtimeReady; _settings.IsEnabled = !busy && !paused;
        _start.IsEnabled = paused || busy || canStart && runtimeReady; _cancel.IsEnabled = busy || paused;
        if (commandsDisabled) _start.IsEnabled = _cancel.IsEnabled = false;
        _readiness.Text = runtimeReady ? "" : L("RuntimeMissing"); UpdateCaption();
    }
    private void UpdateCaption()
    {
        _readiness.Text = _runtimeReady ? "" : L("RuntimeMissing");
        var key = _paused ? "Resume" : _busy ? "Pause" : "Start";
        _start.Content = _busy && !_paused ? Icon("M7,4 V20 M17,4 V20") : Icon("M7,4 L20,12 L7,20 Z");
        MusicAudioUi.Label(_start, L(key));
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
