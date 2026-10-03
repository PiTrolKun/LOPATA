using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private string? _selectedTurnId;
    private int _selectedResultIndex;
    private double _inputHeight = 125;
    private readonly HashSet<string> _hiddenTurns = [];
    private ProgressBar? _operationProgress;
    private TextBlock? _operationStatus;
    private System.Windows.Controls.Button? _pauseButton, _stopButton;

    private ImageGenerationTurn[] VisibleTurns() => _sessionDirectory is null ? [] :
        ImageGenerationSessionStore.Load(_sessionDirectory).Turns.Where(t => !_hiddenTurns.Contains(t.Request.Id)).ToArray();

    private void RenderChat()
    {
        _operationProgress = null; _operationStatus = null;
        var turns = VisibleTurns();
        var selected = turns.FirstOrDefault(t => t.Request.Id == _selectedTurnId) ?? turns.LastOrDefault();
        _selectedTurnId = selected?.Request.Id;
        var result = selected?.Results.FirstOrDefault(r => r.Index == _selectedResultIndex) ?? selected?.Results.LastOrDefault();
        if (result is not null) _selectedResultIndex = result.Index;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var title = Text(L("Title") + " · " + ImageGenerationCatalog.DisplayName(_modelId), true);
        title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 0, 0, 18); grid.Children.Add(title);
        var work = new Grid(); Grid.SetRow(work, 1); grid.Children.Add(work);
        work.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        work.ColumnDefinitions.Add(new() { Width = new GridLength(1.65, GridUnitType.Star), MinWidth = 220 });
        work.ColumnDefinitions.Add(new() { Width = new GridLength(.42, GridUnitType.Star), MinWidth = 90, MaxWidth = 185 });
        var left = new Grid { Margin = new Thickness(0, 0, 16, 0) };
        left.RowDefinitions.Add(new()); left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); work.Children.Add(left);
        var prompts = new StackPanel(); TextBox? selectedText = null;
        foreach (var turn in turns)
        {
            var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            card.SetResourceReference(Border.BorderBrushProperty, turn == selected ? "AccentBrush" : "LineBrush");
            var content = new StackPanel();
            var label = Text(ImageGenerationCatalog.DisplayName(turn.Request.ModelId) + " · " + turn.Request.Width + "×" + turn.Request.Height);
            label.FontSize = 12; label.Margin = new Thickness(0, 0, 0, 6); content.Children.Add(label);
            label.Cursor = System.Windows.Input.Cursors.Hand;
            label.MouseLeftButtonDown += (_, _) => { _selectedTurnId = turn.Request.Id; _selectedResultIndex = int.MaxValue; Render(); };
            var text = new TextBox { Text = turn.Request.Prompt, IsReadOnly = true, IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), FontSize = 16 };
            text.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(text, "Generation.SentPrompt." + turn.Request.Id);
            text.PreviewMouseDown += (_, _) => _selectedTurnId = turn.Request.Id;
            text.ContextMenu = PromptMenu(text, turn.Request); content.Children.Add(text); card.Child = content; prompts.Children.Add(card);
            if (turn == selected) selectedText = text;
            if (turn.Request.ModelId == "krea" && turn == turns.FirstOrDefault(t => t.Request.ModelId == "krea")) prompts.Children.Add(KreaNotice());
        }
        var scroll = new ScrollViewer { Content = prompts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "Generation.Prompts"); left.Children.Add(scroll);
        scroll.Loaded += (_, _) => { if (selectedText is not null) selectedText.BringIntoView(); };
        var input = Composer(); Grid.SetRow(input, 1); left.Children.Add(input);
        var toolbar = ComposerToolbar(); Grid.SetRow(toolbar, 2); left.Children.Add(toolbar);
        _status!.FontSize = 12; _status.Margin = new Thickness(0, 2, 0, 0); Grid.SetRow(_status, 3); left.Children.Add(_status);
        var center = new Grid { Margin = new Thickness(0, 0, 16, 0) }; Grid.SetColumn(center, 1); work.Children.Add(center);
        center.RowDefinitions.Add(new()); center.RowDefinitions.Add(new() { Height = GridLength.Auto }); center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        if (selected is not null && result is not null) RenderCurrentImage(center, selected, result);
        else
        { var empty = Text(L(_busy ? "Generating" : "Workspace.Empty")); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; center.Children.Add(empty); }
        var operation = new StackPanel(); Grid.SetRow(operation, 2); center.Children.Add(operation);
        if (_busy)
        {
            var running = ApplicationBackgroundOperations.Current?.State?.Phase is not (BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting);
            _operationProgress = new ProgressBar { Height = 5, IsIndeterminate = running, Margin = new Thickness(0, 10, 0, 5) };
            _operationProgress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush"); operation.Children.Add(_operationProgress);
            _operationStatus = Text(_statusText); operation.Children.Add(_operationStatus);
        }
        var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _pauseButton = Icon("PauseResume", "Ⅱ / ▶", PauseResumeAsync, "PauseResume", HasPendingGeneration());
        _stopButton = Icon("Cancel", "■", CancelAsync, "Cancel", _busy || HasPendingGeneration());
        controls.Children.Add(_pauseButton); controls.Children.Add(_stopButton);
        controls.Visibility = _busy || HasPendingGeneration() ? Visibility.Visible : Visibility.Collapsed; operation.Children.Add(controls);
        var thumbnails = new StackPanel();
        foreach (var turn in turns.Reverse())
        foreach (var old in turn.Results.Reverse())
        {
            if (turn == selected && old.Index == result?.Index) continue;
            var path = ImageGenerationSessionStore.ResultPath(turn.Request, old.Index);
            var thumb = new System.Windows.Controls.Button { Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = ImageGenerationCatalog.DisplayName(turn.Request.ModelId) + " · " + turn.Request.Prompt };
            thumb.Content = File.Exists(path) ? new Image { Source = ReadImage(path, 180), Stretch = Stretch.Uniform, MaxHeight = 155 } : Text(L("MissingImage"));
            AutomationProperties.SetAutomationId(thumb, "Generation.Thumbnail." + turn.Request.Id + "." + old.Index);
            thumb.Click += (_, _) => { _selectedTurnId = turn.Request.Id; _selectedResultIndex = old.Index; Render(); };
            thumb.ContextMenu = ImageMenu(turn, old); thumbnails.Children.Add(thumb);
        }
        var history = new ScrollViewer { Content = thumbnails, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(history, "Generation.Images"); Grid.SetColumn(history, 2); work.Children.Add(history); Content = grid;
    }

    private void RenderCurrentImage(Grid panel, ImageGenerationTurn turn, ImageGenerationResult result)
    {
        var path = ImageGenerationSessionStore.ResultPath(turn.Request, result.Index);
        if (!File.Exists(path)) { panel.Children.Add(Text(L("MissingImage"))); return; }
        var image = new Image { Source = ReadImage(path), Stretch = Stretch.Uniform, ContextMenu = ImageMenu(turn, result) };
        AutomationProperties.SetAutomationId(image, "Generation.CurrentImage"); panel.Children.Add(image);
        image.MouseLeftButtonDown += async (_, e) => { if (e.ClickCount == 2) await TryAction(() => ImageAction("Open", turn, result)); };
        var footer = new StackPanel(); var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (key, symbol) in new[] { ("Open", "↗"), ("Copy", "▣"), ("Save", "⇩") }) actions.Children.Add(Icon(key, symbol, () => ImageAction(key, turn, result), key + "." + result.Index));
        footer.Children.Add(actions);
        if (result.Exported)
        { var saved = Text(L("AutoSaved")); saved.FontSize = 12; saved.ToolTip = result.ExportPath; footer.Children.Add(saved); }
        else if (result.ExportError is not null)
        {
            var error = Text(L("AutoSaveFailed") + " " + result.ExportError); error.FontSize = 12; footer.Children.Add(error);
            footer.Children.Add(Button("RetrySave", () => RetrySaveAsync(turn, result), "RetrySave." + result.Index));
        }
        Grid.SetRow(footer, 1); panel.Children.Add(footer);
    }

    private FrameworkElement KreaNotice()
    {
        var panel = new StackPanel { Margin = new Thickness(6, 4, 6, 12) };
        var notice = Text(L("ReviewHint")); notice.FontSize = 13; panel.Children.Add(notice);
        panel.Children.Add(Button("ReviewPolicy", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.krea.ai/krea-2-use-policy") { UseShellExecute = true }); return Task.CompletedTask; }, "ReviewPolicy")); return panel;
    }

    private static BitmapSource ReadImage(string path, int previewWidth = 0)
    {
        using var file = File.OpenRead(path);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (previewWidth > 0) bitmap.DecodePixelWidth = previewWidth;
        bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}
