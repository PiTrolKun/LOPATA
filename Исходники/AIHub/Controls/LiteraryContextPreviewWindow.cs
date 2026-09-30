using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class LiteraryContextPreviewWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _text = LiteraryWorkspaceParts.TextArea(false);
    private readonly TextBlock _status = LiteraryUi.Text(""), _meter = LiteraryUi.Text("");
    private readonly Button _accept, _stronger;
    private readonly Func<string, string> _l;
    private readonly Func<double, IProgress<StudioCompactionProgress>, CancellationToken, Task<StudioCompactionResult>> _generate;
    private readonly Func<IReadOnlySet<string>, string?, CancellationToken, Task<StudioContextMeter?>> _measure;
    private readonly Func<IReadOnlySet<string>, string?, CancellationToken, Task<bool>> _apply;
    private StudioCompactionResult? _result;
    private double _ratio = .5;
    private Task? _running;
    private bool _applying, _closing;

    public LiteraryContextPreviewWindow(Window owner, Func<string, string> l, StudioContextMethod method,
        Func<double, IProgress<StudioCompactionProgress>, CancellationToken, Task<StudioCompactionResult>> generate,
        Func<IReadOnlySet<string>, string?, CancellationToken, Task<StudioContextMeter?>> measure,
        Func<IReadOnlySet<string>, string?, CancellationToken, Task<bool>> apply)
    {
        _l = l; _generate = generate; _measure = measure; _apply = apply;
        LiteraryContextWindowParts.Configure(this, owner, l("Studio.Context.Method." + method));
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(LiteraryUi.Text(l("Studio.Context.PreviewHint"))); header.Children.Add(_status); header.Children.Add(_meter);
        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _accept = LiteraryContextWindowParts.Button(l("Studio.Context.Accept"), "Studio.Context.PreviewAccept", async () =>
        {
            if (_result is null || _applying || string.IsNullOrWhiteSpace(_text.Text)) return;
            _applying = true; Buttons();
            try { if (await _apply(_result.ReplacedIds, _text.Text, _lifetime.Token)) { _applying = false; DialogResult = true; } }
            catch (Exception ex) { LiteraryContextWindowParts.Error(_status, ex, l); }
            finally { _applying = false; Buttons(); }
        }, true);
        _stronger = LiteraryContextWindowParts.Button(l("Studio.Context.Stronger"), "Studio.Context.Stronger", async () =>
        { _ratio *= .7; await StartAsync(); });
        footer.Children.Add(_accept); footer.Children.Add(_stronger);
        root.Children.Add(_text); _text.IsReadOnly = true;
        Loaded += async (_, _) => await StartAsync(); Buttons();
        Closing += async (_, e) =>
        {
            if (_applying) { e.Cancel = true; return; }
            if (_running is { IsCompleted: false })
            {
                e.Cancel = true; if (_closing) return;
                _closing = true; _lifetime.Cancel();
                await _running; Close();
            }
            else _lifetime.Cancel();
        };
    }
    private Task StartAsync() => _running = GenerateAsync();
    private async Task GenerateAsync()
    {
        _result = null; _text.IsReadOnly = true; Buttons(); _status.Text = _l("Studio.Context.Counting");
        try
        {
            var progress = new Progress<StudioCompactionProgress>(p => _status.Text = string.Format(
                _l("Studio.Context.Progress." + p.Stage), p.Done + (p.Stage == "Complete" ? 0 : 1), p.Total));
            var result = await _generate(_ratio, progress, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested(); _result = result; _text.Text = result.Text;
            _meter.Text = string.Format(_l("Studio.Context.ResultSize"), result.BeforeTokens, result.AfterTokens);
            var meter = await _measure(result.ReplacedIds, result.Text, _lifetime.Token);
            _meter.Text += "\n" + LiteraryContextWindowParts.Meter(meter, _l);
            _status.Text = _l(result.AfterTokens <= result.BeforeTokens * _ratio ? "Studio.Context.Review" : "Studio.Context.GoalNotMet");
            if (meter is not null && meter.Input > meter.Available) _status.Text += "\n" + _l("Studio.Context.StillFull");
        }
        catch (Exception ex) { if (!_closing) LiteraryContextWindowParts.Error(_status, ex, _l); }
        finally { Buttons(); }
    }
    private void Buttons()
    {
        _accept.IsEnabled = _result is not null && !_applying;
        _stronger.IsEnabled = _result is not null && !_applying && _ratio > .15;
    }
}
