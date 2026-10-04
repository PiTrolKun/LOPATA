using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Models;
using ListBox = System.Windows.Controls.ListBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RichTextBox = System.Windows.Controls.RichTextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SelectionMode = System.Windows.Controls.SelectionMode;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Binding = System.Windows.Data.Binding;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl
{
    private void Render()
    {
        var grid = new Grid { Margin = new(8) };
        grid.ColumnDefinitions.Add(new() { Width = new(230), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 360 });
        grid.ColumnDefinitions.Add(new() { Width = new(54) });
        grid.ColumnDefinitions.Add(new() { Width = new(10) });
        var infoColumn = new ColumnDefinition { Width = new(Math.Clamp(_preferences.InformationWidth, 230, 650)), MinWidth = 230 };
        grid.ColumnDefinitions.Add(infoColumn);
        var settingsRow = new RowDefinition { Height = new(Math.Clamp(_preferences.SettingsHeight, 160, 600)), MinHeight = 160 };
        grid.RowDefinitions.Add(settingsRow);
        grid.RowDefinitions.Add(new() { Height = new(12) });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 220 });
        var sources = ImageUtilityUi.Card(BuildSources()); Grid.SetRowSpan(sources, 3); grid.Children.Add(sources);

        var settings = new Grid(); settings.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 280 });
        settings.ColumnDefinitions.Add(new() { Width = new(1.45, GridUnitType.Star) });
        _primaryPanel = new StackPanel(); RenderPrimarySettings(_primaryPanel);
        // Keep the scrollbar gutter reserved: wrapping must not change its own available width.
        var primary = new ScrollViewer { Content = _primaryPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, Margin = new(0, 0, 18, 0) };
        settings.Children.Add(primary); _settingsPanel = new StackPanel(); RenderMethodSettings();
        var methodSettings = new ScrollViewer { Content = _settingsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        Grid.SetColumn(methodSettings, 1); settings.Children.Add(methodSettings);
        var top = ImageUtilityUi.Card(settings); Grid.SetColumn(top, 1); Grid.SetColumnSpan(top, 4); grid.Children.Add(top);

        var queue = new DockPanel(); var queueHeading = ImageUtilityUi.Text(L("Queue"), true);
        DockPanel.SetDock(queueHeading, Dock.Top); queue.Children.Add(queueHeading);
        var hint = ImageUtilityUi.Text(L("DropHint")); DockPanel.SetDock(hint, Dock.Bottom); queue.Children.Add(hint);
        _list = new ListBox { ItemsSource = _rows, SelectionMode = SelectionMode.Extended, BorderThickness = new(0), AllowDrop = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemTemplate = RowTemplate() };
        _list.SetResourceReference(BackgroundProperty, "PanelBrush");
        VirtualizingPanel.SetIsVirtualizing(_list, true); VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(_list, "ImageUtility.Queue");
        _list.DragOver += QueueDragOver; _list.Drop += QueueDrop;
        _list.ContextMenuOpening += QueueContextMenuOpening;
        _list.ContextMenu = new ContextMenu();
        _list.PreviewMouseRightButtonDown += (_, e) =>
        {
            var value = e.OriginalSource as DependencyObject;
            while (value is not null && value is not ListBoxItem) value = VisualTreeHelper.GetParent(value);
            if (value is ListBoxItem row && !row.IsSelected) { _list.SelectedItems.Clear(); row.IsSelected = true; }
        };
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is ImageUtilityRow row) OpenItem(row.Item); };
        queue.Children.Add(_list); var queueCard = ImageUtilityUi.Card(queue);
        Grid.SetColumn(queueCard, 1); Grid.SetRow(queueCard, 2); grid.Children.Add(queueCard);

        var information = BuildInformation(); Grid.SetColumn(information, 4); Grid.SetRow(information, 2); grid.Children.Add(information);
        var actions = new StackPanel { Margin = new(2, 8, 2, 5) };
        _start = ActionButton("Start", StartAsync); _pause = ActionButton("Pause", PauseResumeAsync); _stop = ActionButton("Stop", StopAsync);
        _retry = ActionButton("RetryFailed", RetryFailedAsync);
        actions.Children.Add(_start); actions.Children.Add(_pause); actions.Children.Add(_stop); actions.Children.Add(_retry);
        actions.Children.Add(ActionButton("RemoveSelected", RemoveSelected)); actions.Children.Add(ActionButton("ClearQueue", ClearQueue));
        var glyphs = new[] { "▶", "⏸", "■", "↻", "−", "⌫" };
        for (var index = 0; index < actions.Children.Count; index++)
        {
            var button = (System.Windows.Controls.Button)actions.Children[index];
            button.ToolTip = button.Content; AutomationProperties.SetName(button, button.Content.ToString());
            button.Content = glyphs[index]; button.Width = button.Height = 42; button.Padding = new(0); button.FontSize = 21;
        }
        Grid.SetColumn(actions, 2); Grid.SetRow(actions, 2); grid.Children.Add(actions);
        var horizontal = new GridSplitter { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(horizontal, 1); Grid.SetColumn(horizontal, 1); Grid.SetColumnSpan(horizontal, 4); grid.Children.Add(horizontal);
        horizontal.SetResourceReference(BackgroundProperty, "LineBrush"); horizontal.Opacity = 0.35;
        var vertical = new System.Windows.Controls.Primitives.Thumb { Width = 8, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, Cursor = System.Windows.Input.Cursors.SizeWE, ToolTip = L("ResizeHint") };
        Grid.SetRow(vertical, 2); Grid.SetColumn(vertical, 3); grid.Children.Add(vertical);
        vertical.SetResourceReference(BackgroundProperty, "LineBrush"); vertical.Opacity = 0.35;
        horizontal.DragCompleted += (_, _) => { _preferences.SettingsHeight = settingsRow.ActualHeight; SavePreferences(); };
        vertical.DragDelta += (_, e) => { infoColumn.Width = new(Math.Clamp(infoColumn.ActualWidth - e.HorizontalChange, 230, Math.Max(230, grid.ActualWidth - 654))); };
        vertical.DragCompleted += (_, _) => { _preferences.InformationWidth = infoColumn.ActualWidth; SavePreferences(); };
        var grip = new System.Windows.Controls.Primitives.Thumb { Width = 26, Height = 26, Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = L("ResizeHint"), Template = ResizeGripTemplate() };
        Grid.SetRow(grip, 1); Grid.SetRowSpan(grip, 2); Grid.SetColumn(grip, 3); grip.VerticalAlignment = VerticalAlignment.Top; grid.Children.Add(grip);
        grip.DragDelta += (_, e) =>
        {
            var availableHeight = Math.Max(160, grid.ActualHeight - 240);
            settingsRow.Height = new(Math.Clamp(settingsRow.ActualHeight + e.VerticalChange, 160, availableHeight));
            var availableWidth = Math.Max(230, grid.ActualWidth - 230 - 360 - 64);
            infoColumn.Width = new(Math.Clamp(infoColumn.ActualWidth - e.HorizontalChange, 230, availableWidth));
        };
        grip.DragCompleted += (_, _) => { _preferences.SettingsHeight = settingsRow.ActualHeight; _preferences.InformationWidth = infoColumn.ActualWidth; SavePreferences(); };
        grid.SizeChanged += (_, _) =>
        {
            if (grid.ActualHeight > 0) settingsRow.Height = new(Math.Clamp(settingsRow.Height.Value, 160, Math.Max(160, grid.ActualHeight - 240)));
            if (grid.ActualWidth > 0) infoColumn.Width = new(Math.Clamp(infoColumn.Width.Value, 230, Math.Max(230, grid.ActualWidth - 654)));
        };
        Content = grid; RefreshRows();
    }
    private static ControlTemplate ResizeGripTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.TextProperty, "↔\n↕");
        text.SetValue(TextBlock.FontSizeProperty, 14d); text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center); text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = text };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.65)); template.Triggers.Add(hover); return template;
    }
    private UIElement BuildInformation()
    {
        var panel = new DockPanel();
        var statusPanel = new StackPanel(); _walker = new ImageUtilityWalker(); statusPanel.Children.Add(_walker);
        _status = ImageUtilityUi.Text(L("Ready")); statusPanel.Children.Add(_status);
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, Margin = new(0, 8, 0, 8) }; statusPanel.Children.Add(_progress);
        _aiStatus = ImageUtilityUi.Text(""); statusPanel.Children.Add(_aiStatus);
        var outputs = ActionButton("OpenOutput", OpenOutput); statusPanel.Children.Add(outputs);
        statusPanel.Children.Add(ImageUtilityUi.Text(L("Events"), true));
        DockPanel.SetDock(statusPanel, Dock.Top); panel.Children.Add(statusPanel);
        _terminal = new RichTextBox { IsReadOnly = true, IsDocumentEnabled = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new("Consolas"), FontSize = 12, Background = new SolidColorBrush(Color.FromRgb(14, 17, 21)),
            Foreground = Brushes.LightGreen, BorderThickness = new(0), Padding = new(8), MinHeight = 90,
            Document = new FlowDocument { PagePadding = new(0) } };
        AutomationProperties.SetAutomationId(_terminal, "ImageUtility.Events");
        foreach (var entry in _events) _terminal.Document.Blocks.Add(new Paragraph(new Run(entry.Text))
            { Foreground = entry.Error ? Brushes.LightCoral : entry.Success ? Brushes.LightGreen : Brushes.Gainsboro, Margin = new(0, 0, 0, 4) });
        panel.Children.Add(_terminal); return ImageUtilityUi.Card(panel);
    }
    private static DataTemplate RowTemplate()
    {
        var template = new DataTemplate(typeof(ImageUtilityRow));
        var grid = new FrameworkElementFactory(typeof(DockPanel)); grid.SetValue(MarginProperty, new Thickness(4, 8, 4, 8));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(WidthProperty, 48d); image.SetValue(HeightProperty, 48d);
        image.SetValue(MarginProperty, new Thickness(0, 2, 12, 2)); image.SetValue(DockPanel.DockProperty, Dock.Left);
        image.SetValue(Image.StretchProperty, Stretch.Uniform); image.SetBinding(Image.SourceProperty, new Binding(nameof(ImageUtilityRow.Thumbnail))); grid.AppendChild(image);
        var texts = new FrameworkElementFactory(typeof(StackPanel));
        Add(nameof(ImageUtilityRow.Name), 15, true); Add(nameof(ImageUtilityRow.Source), 12, false); Add(nameof(ImageUtilityRow.Dimensions), 12, false);
        var state = new FrameworkElementFactory(typeof(TextBlock)); state.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImageUtilityRow.Status)));
        state.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ImageUtilityRow.StatusBrush))); state.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        state.SetValue(TextBlock.FontSizeProperty, 12d); texts.AppendChild(state); grid.AppendChild(texts); template.VisualTree = grid; return template;
        void Add(string binding, double size, bool bold)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(binding));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(TextBlock.FontSizeProperty, size);
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            text.SetValue(TextBlock.FontWeightProperty, bold ? FontWeights.SemiBold : FontWeights.Normal);
            text.SetResourceReference(TextBlock.ForegroundProperty, bold ? "TextPrimaryBrush" : "TextSecondaryBrush"); texts.AppendChild(text);
        }
    }
    private void RemoveSelected()
    {
        if (IsBusy || HasPendingOperation() || _list is null) return;
        var ids = _list.SelectedItems.Cast<ImageUtilityRow>().Select(x => x.Item.Id).ToHashSet();
        _job.Items.RemoveAll(x => ids.Contains(x.Id)); ReclassifyDuplicates(); RefreshRows(true);
    }
    private void ClearQueue()
    {
        if (IsBusy || HasPendingOperation()) return;
        _job = new() { Options = Clone(Options) }; RefreshRows(true);
    }
    private void ReclassifyDuplicates()
    {
        Services.ImageUtilitySources.RecheckDuplicates(_job);
    }
}
