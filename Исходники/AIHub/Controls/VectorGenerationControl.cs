using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

public sealed partial class VectorGenerationControl : UserControl, IDisposable
{
    private Func<string, string> _l = key => key;
    private string _outputRoot = "", _author = "", _selectedId = "", _draft = "", _statusKey = "Ready", _statusDetail = "";
    private readonly VectorWork _work = new();
    private CancellationTokenSource? _cancel;
    private bool _busy;
    private bool _exporting;
    private readonly VectorSession _session = new();
    private VectorGenerationOptions _options = new();
    private Action<VectorGenerationOptions>? _saveOptions;
    public event Action? WorkspaceChanged;
    public bool IsChat => _page == 2;
    public bool CanClearWorkspace => !_busy && !HasPending();
    public void ConfigureOptions(VectorGenerationOptions? options, Action<VectorGenerationOptions> save)
    { if (!_busy) _options = options ?? new(); _saveOptions = save; }
    private TextBox? _input;
    private TextBlock? _status;
    private long _lastProgress;
    public VectorGenerationControl()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Vector.Page");
    }
    public void Configure(Func<string, string> localize, StorageSettings storage, string? author)
    {
        _l = localize; _author = author ?? "";
        if (!_busy) _outputRoot = Path.Combine(storage.Results.Locations.Select(x => x.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? AppDataPaths.BaseDirectory, "VectorGraphics");
        Render();
    }
    public void Localize(Func<string, string> localize) { _l = localize; Render(); }
    private string L(string key) => _l("Vector." + key);
    private TextBlock Text(string value, bool title = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = title ? 23 : 16,
            FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 6, 0, 6) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return text;
    }
    private Button Action(string label, string symbol, Func<Task> action, string id, bool enabled = true)
    {
        var button = new Button { Content = symbol, ToolTip = L(label), IsEnabled = enabled,
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 6, 4), MinWidth = 38 };
        AutomationProperties.SetAutomationId(button, "Vector." + id); AutomationProperties.SetName(button, L(label));
        button.Click += async (_, _) => { try { await action(); } catch (Exception e) { ShowError(e); } };
        return button;
    }
    private void Render()
    {
        CloseLiveView();
        WorkspaceChanged?.Invoke();
        _status = null; _preparationProgress = null;
        if (_page == 0) { RenderSelection(); return; }
        if (_page == 1) { RenderPreparation(); return; }
        var history = _session.Visible().Where(x => x.Request.ModelId == _modelId).ToArray();
        var selected = history.FirstOrDefault(x => x.Request.Id == _selectedId) ?? history.LastOrDefault();
        if (selected is not null) _selectedId = selected.Request.Id;
        var root = new Grid { Margin = new Thickness(12) }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var title = Text(L("Title") + " · " + VectorModels.Name(_modelId), true); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new(0, 0, 0, 18); root.Children.Add(title);
        var work = new Grid(); Grid.SetRow(work, 1); root.Children.Add(work);
        work.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        work.ColumnDefinitions.Add(new() { Width = new GridLength(1.65, GridUnitType.Star), MinWidth = 220 });
        work.ColumnDefinitions.Add(new() { Width = new GridLength(.42, GridUnitType.Star), MinWidth = 90, MaxWidth = 185 });
        var left = new Grid { Margin = new(0, 0, 16, 0) }; left.RowDefinitions.Add(new()); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); work.Children.Add(left);
        var prompts = new StackPanel();
        foreach (var item in history)
        {
            var card = new Border { CornerRadius = new(10), Padding = new(12), Margin = new(0, 0, 0, 10), BorderThickness = new(1), Cursor = Cursors.Hand };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, item == selected ? "AccentBrush" : "LineBrush");
            var panel = new StackPanel(); var label = Text("SVG · " + item.Request.CreatedUtc.ToLocalTime().ToString("g")); label.FontSize = 12; panel.Children.Add(label);
            var prompt = new TextBox { Text = item.Request.Prompt, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0), FontSize = 16 };
            prompt.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush"); panel.Children.Add(prompt); card.Child = panel;
            card.MouseLeftButtonUp += (_, _) => { if (_selectedId != item.Request.Id) { _selectedId = item.Request.Id; Render(); } };
            AutomationProperties.SetAutomationId(card, "Vector.History." + item.Request.Id); prompts.Children.Add(card);
        }
        var scroll = new ScrollViewer { Content = prompts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; left.Children.Add(scroll);
        var composer = new Border { CornerRadius = new(12), Padding = new(12), BorderThickness = new(1), Margin = new(0, 10, 0, 0) };
        composer.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); composer.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var inputGrid = new Grid(); inputGrid.ColumnDefinitions.Add(new()); inputGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        const string promptKey = "Prompt";
        _input = new TextBox { Text = _draft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 122, MinHeight = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, BorderThickness = new(0), FontSize = 16,
            ToolTip = L(promptKey), IsReadOnly = _busy };
        _input.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush"); _input.TextChanged += (_, _) => _draft = _input.Text;
        AutomationProperties.SetAutomationId(_input, "Vector.Prompt"); AutomationProperties.SetName(_input, L(promptKey));
        _input.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; try { await SendAsync(); } catch (Exception error) { ShowError(error); } } };
        inputGrid.Children.Add(_input);
        var hint = Text(L(promptKey)); hint.IsHitTestVisible = false; hint.Opacity = .55; hint.VerticalAlignment = VerticalAlignment.Top; hint.Visibility = _draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed; inputGrid.Children.Add(hint);
        _input.TextChanged += (_, _) => hint.Visibility = string.IsNullOrEmpty(_draft) ? Visibility.Visible : Visibility.Collapsed; var send = Action("Send", "➤", SendAsync, "Send", !_busy && !HasPending()); Grid.SetColumn(send, 1); send.VerticalAlignment = VerticalAlignment.Bottom; inputGrid.Children.Add(send); composer.Child = inputGrid; Grid.SetRow(composer, 1); left.Children.Add(composer);
        var lower = new StackPanel(); Grid.SetRow(lower, 2); left.Children.Add(lower);
        lower.Children.Add(SettingsToolbar());
        _status = Text(string.IsNullOrEmpty(_statusDetail) ? L(_statusKey) : _statusDetail); _status.FontSize = 12; lower.Children.Add(_status);
        var center = new Grid { Margin = new(0, 0, 16, 0) }; center.RowDefinitions.Add(new()); center.RowDefinitions.Add(new() { Height = GridLength.Auto }); Grid.SetColumn(center, 1); work.Children.Add(center);
        var preview = selected is null ? null : VectorStore.FilePath(selected.Request.Id, "preview.png");
        if (ShowLive(center, selected)) { }
        else if (preview is not null && File.Exists(preview))
        {
            var image = new Image { Source = ReadImage(preview), Stretch = Stretch.Uniform }; AutomationProperties.SetAutomationId(image, "Vector.Preview");
            image.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) OpenLarge(selected!); }; center.Children.Add(PreviewSurface(image));
        }
        else
        {
            var empty = Text(selected?.Error is { } error ? TranslateError(error) : L(_busy ? "Generating" : "Empty")); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; center.Children.Add(empty);
        }
        var footer = new StackPanel(); Grid.SetRow(footer, 1); center.Children.Add(footer);
        if (_busy) footer.Children.Add(new ProgressBar { Height = 5, IsIndeterminate = true, Margin = new(0, 10, 0, 6) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center }; footer.Children.Add(actions);
        if (selected is not null && File.Exists(VectorStore.FilePath(selected.Request.Id, "image.svg")))
        {
            actions.Children.Add(Action("Open", "↗", () => { OpenLarge(selected); return Task.CompletedTask; }, "Open"));
            actions.Children.Add(Action("Copy", "▣", () => CopySvg(selected), "Copy"));
            actions.Children.Add(Action("Save", "⇩", () => SaveSvg(selected), "Save"));
            actions.Children.Add(Action("Export.Title", "▧", () => ExportRasterAsync(selected), "Export", !_busy && !HasPending()));
            actions.Children.Add(Action("Folder", "▱", () => ShowFolder(selected), "Folder"));
        }
        if (_busy || HasPending())
        {
            if (!_exporting) actions.Children.Add(Action("PauseResume", "Ⅱ / ▶", PauseResumeAsync, "PauseResume"));
            actions.Children.Add(Action("Cancel", "■", CancelAsync, "Cancel"));
        }
        if (selected?.Phase == "failed") actions.Children.Add(Action("Retry", "↻", () => RetryAsync(selected), "Retry", !_busy && !HasPending()));
        if (selected?.ExportPath is { } saved) { var caption = Text(L("AutoSaved")); caption.FontSize = 12; caption.ToolTip = saved; footer.Children.Add(caption); }
        if (selected is { SyntaxCorrections: > 0 }) { var note = Text(L("SyntaxCorrected") + " " + selected.SyntaxCorrections); note.FontSize = 12; footer.Children.Add(note); }
        var thumbs = new StackPanel();
        foreach (var item in history.Reverse())
        {
            var path = VectorStore.FilePath(item.Request.Id, "preview.png"); if (!File.Exists(path)) continue;
            var button = new Button { Content = PreviewSurface(new Image { Source = ReadImage(path, 180), Stretch = Stretch.Uniform, MaxHeight = 155 }), Padding = new(3), Margin = new(0, 0, 0, 10), ToolTip = item.Request.Prompt };
            button.Click += (_, _) => { _selectedId = item.Request.Id; Render(); }; thumbs.Children.Add(button);
        }
        var right = new ScrollViewer { Content = thumbs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(right, 2); work.Children.Add(right); Content = root;
    }
    private static BitmapSource ReadImage(string path, int width = 0)
    {
        using var stream = File.OpenRead(path); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) image.DecodePixelWidth = width; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private static Border PreviewSurface(Image image)
    {
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        tile.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        tile.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(10, 10, 10, 10))));
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 20, 20), Stretch = Stretch.None };
        brush.Freeze(); return new Border { Background = brush, Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }
    public void Dispose() { CloseLiveView(); EndLive(); _cancel?.Cancel(); _work.Dispose(); }
}
