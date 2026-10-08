using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIHub.Controls;

public sealed class MusicStatusControl : UserControl, IDisposable
{
    private readonly MusicViolinist _figure = new();
    private readonly TextBlock _time = MusicAudioUi.Text(12);
    private readonly TextBlock _caption = MusicAudioUi.Text(12);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8, MinWidth = 30 };
    private readonly TextBox _terminal = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Black, Foreground = Brushes.Gainsboro, SelectionBrush = Brushes.RoyalBlue,
        FontFamily = new FontFamily("Consolas"), FontSize = 11, Padding = new(4, 1, 4, 1), BorderThickness = new(0)
    };
    private readonly DispatcherTimer _timer;
    private Func<string, string> _localize = key => key;
    private Guid _lastOperation;
    private MusicGenerationStage _lastStage = MusicGenerationStage.Idle;
    private bool _disposed;
    public MusicGenerationStatus Telemetry { get; }

    public MusicStatusControl() : this(new MusicGenerationStatus()) { }
    internal MusicStatusControl(MusicGenerationStatus telemetry)
    {
        Telemetry = telemetry;
        AutomationProperties.SetAutomationId(this, "Music.Status");
        AutomationProperties.SetAutomationId(_terminal, "Music.Status.Log");
        _terminal.PreviewMouseWheel += (_, e) => e.Handled = true;
        AutomationProperties.SetAutomationId(_time, "Music.Status.Time");
        AutomationProperties.SetAutomationId(_progress, "Music.Status.Progress");
        // The native Windows progress theme can ignore Foreground; keep stage colours explicit.
        _progress.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ProgressBar">
              <Grid x:Name="PART_Track" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Border Background="{TemplateBinding Background}" CornerRadius="4"/>
                <Border x:Name="PART_Indicator" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}" CornerRadius="4"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsIndeterminate" Value="True">
                  <Trigger.EnterActions>
                    <BeginStoryboard x:Name="Waiting" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                      <Storyboard><DoubleAnimation Storyboard.TargetName="PART_Indicator" Storyboard.TargetProperty="Opacity"
                        From="0.3" To="1" Duration="0:0:0.7" AutoReverse="True" RepeatBehavior="Forever"/></Storyboard>
                    </BeginStoryboard>
                  </Trigger.EnterActions>
                  <Trigger.ExitActions><RemoveStoryboard BeginStoryboardName="Waiting"/></Trigger.ExitActions>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var root = new Grid { Margin = new(8) };
        root.ColumnDefinitions.Add(new() { Width = new(100) }); root.ColumnDefinitions.Add(new());
        _figure.Margin = new(0, 0, 8, 0); root.Children.Add(_figure);
        var right = new Grid(); right.RowDefinitions.Add(new() { Height = new(30) }); right.RowDefinitions.Add(new());
        Grid.SetColumn(right, 1); root.Children.Add(right);
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new());
        heading.RowDefinitions.Add(new() { Height = new(16) }); heading.RowDefinitions.Add(new() { Height = new(12) });
        Grid.SetColumnSpan(_caption, 2); heading.Children.Add(_caption);
        _time.Margin = new(0, 0, 8, 0); Grid.SetRow(_time, 1); heading.Children.Add(_time);
        Grid.SetColumn(_progress, 1); Grid.SetRow(_progress, 1); heading.Children.Add(_progress);
        right.Children.Add(heading); Grid.SetRow(_terminal, 1); right.Children.Add(_terminal);
        Content = root;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _timer.Stop(); Telemetry.Changed += Changed;
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop(); Loaded += (_, _) => { Refresh(); UpdateTimer(); };
        Refresh();
    }

    public void Localize(Func<string, string> localize)
    {
        _localize = localize;
        AutomationProperties.SetName(_terminal, L("Log")); _terminal.ToolTip = L("LogHint");
        if (_terminal.Text.Length == 0) AppendLog(L("Idle"));
        Refresh();
    }

    // Future program/model output can be appended from a worker thread.
    public void AppendLog(string message)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendLog(message)); return; }
        // With manual scrolling hidden, an old viewport offset must never stop the stream.
        // Preserve the viewport only while the user selects earlier text for copying.
        var follow = _terminal.SelectionLength == 0 ||
            _terminal.VerticalOffset >= _terminal.ExtentHeight - _terminal.ViewportHeight - 2;
        var offset = _terminal.VerticalOffset; var start = _terminal.SelectionStart; var length = _terminal.SelectionLength;
        var stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var lines = message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var appended = string.Join("\n", lines.Select(line => $"[{stamp}] {new string(line.Where(c => !char.IsControl(c) || c == '\t').ToArray())}"));
        _terminal.AppendText((_terminal.Text.Length == 0 ? "" : "\n") + appended);
        var all = _terminal.Text.Split('\n'); var removed = 0;
        if (all.Length > 500)
        {
            removed = all.Take(all.Length - 500).Sum(line => line.Length + 1);
            _terminal.Text = string.Join("\n", all.Skip(all.Length - 500));
        }
        if (follow) { _terminal.UpdateLayout(); _terminal.ScrollToEnd(); }
        else
        {
            var end = Math.Max(0, start + length - removed); start = Math.Max(0, start - removed);
            _terminal.Select(start, Math.Max(0, end - start)); _terminal.UpdateLayout();
            _terminal.ScrollToVerticalOffset(Math.Max(0, offset - (all.Length - 500 > 0 ? (all.Length - 500) * _terminal.FontSize * 1.2 : 0)));
        }
    }

    private void Changed()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Changed); return; }
        var state = Telemetry.Snapshot;
        if (state.Operation != _lastOperation || state.Stage != _lastStage)
        {
            _lastOperation = state.Operation; _lastStage = state.Stage; AppendLog(L(state.Stage.ToString()));
        }
        Refresh(); UpdateTimer();
    }
    private void Refresh()
    {
        if (_disposed) return;
        var state = Telemetry.Snapshot;
        var stage = L(state.Stage.ToString()); var suffix = L("Seconds");
        _time.Text = MusicGenerationStatus.CompactTime(state.Elapsed, suffix);
        var total = (long)Math.Max(0, state.Elapsed.TotalSeconds);
        _time.ToolTip = string.Format(CultureInfo.CurrentCulture, L("FullTime"), total / 60, total % 60);
        var percent = state.Percent is { } value ? value.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "";
        if (state.Estimated && percent.Length > 0) percent = string.Format(CultureInfo.CurrentCulture, L("Estimated"), percent);
        _caption.Text = stage + (percent.Length == 0 ? "" : " · " + percent);
        _caption.ToolTip = _caption.Text;
        _progress.Foreground = state.Stage == MusicGenerationStage.Error ? Brushes.Firebrick : Brushes.ForestGreen;
        _progress.Background = state.Stage == MusicGenerationStage.Error ? Brushes.DarkRed : Brushes.DarkSlateGray;
        _progress.Value = state.Percent ?? 0;
        _progress.IsIndeterminate = state.Active && state.Percent is null && SystemParameters.ClientAreaAnimation;
        _progress.ToolTip = percent.Length == 0 ? L("UnknownProgress") : percent;
        AutomationProperties.SetName(_progress, _caption.Text);
        AutomationProperties.SetName(_figure, stage); _figure.ToolTip = stage;
        _figure.Update(state, IsVisible && SystemParameters.ClientAreaAnimation);
    }
    private void UpdateTimer()
    { if (!_disposed && IsVisible && Telemetry.Snapshot.Active) _timer.Start(); else _timer.Stop(); }
    private string L(string key) => _localize("Music.Status." + key);
    public void Dispose()
    { if (_disposed) return; _disposed = true; _timer.Stop(); Telemetry.Changed -= Changed; }
}
