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
        grid.ColumnDefinitions.Add(new() { Width = new(310), MinWidth = 230 });
        grid.RowDefinitions.Add(new() { Height = new(320), MinHeight = 230 });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 220 });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var sources = ImageUtilityUi.Card(BuildSources()); Grid.SetRowSpan(sources, 3); grid.Children.Add(sources);

        var settings = new Grid(); settings.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 280 });
        settings.ColumnDefinitions.Add(new() { Width = new(1.45, GridUnitType.Star) });
        _primaryPanel = new StackPanel(); RenderPrimarySettings(_primaryPanel);
        var primary = new ScrollViewer { Content = _primaryPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 18, 0) };
        settings.Children.Add(primary); _settingsPanel = new StackPanel(); RenderMethodSettings();
        var methodSettings = new ScrollViewer { Content = _settingsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(methodSettings, 1); settings.Children.Add(methodSettings);
        var top = ImageUtilityUi.Card(settings); Grid.SetColumn(top, 1); Grid.SetColumnSpan(top, 2); grid.Children.Add(top);

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
        Grid.SetColumn(queueCard, 1); Grid.SetRow(queueCard, 1); grid.Children.Add(queueCard);

        var information = BuildInformation(); Grid.SetColumn(information, 2); Grid.SetRow(information, 1); Grid.SetRowSpan(information, 2); grid.Children.Add(information);
        var actions = new WrapPanel { Margin = new(8, 5, 8, 5) };
        _start = ActionButton("Start", StartAsync); _pause = ActionButton("Pause", PauseResumeAsync); _stop = ActionButton("Stop", StopAsync);
        _retry = ActionButton("RetryFailed", RetryFailedAsync);
        actions.Children.Add(_start); actions.Children.Add(_pause); actions.Children.Add(_stop); actions.Children.Add(_retry);
        actions.Children.Add(ActionButton("RemoveSelected", RemoveSelected)); actions.Children.Add(ActionButton("ClearQueue", ClearQueue));
        Grid.SetColumn(actions, 1); Grid.SetRow(actions, 2); grid.Children.Add(actions);
        Content = grid; RefreshRows();
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
