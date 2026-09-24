using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportQuestionsControl
{
    private CancellationTokenSource? _chapterPreviewCancellation;
    private void AddChapterLinkColumn()
    {
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(ContentControl.ContentProperty, "↗");
        button.SetValue(ToolTipProperty, T("RouteOpenChapter"));
        button.SetValue(MarginProperty, new Thickness(3));
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.SetValue(System.Windows.Controls.Control.PaddingProperty, new Thickness(4, 1, 4, 1));
        button.SetValue(System.Windows.Controls.Control.MinWidthProperty, 24d);
        button.SetValue(System.Windows.Controls.Control.MinHeightProperty, 24d);
        button.SetBinding(IsEnabledProperty, new Binding("HasSource"));
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, e) =>
        {
            if (((FrameworkElement)sender).DataContext is ImportRouteRow row) _ = OpenChapterAsync(row);
        }));
        _routeGrid!.Columns.Add(new DataGridTemplateColumn { Header = T("RouteBook"), Width = 65,
            CellTemplate = new DataTemplate { VisualTree = button }, IsReadOnly = true });
    }

    private async Task OpenChapterAsync(ImportRouteRow row)
    {
        if (_chapterPreviewCancellation is not null || !TrySaveDraft()) return;
        using var cancellation = new CancellationTokenSource(); _chapterPreviewCancellation = cancellation;
        var draft = _route;
        try
        {
            var source = await ImportChapterOutline.ReadAsync(_projectRoot, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(draft, _route)) return;
            var chapter = ImportChapterOutline.LinkedText(source, row);
            if (chapter is null) { _outlineStatus!.Text = T("RouteLinkStale"); return; }
            var preview = new Window { Width = 950, Height = 720, MinWidth = 500, MinHeight = 350 };
            LiteraryPromptDialogUi.Configure(preview, Window.GetWindow(this), T("RouteOpenChapter"));
            var panel = new DockPanel { Margin = new Thickness(16) }; preview.Content = panel;
            panel.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "PanelBrush");
            var label = LiteraryUi.Text(row.Title + "\n" + T("RoutePreviewHint"));
            DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
            var text = new TextBox { Text = chapter, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 18, Padding = new Thickness(16) };
            text.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
            text.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            preview.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/AIHub;component/Controls/LiteraryBookScrollResources.xaml", UriKind.Relative) });
            panel.Children.Add(text); preview.ShowDialog();
        }
        catch (Exception) { if (!_closed && ReferenceEquals(draft, _route)) _outlineStatus!.Text = T("RouteLinkStale"); }
        finally { if (ReferenceEquals(_chapterPreviewCancellation, cancellation)) _chapterPreviewCancellation = null; }
    }
}
