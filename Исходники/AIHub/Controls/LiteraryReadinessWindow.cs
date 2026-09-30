using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class LiteraryReadinessWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly Func<CancellationToken, Task<LiteraryGpuSnapshot?>> _read;
    private readonly Func<long?> _spare;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _device = new(), _counts = new(), _estimate = new(), _status = new();
    private readonly TextBlock _preparation = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressBar _scale = new() { Minimum = 0, Maximum = 1, Height = 16 };
    private readonly Border _marker = new() { Width = 2, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private double _markerRatio;
    private Task _poll = Task.CompletedTask;
    private bool _started;
    public Task Completion => _poll;

    public LiteraryReadinessWindow(Func<string, string> localize,
        Func<CancellationToken, Task<LiteraryGpuSnapshot?>> read, Func<long?> spare)
    {
        _l = localize; _read = read; _spare = spare;
        Title = _l("Literary.Readiness.Title"); Width = 640; MinWidth = 420;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main && !ReferenceEquals(main, this))
            Resources.MergedDictionaries.Add(main.Resources);
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontSize = 14;
        var root = new StackPanel { Margin = new Thickness(24) };
        void Add(TextBlock block, string text = "")
        {
            block.Text = text; block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, 0, 0, 12); root.Children.Add(block);
        }
        Add(new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold }, Title);
        Add(new TextBlock(), _l("Literary.Readiness.Hint"));
        Add(_device, _l("Literary.Readiness.Checking")); Add(_counts);
        var track = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        _scale.Background = new SolidColorBrush(Color.FromRgb(43, 52, 65));
        track.Children.Add(_scale); track.Children.Add(_marker); root.Children.Add(track);
        track.SizeChanged += (_, _) => _marker.Margin = new Thickness(Math.Max(0, (track.ActualWidth - 2) * _markerRatio), 0, 0, 0);
        Add(_estimate); Add(_status); Add(_preparation);
        Add(new TextBlock { FontSize = 12 }, _l("Literary.Readiness.Disclaimer"));
        var ready = new Button { Content = _l("Literary.Readiness.Ready"), IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(24, 10, 24, 10), MinWidth = 120 };
        ready.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        ready.Click += (_, _) => DialogResult = true;
        root.Children.Add(ready); Content = root;
        Loaded += (_, _) => { if (!_started) { _started = true; _poll = PollAsync(); } };
        Closing += (_, _) => _cancel.Cancel();
        Closed += async (_, _) => { try { await _poll; } finally { _cancel.Dispose(); } };
    }
    public void SetPreparationStatus(string text)
    {
        _preparation.Text = text;
        _preparation.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                LiteraryGpuSnapshot? snapshot;
                try { snapshot = await _read(_cancel.Token); }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { break; }
                catch (Exception) { snapshot = null; }
                if (_cancel.IsCancellationRequested) break;
                Render(snapshot);
                await Task.Delay(TimeSpan.FromSeconds(1), _cancel.Token);
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
    }
    private void Render(LiteraryGpuSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _device.Text = _l("Literary.Readiness.Unavailable"); _counts.Text = "";
            _scale.Value = 0; _marker.Visibility = Visibility.Collapsed;
            _estimate.Text = _l("Literary.Readiness.Unknown"); _status.Text = "";
            _scale.Foreground = Brushes.SlateGray;
            return;
        }
        _device.Text = snapshot.Name;
        double GiB(long bytes) => bytes / 1073741824.0;
        _counts.Text = string.Format(CultureInfo.CurrentCulture, _l("Literary.Readiness.Counts"),
            GiB(snapshot.FreeBytes), GiB(snapshot.UsedBytes), GiB(snapshot.TotalBytes));
        _scale.Value = (double)snapshot.FreeBytes / snapshot.TotalBytes;
        var spare = _spare();
        if (spare is not > 0)
        {
            _estimate.Text = _l("Literary.Readiness.Unknown"); _status.Text = "";
            _scale.Foreground = Brushes.SlateGray; _marker.Visibility = Visibility.Collapsed;
            return;
        }
        _estimate.Text = string.Format(CultureInfo.CurrentCulture, _l("Literary.Readiness.Spare"), GiB(spare.Value));
        _markerRatio = Math.Clamp((double)spare.Value / snapshot.TotalBytes, 0, 1);
        _marker.Margin = new Thickness(Math.Max(0, (_scale.ActualWidth - 2) * _markerRatio), 0, 0, 0);
        _marker.Visibility = Visibility.Visible;
        var enough = snapshot.FreeBytes >= spare;
        var comfortable = snapshot.FreeBytes >= spare * 1.25;
        _scale.Foreground = new SolidColorBrush(comfortable ? Color.FromRgb(65, 185, 114) : enough ? Color.FromRgb(220, 172, 58) : Color.FromRgb(226, 94, 94));
        _status.Text = enough ? _l(comfortable ? "Literary.Readiness.Enough" : "Literary.Readiness.Tight")
            : string.Format(CultureInfo.CurrentCulture, _l("Literary.Readiness.Release"), Math.Ceiling(GiB(spare.Value - snapshot.FreeBytes) * 10) / 10);
    }
}
