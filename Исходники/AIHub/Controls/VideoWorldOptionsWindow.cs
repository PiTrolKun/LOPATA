using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

/// <summary>Three persistent horizontal slots. Choosing a ready result never waits for the other calls.</summary>
public sealed class VideoWorldOptionsWindow : Window
{
    private readonly Func<string, string> _localize;
    private readonly StackPanel[] _slots = new StackPanel[3];
    private readonly ComboBox _models;
    private readonly Button _run;
    private readonly Button _pause;
    private readonly string[] _shown = new string[3], _latest = new string[3];
    private bool _paused;
    private IReadOnlyList<VideoWorldOption> _current = [];
    private bool _busy;
    public string SelectedModelId => (string?)_models.SelectedValue ?? "";
    public event Action<string>? StartRequested;
    public event Action? PauseRequested;
    public event Action<string>? ChooseRequested;
    public event Action<string>? RegenerateRequested;
    public string SelectedOptionId { get; private set; } = "";
    private string T(string key) => _localize("VideoPrep." + key);

    public VideoWorldOptionsWindow(Window? owner, Func<string, string> localize, IReadOnlyList<VideoConceptChoice> choices,
        string selectedModel)
    {
        _localize = localize; Owner = owner; Title = T("WorldPropose"); ShowInTaskbar = false;
        Width = Math.Min(1220, SystemParameters.WorkArea.Width - 40); Height = Math.Min(760, SystemParameters.WorkArea.Height - 40);
        MinWidth = 500; MinHeight = 380; WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "LineBrush", "TextPrimaryBrush", "TextSecondaryBrush",
            "AccentBrush", "SecondaryButtonBackgroundBrush", "StepBadgeBrush", "UiBodyFontSize", "UiSectionFontSize" })
            if (owner?.TryFindResource(key) is { } value) Resources[key] = value;
        foreach (var (key, color) in new[] { ("WindowBackgroundBrush", Colors.White), ("PanelBrush", Colors.WhiteSmoke), ("LineBrush", Colors.Gray),
            ("TextPrimaryBrush", Colors.Black), ("TextSecondaryBrush", Colors.DimGray), ("AccentBrush", Colors.RoyalBlue),
            ("SecondaryButtonBackgroundBrush", Colors.WhiteSmoke), ("StepBadgeBrush", Colors.LightBlue) })
            if (!Resources.Contains(key)) Resources[key] = new SolidColorBrush(color);
        if (!Resources.Contains("UiBodyFontSize")) Resources["UiBodyFontSize"] = 14d;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize"); UseLayoutRounding = true;
        AutomationProperties.SetAutomationId(this, "VideoPrep.WorldOptions");
        SourceInitialized += (_, _) => WindowTitleBarThemeService.Apply(this, Resources["WindowBackgroundBrush"] is SolidColorBrush b && b.Color.R < 128);
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel(); top.Children.Add(Label(T("WorldOptionsHint")));
        _models = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = selectedModel, MinHeight = 36 };
        AutomationProperties.SetAutomationId(_models, "VideoPrep.WorldModel"); AutomationProperties.SetName(_models, T("AiModel"));
        top.Children.Add(_models); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _run = MakeButton("WorldStart", () => StartRequested?.Invoke((string?)_models.SelectedValue ?? ""));
        _pause = MakeButton("AiPauseResume", () => PauseRequested?.Invoke());
        _models.SelectionChanged += (_, _) => _run.IsEnabled = choices.FirstOrDefault(c => c.Id == (string?)_models.SelectedValue) is { Model: not null, Fits: not false };
        _run.IsEnabled = choices.FirstOrDefault(c => c.Id == selectedModel) is { Model: not null, Fits: not false };
        footer.Children.Add(_pause); footer.Children.Add(_run);
        footer.Children.Add(MakeButton("WorldCancel", Close)); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var columns = new Grid { MinWidth = 900, Width = 900 };
        for (var i = 0; i < 3; i++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            _slots[i] = new StackPanel();
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(5, 12, 5, 0),
                Child = new ScrollViewer { Content = _slots[i], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            AutomationProperties.SetAutomationId(border, "VideoPrep.WorldOption." + (i + 1)); Grid.SetColumn(border, i); columns.Children.Add(border);
        }
        var horizontalScroll = new ScrollViewer { Content = columns, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        horizontalScroll.SizeChanged += (_, _) => columns.Width = Math.Max(900, horizontalScroll.ActualWidth - 2);
        root.Children.Add(horizontalScroll);
        Content = root; Update([], false);
    }
    private TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
    private Button MakeButton(string key, Action action)
    {
        var b = MusicWishUi.Button(T(key), "VideoPrep." + key, action); b.Padding = new Thickness(10, 8, 10, 8);
        if (b.Content is TextBlock t) t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
        return b;
    }
    public void Update(IReadOnlyList<VideoWorldOption> options, bool busy)
    {
        _current = options; _busy = busy;
        _models.IsEnabled = !busy; _run.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _pause.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < 3; i++)
        {
            var slot = _slots[i]; slot.Children.Clear(); var level = i + 1;
            var heading = Label(T("AiFreedom." + level)); heading.FontWeight = FontWeights.SemiBold; slot.Children.Add(heading);
            var levelOptions = options.Where(o => o.Freedom == level).ToArray();
            if (_latest[i] != levelOptions.LastOrDefault()?.Id) { _latest[i] = levelOptions.LastOrDefault()?.Id ?? ""; _shown[i] = _latest[i]; }
            var option = levelOptions.FirstOrDefault(o => o.Id == _shown[i]) ?? levelOptions.LastOrDefault();
            if (levelOptions.Length > 1)
            {
                var index = Array.IndexOf(levelOptions, option); var captured = i;
                var nav = new WrapPanel();
                var back = MakeButton("WorldPrevious", () => { _shown[captured] = levelOptions[index - 1].Id; Update(options, busy); });
                var next = MakeButton("WorldNext", () => { _shown[captured] = levelOptions[index + 1].Id; Update(options, busy); });
                back.IsEnabled = index > 0; next.IsEnabled = index + 1 < levelOptions.Length;
                nav.Children.Add(back); nav.Children.Add(next); slot.Children.Add(nav);
            }
            if (option?.Status == "ready" && option.Answer is { } answer)
            {
                var p = VideoWorldProtocol.Read(answer.Text); var title = Label(p.Title); title.FontWeight = FontWeights.Bold; slot.Children.Add(title);
                slot.Children.Add(Label(p.Summary));
                var choose = MakeButton("WorldChoose", () => ChooseRequested?.Invoke(option.Id));
                AutomationProperties.SetAutomationId(choose, "VideoPrep.WorldChoose." + level); slot.Children.Add(choose);
                foreach (var card in p.Cards)
                {
                    var name = Label(card.Name); name.FontWeight = FontWeights.SemiBold; slot.Children.Add(name); slot.Children.Add(Label(card.Description));
                    if (card.Fixed.Length > 0) slot.Children.Add(Label(T("WorldFixed") + ": " + card.Fixed));
                }
            }
            else
            {
                slot.Children.Add(Label(T(option?.Status == "running" && _paused ? "AiPaused" : "WorldStatus." + (option?.Status ?? "pending"))));
                if (option?.Status == "running")
                {
                    var progress = new System.Windows.Controls.ProgressBar { IsIndeterminate = !_paused, Height = 6 };
                    progress.SetResourceReference(ForegroundProperty, "AccentBrush"); slot.Children.Add(progress);
                }
            }
            if (!busy && option is not null)
            {
                var again = MakeButton("WorldRegenerate", () => RegenerateRequested?.Invoke(option.Id));
                AutomationProperties.SetAutomationId(again, "VideoPrep.WorldRegenerate." + level); slot.Children.Add(again);
            }
        }
    }
    public void Accept(string id) { SelectedOptionId = id; DialogResult = true; }
    public void SetPaused(bool paused) { _paused = paused; Update(_current, _busy); }
}
