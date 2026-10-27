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

/// <summary>Only selection happens here. Model work starts on the existing canvas after the owned dialog closes.</summary>
public sealed class VideoConceptOptionsWindow : Window
{
    public string SelectedModelId => (string?)_models.SelectedValue ?? "";
    public int Freedom => _freedom.SelectedIndex + 1;
    private readonly ComboBox _models;
    private readonly ComboBox _freedom;

    public VideoConceptOptionsWindow(Window? owner, Func<string, string> localize,
        IReadOnlyList<VideoConceptChoice> choices, string selectedId, int freedom, bool translation = false, bool development = false)
    {
        string T(string key) => localize("VideoPrep." + key);
        Owner = owner; Title = T(translation ? "Translate" : development ? "AiDevelop" : "AiToggle"); ShowInTaskbar = false;
        Width = Math.Min(560, Math.Max(360, (owner?.ActualWidth ?? 700) - 48)); MinWidth = 320;
        MaxHeight = Math.Max(320, (owner?.ActualHeight ?? SystemParameters.WorkArea.Height) - 48);
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "LineBrush", "TextPrimaryBrush", "TextSecondaryBrush",
            "AccentBrush", "SecondaryButtonBackgroundBrush", "StepBadgeBrush", "UiBodyFontSize", "UiSectionFontSize" })
            if (owner?.TryFindResource(key) is { } value) Resources[key] = value;
        if (!Resources.Contains("UiBodyFontSize")) Resources["UiBodyFontSize"] = 14d;
        foreach (var (key, color) in new[] { ("WindowBackgroundBrush", Colors.White), ("PanelBrush", Colors.White),
            ("LineBrush", Colors.Gray), ("TextPrimaryBrush", Colors.Black), ("TextSecondaryBrush", Colors.DimGray),
            ("AccentBrush", Colors.RoyalBlue), ("SecondaryButtonBackgroundBrush", Colors.WhiteSmoke), ("StepBadgeBrush", Colors.LightBlue) })
            if (!Resources.Contains(key)) Resources[key] = new SolidColorBrush(color);
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        UseLayoutRounding = true;
        AutomationProperties.SetAutomationId(this, "VideoPrep.AiOptions");
        SourceInitialized += (_, _) => WindowTitleBarThemeService.Apply(this,
            Resources["WindowBackgroundBrush"] is SolidColorBrush brush && brush.Color.R < 128);
        TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) };
        Button MakeButton(string key, Action action)
        {
            var button = MusicWishUi.Button(T(key), "VideoPrep." + key, action); button.Padding = new Thickness(12, 8, 12, 8);
            if (button.Content is TextBlock text) text.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("Foreground") { Source = button });
            return button;
        }
        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        var footer = new System.Windows.Controls.WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = MakeButton("AiDialogCancel", () => DialogResult = false); cancel.IsCancel = true;
        var run = MakeButton(translation ? "Translate" : development ? "AiDevelop" : "AiImprove", () => DialogResult = true); run.IsDefault = true;
        run.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle");
        footer.Children.Add(cancel); footer.Children.Add(run); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); body.Children.Add(Label(T("AiModel")));
        _models = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = selectedId, MinHeight = 36 };
        AutomationProperties.SetAutomationId(_models, "VideoPrep.AiModel"); AutomationProperties.SetName(_models, T("AiModel"));
        var note = Label(""); note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        void UpdateModel()
        {
            var selected = choices.FirstOrDefault(c => c.Id == SelectedModelId);
            note.Text = T(selected?.Placement ?? "AiHardwareUnknown");
            run.IsEnabled = selected?.Model is not null && selected.Fits != false;
        }
        _models.SelectionChanged += (_, _) => UpdateModel(); UpdateModel();
        body.Children.Add(_models); body.Children.Add(note);
        _freedom = new ComboBox { ItemsSource = Enumerable.Range(1, 3).Select(n => T("AiFreedom." + n)).ToArray(), SelectedIndex = freedom - 1, MinHeight = 36 };
        AutomationProperties.SetAutomationId(_freedom, "VideoPrep.AiFreedom"); AutomationProperties.SetName(_freedom, T("AiFreedom"));
        var hint = Label(T("AiFreedomHint." + freedom));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _freedom.SelectionChanged += (_, _) => hint.Text = T("AiFreedomHint." + Freedom);
        if (translation) body.Children.Add(Label(T("TranslationPolicy")));
        else { body.Children.Add(Label(T("AiFreedom"))); body.Children.Add(_freedom); body.Children.Add(hint); }
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Content = root;
    }
}
