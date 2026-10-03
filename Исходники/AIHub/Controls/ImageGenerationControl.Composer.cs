using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private FrameworkElement Composer()
    {
        var frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Height = _inputHeight, Margin = new Thickness(0, 12, 0, 6), Padding = new Thickness(12, 12, 8, 8) };
        frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
        _promptBox = new TextBox { Text = _prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0), IsEnabled = !_busy };
        _promptBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        LiterarySpellChecking.Enable(_promptBox, _languageCode);
        AutomationProperties.SetAutomationId(_promptBox, "Generation.Prompt");
        var placeholder = Text(L("PromptHint")); placeholder.IsHitTestVisible = false; placeholder.Margin = new Thickness(0); placeholder.Opacity = .55;
        placeholder.Visibility = _prompt.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _promptBox.TextChanged += (_, _) => placeholder.Visibility = _promptBox?.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _promptBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            e.Handled = true; await TryAction(SendAsync);
        };
        grid.Children.Add(_promptBox); grid.Children.Add(placeholder);
        var send = Icon("Send", "➤", SendAsync, "Send", !_busy && !HasPendingGeneration());
        send.Padding = new Thickness(4); send.Margin = new Thickness(0); send.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(send, 1); grid.Children.Add(send);
        var resize = new Thumb { Width = 22, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.SizeNS,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = L("ResizeInput") };
        resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = ResizeHandle() };
        AutomationProperties.SetAutomationId(resize, "Generation.ResizeInput"); Grid.SetColumn(resize, 1); grid.Children.Add(resize);
        resize.DragDelta += (_, e) => { _inputHeight = Math.Clamp(_inputHeight - e.VerticalChange, 80, 260); frame.Height = _inputHeight; };
        frame.Child = grid; return frame;
    }

    private static FrameworkElementFactory ResizeHandle()
    {
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.TextProperty, "╱╱");
        label.SetValue(TextBlock.FontSizeProperty, 14d); label.SetValue(TextBlock.OpacityProperty, .6);
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return label;
    }

    private FrameworkElement ComposerToolbar()
    {
        var toolbar = new WrapPanel(); var enabled = !_busy && !HasPendingGeneration();
        toolbar.Children.Add(Icon("AttachLater", "📎", () => Task.CompletedTask, "Attach", false));
        var ratio = Icon("AspectRatio", ImageGenerationDimensions.Ratio(_width, _height), () => Task.CompletedTask, "AspectRatio", enabled);
        ratio.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var item in ImageGenerationDimensions.Ratios)
            {
                var option = new MenuItem { Header = item.Width + ":" + item.Height };
                option.Click += (_, _) => { (_width, _height) = ImageGenerationDimensions.Fit(item.Width, item.Height, Math.Max(_width, _height), ImageGenerationCatalog.Get(_modelId).MaximumSide); Render(); };
                menu.Items.Add(option);
            }
            ShowMenu(ratio, menu);
        }; toolbar.Children.Add(ratio);
        var size = Icon("Resolution", ImageGenerationDimensions.Quality(Math.Max(_width, _height)), () => Task.CompletedTask, "Resolution", enabled);
        size.ToolTip = L("Resolution") + " · " + _width + "×" + _height;
        size.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var value in new[] { 512, 1024, 1536, 2048, 3072, 4096 }.Where(n => n <= ImageGenerationCatalog.Get(_modelId).MaximumSide))
            {
                var dims = ImageGenerationDimensions.Fit(_width, _height, value, ImageGenerationCatalog.Get(_modelId).MaximumSide);
                var option = new MenuItem { Header = ImageGenerationDimensions.Quality(value) + " · " + dims.Width + "×" + dims.Height };
                option.Click += (_, _) => { (_width, _height) = dims; Render(); }; menu.Items.Add(option);
            }
            var custom = new MenuItem { Header = L("CustomSize") }; custom.Click += (_, _) => CustomSize(); menu.Items.Add(custom); ShowMenu(size, menu);
        }; toolbar.Children.Add(size);
        toolbar.Children.Add(Icon("EnhanceLater", "✧", () => Task.CompletedTask, "Enhance", false));
        toolbar.Children.Add(Icon("Another", "↻", RepeatSelectedAsync, "Another.0", enabled && SelectedRequest() is { } request && ImageGenerationCatalog.IsAvailable(request.ModelId)));
        var variants = Icon("Variants", "×" + _count, () => Task.CompletedTask, "Variants", enabled);
        variants.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var n in new[] { 1, 2, 3, 4 }) { var option = new MenuItem { Header = "×" + n }; option.Click += (_, _) => { _count = n; Render(); }; menu.Items.Add(option); }
            ShowMenu(variants, menu);
        }; toolbar.Children.Add(variants); return toolbar;
    }

    private void CustomSize()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = new Window { Owner = Window.GetWindow(this), Title = L("CustomSize"), Content = panel,
            Width = 350, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(Window.BackgroundProperty, "PanelBrush"); window.Resources.MergedDictionaries.Add(Resources.MergedDictionaries[0]);
        panel.Children.Add(Text(L("Width"))); var width = new TextBox { Text = _width.ToString() }; panel.Children.Add(width);
        panel.Children.Add(Text(L("Height"))); var height = new TextBox { Text = _height.ToString() }; panel.Children.Add(height);
        var hint = Text(L("SizeHint")); panel.Children.Add(hint);
        panel.Children.Add(Button("ApplySize", () =>
        {
            var maximum = ImageGenerationCatalog.Get(_modelId).MaximumSide;
            if (!int.TryParse(width.Text, out var w) || !int.TryParse(height.Text, out var h) || w < 256 || h < 256 || w > maximum || h > maximum || w % 64 != 0 || h % 64 != 0)
            { hint.Text = L("InvalidSize") + " · 256–" + maximum; return Task.CompletedTask; }
            (_width, _height) = (w, h); window.DialogResult = true; return Task.CompletedTask;
        }, "ApplySize"));
        if (window.ShowDialog() == true) Render();
    }

    private Button Icon(string key, string symbol, Func<Task> action, string id, bool enabled = true)
    {
        var button = Button(key, action, id, enabled); button.Content = symbol; button.ToolTip = L(key);
        button.FontSize = 18; button.MinWidth = 38; button.Padding = new Thickness(9, 7, 9, 7);
        ToolTipService.SetShowOnDisabled(button, true); AutomationProperties.SetName(button, L(key)); return button;
    }
    private static void ShowMenu(FrameworkElement target, ContextMenu menu)
    { menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; }
    private async Task TryAction(Func<Task> action) { try { await action(); } catch (Exception error) { Error(error); } }
}
