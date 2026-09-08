using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Small batch surface inside the existing workspace; model activity and voice remain shared.</summary>
public sealed partial class ImageBatchControl : System.Windows.Controls.UserControl
{
    private readonly Func<string, string> _l;
    private readonly Grid _grid = new();
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly ListBox _list = new();
    private readonly Button _next = new() { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private string _nextAction = "settings";
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    public event Action<string>? Action;
    public ImageBatchItem? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ImageBatchItem;
    public bool AcceptsInput { get; private set; }
    public void SuspendInput() => AcceptsInput = false;
    public ImageBatchControl(Func<string, string> localize)
    {
        _l = localize; Content = _grid; AllowDrop = true;
        _grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _actions.Margin = new Thickness(0, 0, 0, 10); _grid.Children.Add(_actions);
        Grid.SetRow(_list, 1); _grid.Children.Add(_list);
        Grid.SetRow(_status, 2); _grid.Children.Add(_status);
        Grid.SetRow(_next, 3); _grid.Children.Add(_next);
        _next.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        ToolTipService.SetShowOnDisabled(_next, true);
        _next.Click += (_, _) => Action?.Invoke(_nextAction);
        _list.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        InitializeReordering();
        // The visible progress bar belongs to the workspace footer.
        _list.SetResourceReference(BackgroundProperty, "PanelBrush");
        _list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _list.BorderThickness = new Thickness(0);
    }
    private void Add(string key, string action, bool enabled = true)
    {
        var button = new Button { Content = _l(key), Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(8, 4, 8, 4), MinWidth = 0, IsEnabled = enabled };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += (_, _) => Action?.Invoke(action); _actions.Children.Add(button);
    }
    public void Selector(IEnumerable<ImageBatchJob> jobs)
    {
        _next.Visibility = Visibility.Collapsed;
        AcceptsInput = false; _actions.Children.Clear(); _list.Items.Clear();
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        card.SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel();
        var title = new TextBlock { Text = _l("Batch.Title"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 5) };
        title.SetResourceReference(TextBlock.FontSizeProperty, "UiFont18");
        text.Children.Add(title);
        var description = new TextBlock { Text = _l("Batch.Independent"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); text.Children.Add(description); row.Children.Add(text);
        var start = new Button { Content = _l("ImageAnalysis.Workspace.Subscenario.Single.Start"), MinWidth = 160, Height = 40 };
        start.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); start.Click += (_, _) => Action?.Invoke("new");
        Grid.SetColumn(start, 1); row.Children.Add(start); card.Child = row;
        // Selector occupies the full row, rather than a content-width action button.
        _list.Items.Add(card);
        _list.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        _status.Text = _l("Batch.Independent");
    }
    public void Files(ImageBatchJob job)
    {
        AcceptsInput = !job.Started; _actions.Children.Clear();
        Add("Batch.Add", "add", !job.Started);
        _next.Content = _l(job.Started ? "Batch.Resume" : "Batch.Configure");
        _nextAction = job.Started ? "run" : "settings";
        _next.IsEnabled = job.Items.Count > 0; _next.Visibility = Visibility.Visible;
        _next.ToolTip = job.Items.Count == 0 ? _l("Batch.AddAtLeastOne") : null;
        RenderItems(job); _status.Text = _l(job.Started ? "Batch.ResumeHint" : "Batch.DropHint");
        if (job.Items.Count == 0) _status.Text = _l("Batch.AddAtLeastOne") + " " + _status.Text;
    }
    public void Running(ImageBatchJob job)
    {
        _next.Visibility = Visibility.Collapsed;
        AcceptsInput = false; _actions.Children.Clear(); Add("Common.Cancel", "cancel");
        RenderItems(job);
    }
    public void Results(ImageBatchJob job, string report)
    {
        _next.Visibility = Visibility.Collapsed;
        AcceptsInput = false; _actions.Children.Clear();
        bool complete = job.Status == "completed";
        Add("ImageAnalysis.Workspace.Result.Preview", "preview", complete && job.SingleDocument);
        Add(job.SingleDocument ? "ImageAnalysis.Workspace.Result.Export" : "Batch.OpenFolder", job.SingleDocument ? "export" : "folder", complete);
        if (!complete || job.Items.Any(i => i.Status == "error")) Add("Batch.Resume", "run");
        Add(complete ? "ImageAnalysis.Workspace.Result.Complete" : "Batch.Back", "back"); RenderItems(job); _status.Text = report;
    }
    private void RenderItems(ImageBatchJob job)
    {
        var selected = Selected?.Id; _list.Items.Clear();
        foreach (var item in job.Items)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var thumbnail = new Image { Width = 72, Height = 48, Margin = new Thickness(0, 0, 12, 0), ToolTip = item.File.SourcePath };
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 72; bitmap.UriSource = new Uri(item.File.SourcePath); bitmap.EndInit(); bitmap.Freeze(); thumbnail.Source = bitmap;
            }
            catch { /* Missing originals do not prevent reading persisted descriptions. */ }
            row.Children.Add(thumbnail);
            var label = new TextBlock { Text = item.File.DisplayName + "\n" + _l("Batch.Item." + item.Status), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, ToolTip = string.IsNullOrEmpty(item.Error) ? item.File.SourcePath : item.Error };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var entry = new ListBoxItem { Content = row, Tag = item, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3) };
            if (AcceptsInput)
            {
                var remove = new Button { Content = "×", ToolTip = _l("Batch.Remove"), Width = 30, Height = 30, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0) };
                remove.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
                System.Windows.Automation.AutomationProperties.SetName(remove, _l("Batch.Remove"));
                remove.Click += (_, e) => { e.Handled = true; entry.IsSelected = true; Action?.Invoke("remove"); };
                Grid.SetColumn(remove, 2); row.Children.Add(remove);
                AttachReordering(entry, item, job);
            }
            _list.Items.Add(entry); if (selected == item.Id) entry.IsSelected = true;
        }
    }
    public void Progress(ImageBatchProgress p)
    {
        var percent = p.Total == 0 ? 0 : 100 * p.Finished / p.Total;
        _status.Text = string.Format(_l("Batch.Progress"), p.Finished, p.Total, percent, p.Failed) + " · " + _l("Batch.Stage." + p.Stage);
        // Update labels without recreating thumbnails on every generation event.
    }
}
