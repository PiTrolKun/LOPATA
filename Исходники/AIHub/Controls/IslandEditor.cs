using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AIHub.Services;
using Lopata.Island;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace AIHub.Controls;

internal sealed class IslandEditor : System.Windows.Controls.UserControl
{
    private readonly Func<string, string> _localize;
    private readonly bool _showPreview;
    private readonly bool _allowStartup;
    private readonly Action? _openVoiceCommands;
    private readonly StackPanel _extended = new() { Margin = new(0, 0, 0, 8) };
    private readonly Border _preview = new()
    {
        Padding = new Thickness(12), CornerRadius = new CornerRadius(12), MinHeight = 214,
        Background = new LinearGradientBrush(Color.FromRgb(29, 50, 64), Color.FromRgb(16, 25, 39), 132)
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 9, 0, 0) };
    private readonly Button _apply = new();
    private CheckBox? _followPointer;
    private IslandPreferences _saved = IslandPreferences.Read();
    private IslandPreferences _draft;
    private int _view;
    private string? _appliedControlTheme;
    private ResourceDictionary? _controlTheme;

    internal IslandEditor(Func<string, string> localize, bool showPreview, bool allowStartup = true, Action? openVoiceCommands = null)
    {
        _localize = localize;
        _view = 2;
        _showPreview = showPreview;
        _allowStartup = allowStartup;
        _openVoiceCommands = openVoiceCommands;
        _draft = _saved;
        AutomationProperties.SetAutomationId(this, showPreview ? "Island.Editor" : "Island.Global");
        Content = Build();
        RefreshPreview();
    }

    internal event Action? PreferencesApplied;

    internal bool HasNewSavedPreferences() => _draft == _saved && IslandPreferences.Read() != _saved;

    internal void SetDarkTheme(bool dark)
    {
        string theme = SystemParameters.HighContrast ? "HC" : dark ? "Dark" : "Light";
        if (theme == _appliedControlTheme) return;
        _appliedControlTheme = theme;
        if (_controlTheme is not null) Resources.MergedDictionaries.Remove(_controlTheme);
        _controlTheme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{theme}.xaml")
        };
        Resources.MergedDictionaries.Add(_controlTheme);
    }

    private FrameworkElement Build()
    {
        var root = new StackPanel { MaxWidth = 1040, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        if (_showPreview) root.Children.Add(Heading("Island.Title", 23));
        var controls = new StackPanel { MinWidth = 250 };
        controls.Children.Add(Heading("Island.Appearance", 16));
        controls.Children.Add(Combo("Island.Tone", ["Island.Graphite", "Island.Ocean", "Island.Forest"],
            Array.IndexOf(new[] { "Graphite", "Ocean", "Forest" }, _draft.Tone),
            index => Change(_draft with { Tone = new[] { "Graphite", "Ocean", "Forest" }[index] })));
        controls.Children.Add(Check("Island.Above", _draft.AboveWindows, value => Change(_draft with { AboveWindows = value })));
        controls.Children.Add(Check("Island.Advanced", _draft.Advanced, value => Change(_draft with { Advanced = value })));
        _extended.Children.Add(Check("Island.ReducedMotion", _draft.ReduceMotion, v => Change(_draft with { ReduceMotion = v })));
        _followPointer = Check("Island.Pointer", _draft.FollowPointer, value =>
        {
            if (value != _draft.FollowPointer) Change(_draft with { FollowPointer = value });
        });
        _extended.Children.Add(_followPointer);
        var displays = System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName).Prepend("").ToArray();
        var names = displays.Select((device, i) => i == 0 ? _localize("Island.Primary") : $"{_localize("Island.Display")} {i}").ToArray();
        _extended.Children.Add(Combo("Island.Display", names, Math.Max(0, Array.IndexOf(displays, _draft.Monitor)),
            i => Change(_draft with { Monitor = displays[i], FollowPointer = false }), translate: false));
        _extended.Children.Add(Check("Island.Hotkey", _draft.Hotkey, v => Change(_draft with { Hotkey = v })));
        AddSize("Island.ClosedWidth", 240, 520, _draft.ClosedWidth, v => Change(_draft with { ClosedWidth = v }));
        AddSize("Island.ClosedHeight", 30, 58, _draft.ClosedHeight, v => Change(_draft with { ClosedHeight = v }));
        AddSize("Island.HoverWidth", 300, 540, _draft.HoverWidth, v => Change(_draft with { HoverWidth = v }));
        AddSize("Island.HoverHeight", IslandPreferences.MinimumHoverHeight, 260, _draft.HoverHeight, v => Change(_draft with { HoverHeight = v }));
        AddSize("Island.PanelWidth", 320, 580, _draft.PanelWidth, v => Change(_draft with { PanelWidth = v }));
        AddSize("Island.PanelHeight", 270, 540, _draft.PanelHeight, v => Change(_draft with { PanelHeight = v }));
        controls.Children.Add(_extended);
        if (_allowStartup && !_showPreview)
        {
            var startup = Check("Island.Autostart", IslandRuntime.StartsWithWindows(), _ => { });
            startup.IsEnabled = IslandRuntime.IsAvailable || startup.IsChecked == true;
            startup.Click += async (_, _) =>
            {
                bool enabled = startup.IsChecked == true;
                try { await IslandRuntime.SetStartupAsync(enabled); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
                { startup.IsChecked = !enabled; _status.Text = _localize("Island.Failed"); }
            };
            controls.Children.Add(startup);
        }
        var commands = new WrapPanel { Margin = new(0, 9, 0, 0) };
        _apply.Content = _localize("Island.Apply");
        _apply.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        _apply.IsEnabled = false;
        _apply.Margin = new(0, 0, 8, 0);
        AutomationProperties.SetAutomationId(_apply, "Island.Apply");
        _apply.Click += (_, _) => Apply();
        commands.Children.Add(_apply);
        var launch = new Button { Content = _localize("Island.Launch"), IsEnabled = IslandRuntime.IsAvailable };
        AutomationProperties.SetAutomationId(launch, "Island.Launch");
        launch.Click += async (_, _) =>
        {
            launch.IsEnabled = false;
            try { await IslandRuntime.StartAsync(); _status.Text = ""; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException)
            {
                OwnedProcessRegistry.Log("island_launch_failed", "NabyScape", detail: error.GetType().Name);
                _status.Text = _localize("Island.Failed");
            }
            finally { launch.IsEnabled = IslandRuntime.IsAvailable; }
        };
        commands.Children.Add(launch);
        if (_openVoiceCommands is not null)
        {
            var voice = new Button { Content = _localize("Island.VoiceCommands"), Margin = new(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(voice, "Island.VoiceCommands");
            voice.Click += (_, _) => _openVoiceCommands();
            commands.Children.Add(voice);
        }
        controls.Children.Add(commands);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        controls.Children.Add(_status);
        if (!IslandRuntime.IsAvailable) _status.Text = _localize("Island.Unavailable");

        if (_showPreview)
        {
            var layout = new Grid { Margin = new(0, 14, 0, 0) };
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(1.6, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var stage = new StackPanel();
            stage.Children.Add(Heading("Island.Preview", 16));
            var tabs = new WrapPanel { Margin = new(0, 0, 0, 8) };
            foreach (string key in new[] { "Island.Compact", "Island.Hover", "Island.Expanded" })
            {
                int view = tabs.Children.Count;
                var tab = new System.Windows.Controls.RadioButton
                {
                    GroupName = "IslandPreviewViews", Content = _localize(key), IsChecked = view == _view, Margin = new(0, 0, 12, 0)
                };
                tab.SetResourceReference(System.Windows.Controls.RadioButton.ForegroundProperty, "TextPrimaryBrush");
                tab.Checked += (_, _) => { _view = view; RefreshPreview(); };
                tabs.Children.Add(tab);
            }
            stage.Children.Add(tabs);
            stage.Children.Add(_preview);
            layout.Children.Add(stage);
            Grid.SetColumn(controls, 2);
            layout.Children.Add(controls);
            root.Children.Add(layout);
        }
        else
        {
            controls.MaxWidth = 490;
            controls.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            root.Children.Add(controls);
        }

        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private TextBlock Heading(string key, int size)
    {
        var title = new TextBlock { Text = _localize(key), FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 9) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return title;
    }

    private CheckBox Check(string key, bool value, Action<bool> changed)
    {
        var caption = _localize(key);
        var check = new CheckBox
        {
            Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap },
            IsChecked = value, Margin = new(0, 0, 0, 6)
        };
        check.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(check, key);
        AutomationProperties.SetName(check, caption);
        check.Checked += (_, _) => changed(true);
        check.Unchecked += (_, _) => changed(false);
        return check;
    }

    private StackPanel Combo(string key, string[] choices, int selected, Action<int> changed, bool translate = true)
    {
        var box = new ComboBox { ItemsSource = translate ? choices.Select(_localize).ToArray() : choices, SelectedIndex = selected,
            Margin = new(0, 1, 0, 8) };
        AutomationProperties.SetName(box, _localize(key));
        AutomationProperties.SetAutomationId(box, key);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(box.SelectedIndex); };
        return new StackPanel { Children = { Heading(key, 13), box } };
    }

    private void AddSize(string key, int min, int max, int initial, Action<int> changed)
    {
        var readout = new TextBlock { Text = initial.ToString(), Width = 40, VerticalAlignment = VerticalAlignment.Center };
        readout.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = initial, TickFrequency = 1, IsSnapToTickEnabled = true,
            Margin = new(0, 2, 6, 4) };
        AutomationProperties.SetName(slider, _localize(key));
        AutomationProperties.SetAutomationId(slider, key);
        slider.ValueChanged += (_, _) => { int value = (int)slider.Value; readout.Text = value.ToString(); changed(value); };
        var line = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        line.Children.Add(readout);
        line.Children.Add(slider);
        _extended.Children.Add(Heading(key, 12));
        _extended.Children.Add(line);
    }

    private void Change(IslandPreferences next)
    {
        _draft = next.Normalize();
        if (_followPointer is { } follow && follow.IsChecked != _draft.FollowPointer) follow.IsChecked = _draft.FollowPointer;
        _apply.IsEnabled = _draft != _saved;
        RefreshPreview();
    }

    private void Apply()
    {
        try
        {
            IslandPreferences.Save(_draft);
            _saved = _draft;
            _apply.IsEnabled = false;
            _status.Text = _localize("Island.Applied");
            PreferencesApplied?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = _localize("Island.Failed"); }
    }

    private void RefreshPreview()
    {
        _extended.Visibility = _draft.Advanced ? Visibility.Visible : Visibility.Collapsed;
        if (!_showPreview) return;
        var geometry = _draft.Advanced ? _draft : new IslandPreferences();
        var accent = (Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_draft.Accent)!;
        var content = new StackPanel();
        int width = _view == 0 ? geometry.ClosedWidth : _view == 1 ? geometry.HoverWidth : geometry.PanelWidth;
        if (_view == 0)
        {
            var header = new Grid();
            header.ColumnDefinitions.Add(new());
            header.ColumnDefinitions.Add(new());
            header.ColumnDefinitions.Add(new());
            var time = PreviewText(DateTime.Now.ToString("HH:mm"), 11, "#F3F7FA");
            header.Children.Add(time);
            var date = PreviewText(DateTime.Now.ToString("dd.MM"), 11, "#B4C2D2");
            date.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            Grid.SetColumn(date, 1);
            header.Children.Add(date);
            var light = new Ellipse { Fill = accent, Width = 8, Height = 8, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            Grid.SetColumn(light, 2);
            header.Children.Add(light);
            content.Children.Add(header);
        }
        else
        {
            if (_view == 2)
            {
                var pages = PreviewText("●   ●   ●   ●   ●   ●   ●", 8, "#91A5B4");
                pages.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                content.Children.Add(pages);
                var actions = new UniformGrid { Rows = 1, Margin = new(0, 10, 0, 0) };
                foreach (string key in new[] { "Island.Open", _draft.AboveWindows ? "Island.Above" : "Island.NormalLayer", "Island.Close" })
                {
                    var action = PreviewText(_localize(key), 10, "#EFF5FA");
                    action.TextAlignment = TextAlignment.Center;
                    actions.Children.Add(new Border { Child = action, Padding = new(4, 7, 4, 7), Margin = new(2, 0, 2, 0),
                        Background = new SolidColorBrush(Color.FromRgb(32, 44, 56)), CornerRadius = new CornerRadius(6) });
                }
                content.Children.Add(actions);
            }
            var title = PreviewText(_localize("Island.PreviewTrack"), 17, "#F3F7FA");
            title.TextAlignment = TextAlignment.Center;
            title.Margin = new Thickness(0, _view == 2 ? 17 : 4, 0, 4);
            content.Children.Add(title);
            var source = PreviewText(_localize("Island.PreviewSource"), 12, "#B9C8D4");
            source.TextAlignment = TextAlignment.Center;
            content.Children.Add(source);
            if (_view == 2)
            {
                var playback = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 14, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                playback.Children.Add(PreviewText("│◀", 13, "#A9BACC"));
                var play = PreviewText("▶", 16, "#101820");
                play.TextAlignment = TextAlignment.Center;
                play.VerticalAlignment = VerticalAlignment.Center;
                playback.Children.Add(new Border { Width = 39, Height = 39, Background = accent,
                    CornerRadius = new CornerRadius(20), Margin = new Thickness(18, 0, 18, 0), Child = play });
                playback.Children.Add(PreviewText("▶│", 13, "#A9BACC"));
                foreach (FrameworkElement item in playback.Children) item.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(playback);
            }
            var meter = new Grid { Height = 3, Margin = new Thickness(0, 13, 0, 0), Background = new SolidColorBrush(Color.FromRgb(71, 86, 98)) };
            meter.ColumnDefinitions.Add(new() { Width = new GridLength(0.42, GridUnitType.Star) });
            meter.ColumnDefinitions.Add(new() { Width = new GridLength(0.58, GridUnitType.Star) });
            meter.Children.Add(new Border { Background = accent, CornerRadius = new CornerRadius(1) });
            content.Children.Add(meter);
        }
        AutomationProperties.SetAutomationId(content, "Island.Sample");
        var dock = new Border
        {
            Child = content, Width = width,
            MinHeight = _view == 0 ? geometry.ClosedHeight : _view == 1 ? geometry.HoverHeight : Math.Max(175, geometry.PanelHeight * 0.58),
            Padding = _view == 0 ? new Thickness(13, 7, 13, 6) : new Thickness(14),
            CornerRadius = new CornerRadius(_view == 0 ? 19 : 16),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(70, 88, 101)),
            Background = _view == 2
                ? new LinearGradientBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(_draft.Surface),
                    Color.FromArgb(92, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B), 90)
                : (Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_draft.Surface)!,
            VerticalAlignment = VerticalAlignment.Top
        };
        if (IsLoaded && !(_draft.Advanced && _draft.ReduceMotion) && !SystemParameters.HighContrast)
            dock.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.65, 1,
                new Duration(TimeSpan.FromMilliseconds(120))));
        _preview.Child = new Viewbox { Child = dock, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    }

    private static TextBlock PreviewText(string text, double size, string color) => new()
    {
        Text = text, FontSize = size, Foreground = (Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}
