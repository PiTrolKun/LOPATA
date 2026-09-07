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
public sealed class ImageBatchControl : System.Windows.Controls.UserControl
{
    private readonly Func<string, string> _l;
    private readonly Grid _grid = new();
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly ListBox _list = new();
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
        AcceptsInput = false; _actions.Children.Clear(); _list.Items.Clear();
        Add("Batch.Title", "new");
        _status.Text = _l("Batch.History");
        foreach (var job in jobs.OrderByDescending(j => j.Created))
        {
            var b = new Button { Content = $"{job.Created:g} · {job.Items.Count} · {_l("Batch.State." + job.Status)}", Tag = job, Margin = new Thickness(0, 4, 0, 4) };
            b.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            b.Click += (_, _) => Action?.Invoke("load:" + job.Id); _list.Items.Add(b);
        }
    }
    public void Files(ImageBatchJob job)
    {
        AcceptsInput = !job.Started; _actions.Children.Clear();
        Add("Batch.Add", "add", !job.Started); Add("Batch.Remove", "remove", !job.Started);
        Add("Batch.Up", "up", !job.Started); Add("Batch.Down", "down", !job.Started);
        Add(job.Started ? "Batch.Resume" : "Batch.Configure", job.Started ? "run" : "settings", job.Items.Count > 0);
        RenderItems(job); _status.Text = _l(job.Started ? "Batch.ResumeHint" : "Batch.DropHint");
    }
    public void Running(ImageBatchJob job)
    {
        AcceptsInput = false; _actions.Children.Clear(); Add("Common.Cancel", "cancel");
        RenderItems(job);
    }
    public void Results(ImageBatchJob job, string report)
    {
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
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var thumbnail = new Image { Width = 72, Height = 48, Margin = new Thickness(0, 0, 12, 0), ToolTip = item.File.SourcePath };
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 72; bitmap.UriSource = new Uri(item.File.SourcePath); bitmap.EndInit(); bitmap.Freeze(); thumbnail.Source = bitmap;
            }
            catch { /* Missing originals do not prevent reading persisted descriptions. */ }
            row.Children.Add(thumbnail);
            row.Children.Add(new TextBlock { Text = item.File.DisplayName + "\n" + _l("Batch.Item." + item.Status), VerticalAlignment = VerticalAlignment.Center, ToolTip = string.IsNullOrEmpty(item.Error) ? item.File.SourcePath : item.Error });
            var entry = new ListBoxItem { Content = row, Tag = item, Margin = new Thickness(0, 3, 0, 3) };
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
