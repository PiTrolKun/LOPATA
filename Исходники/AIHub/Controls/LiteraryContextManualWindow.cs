using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

public sealed class LiteraryContextManualWindow : Window
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _measurement;
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TextBlock _counter = LiteraryUi.Text(""), _status = LiteraryUi.Text("");
    private readonly Canvas _overlay = new() { IsHitTestVisible = true };
    private readonly Border _floating = new() { Width = 260, Padding = new Thickness(10), CornerRadius = new CornerRadius(8) };
    private readonly Button _accept = new();
    private readonly Func<string, string> _l;
    private readonly Func<IReadOnlySet<string>, string?, CancellationToken, Task<StudioContextMeter?>> _measure;
    private bool _applying;

    public LiteraryContextManualWindow(Window owner, Func<string, string> l, StudioContextPlan plan,
        Func<IReadOnlySet<string>, string?, CancellationToken, Task<StudioContextMeter?>> measure,
        Func<IReadOnlySet<string>, CancellationToken, Task<bool>> apply)
    {
        _l = l; _measure = measure;
        LiteraryContextWindowParts.Configure(this, owner, l("Studio.Context.Method.Manual"));
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = LiteraryUi.Text(l("Studio.Context.ManualHint")); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        _accept = LiteraryContextWindowParts.Button(l("Studio.Context.Confirm"), "Studio.Context.ManualAccept", async () =>
        {
            if (_selected.Count == 0 || _applying) return;
            _applying = true; _accept.IsEnabled = false;
            try { if (await apply(_selected.ToHashSet(), _lifetime.Token)) { _applying = false; DialogResult = true; } }
            catch (Exception ex) { LiteraryContextWindowParts.Error(_status, ex, l); }
            finally { _applying = false; _accept.IsEnabled = _selected.Count > 0; }
        }, true);
        _accept.IsEnabled = false; footer.Children.Add(_accept);
        var area = new Grid(); root.Children.Add(area);
        var list = new StackPanel();
        area.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var number = 0;
        foreach (var item in plan.Items)
        {
            var card = new StackPanel { Margin = new Thickness(0, 8, 0, 14) };
            var check = new CheckBox { Content = l("Studio.Role." + item.Role) + " · " + ++number, Tag = item.Id, Margin = new Thickness(0, 0, 0, 6) };
            check.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            System.Windows.Automation.AutomationProperties.SetAutomationId(check, "Studio.Context.Exclude." + item.Id);
            check.Checked += (_, _) => { _selected.Add(item.Id); Changed(); };
            check.Unchecked += (_, _) => { _selected.Remove(item.Id); Changed(); };
            card.Children.Add(check);
            var text = new TextBox { Text = item.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
            text.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
            text.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush"); card.Children.Add(text); list.Children.Add(card);
        }
        // Canvas has no background: only the draggable counter intercepts pointer input.
        area.Children.Add(_overlay); _overlay.Children.Add(_floating);
        _floating.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        _floating.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); _floating.BorderThickness = new Thickness(1);
        var panel = new StackPanel(); _floating.Child = panel;
        var grip = new Thumb { Height = 18, Cursor = System.Windows.Input.Cursors.SizeAll };
        System.Windows.Automation.AutomationProperties.SetName(grip, l("Studio.Context.DragCounter"));
        panel.Children.Add(grip); panel.Children.Add(_counter);
        Canvas.SetLeft(_floating, 10); Canvas.SetTop(_floating, 10);
        grip.DragDelta += (_, e) => Place(Canvas.GetLeft(_floating) + e.HorizontalChange, Canvas.GetTop(_floating) + e.VerticalChange);
        _overlay.SizeChanged += (_, _) => Place(Canvas.GetLeft(_floating), Canvas.GetTop(_floating));
        _floating.SizeChanged += (_, _) => Place(Canvas.GetLeft(_floating), Canvas.GetTop(_floating));
        _delay.Tick += async (_, _) => { _delay.Stop(); await MeasureAsync(); };
        Loaded += async (_, _) => await MeasureAsync();
        Closing += (_, e) => { if (_applying) { e.Cancel = true; return; } _lifetime.Cancel(); _measurement?.Cancel(); _delay.Stop(); };
    }
    private void Place(double x, double y)
    {
        Canvas.SetLeft(_floating, Math.Clamp(x, 0, Math.Max(0, _overlay.ActualWidth - _floating.ActualWidth)));
        Canvas.SetTop(_floating, Math.Clamp(y, 0, Math.Max(0, _overlay.ActualHeight - _floating.ActualHeight)));
    }
    private void Changed()
    {
        _accept.IsEnabled = _selected.Count > 0 && !_applying;
        _measurement?.Cancel(); _counter.Text = _l("Studio.Context.Counting"); _delay.Stop(); _delay.Start();
    }
    private async Task MeasureAsync()
    {
        _measurement?.Cancel(); using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _measurement = source;
        try
        {
            var meter = await _measure(_selected.ToHashSet(), null, source.Token);
            if (!source.IsCancellationRequested) _counter.Text = meter is null ? _l("Studio.Context.Unknown")
                : string.Format(_l("Studio.Context.Meter"), meter.Input, meter.Available, meter.Free, meter.UsedRatio * 100)
                    + (meter.Estimate ? "\n" + _l("Studio.Context.EstimateShort") : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!source.IsCancellationRequested) LiteraryContextWindowParts.Error(_counter, ex, _l); }
        finally { if (_measurement == source) _measurement = null; }
    }
}
