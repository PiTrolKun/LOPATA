using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

/// <summary>One replaceable draft at a time. Hidden/unloaded views do no parsing or rendering.</summary>
internal sealed class VectorLivePreview : Grid, IDisposable
{
    private readonly VectorLiveState _state;
    private readonly Func<string, string> _l;
    private readonly CancellationTokenSource _cancel = new();
    private readonly CancellationToken _token;
    private readonly TextBlock _caption = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBox _reasoning = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent, BorderThickness = new(0), FontSize = 15, Padding = new(8) };
    private readonly Grid _surface = new();
    private VectorBrowserView? _browser;
    private bool _started, _ready, _unavailable, _disposed;
    private int _epoch = -1, _reasoningLength, _svgLength;
    private long _nextDraft;
    private string _lastSvg = "";
    private double _aspect = 1;
    private Matrix _matrix = Matrix.Identity;

    internal VectorLivePreview(VectorLiveState state, Func<string, string> localize)
    {
        _state = state; _l = localize; _token = _cancel.Token;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _reasoning.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "Vector.Live");
        AutomationProperties.SetAutomationId(_reasoning, "Vector.Live.Reasoning");
        AutomationProperties.SetName(_reasoning, L("Reasoning"));
        AutomationProperties.SetAutomationId(_caption, "Vector.Live.Status");
        _caption.Text = L("Starting"); Children.Add(_caption);
        _surface.Children.Add(_reasoning); Grid.SetRow(_surface, 1); Children.Add(_surface);
        Loaded += async (_, _) => { if (_started || _disposed) return; _started = true; await PumpAsync(); };
        Unloaded += (_, _) => Dispose();
        SizeChanged += async (_, _) =>
        {
            try { await FitAsync(); }
            catch (Exception error) when (_disposed) { _ = error; }
            catch (Exception error) { BrowserFailed(error); }
        };
    }
    private string L(string key) => _l("Live." + key);
    private async Task PumpAsync()
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                if (IsVisible) await RefreshAsync();
                await Task.Delay(250, _token);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) when (_disposed) { _ = error; }
        catch (Exception error) { BrowserFailed(error); }
    }
    internal async Task RefreshAsync()
    {
        if (_disposed) return;
        var draftDue = !_unavailable && Environment.TickCount64 >= _nextDraft;
        var snapshot = _state.Read(_epoch, _reasoningLength, _svgLength, draftDue);
        if (snapshot.Epoch != _epoch)
        {
            CloseBrowser(); _reasoning.Clear(); _reasoningLength = _svgLength = 0; _lastSvg = "";
            _reasoning.Visibility = Visibility.Visible; _epoch = snapshot.Epoch; _matrix = Matrix.Identity;
        }
        if (snapshot.Reasoning.Length > 0 && !_ready)
        {
            var follow = _reasoning.ExtentHeight <= _reasoning.ViewportHeight + _reasoning.VerticalOffset + 12;
            _reasoning.AppendText(snapshot.Reasoning); if (follow) _reasoning.ScrollToEnd();
        }
        _reasoningLength = snapshot.ReasoningLength;
        _caption.Text = _unavailable ? L("Unavailable") : _ready ? L("Draft")
            : snapshot.Stage == "Rendering" ? _l("Rendering") : snapshot.SvgLength > 0 ? L("Waiting")
            : snapshot.ReasoningLength > 0 ? L("Reasoning") : L("Starting");
        if (!draftDue || snapshot.Answer is null) return;
        _nextDraft = Environment.TickCount64 + 1000; _svgLength = snapshot.SvgLength;
        var frame = await Task.Run(() => VectorDraftSvg.TryCreate(snapshot.Answer, _token), _token);
        if (_disposed || snapshot.Epoch != _state.Epoch || frame is null || frame.Svg == _lastSvg) return;
        try
        {
            if (_browser is null)
            {
                _browser = new VectorBrowserView { Visibility = Visibility.Hidden };
                _browser.ViewportChanged += matrix => _matrix = matrix;
                _surface.Children.Add(_browser);
                await _browser.InitializeAsync(frame.Svg, _token);
            }
            else await _browser.ReplaceSvgAsync(frame.Svg, _token);
            if (_disposed || snapshot.Epoch != _state.Epoch) return;
            _aspect = frame.Aspect; _lastSvg = frame.Svg; _ready = true;
            await FitAsync();
            _browser.Visibility = Visibility.Visible; _reasoning.Visibility = Visibility.Collapsed;
            _caption.Text = L("Draft");
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) when (_disposed) { _ = error; }
        catch (Exception error) { BrowserFailed(error); }
    }
    private Task FitAsync() => _ready && _browser is { } browser && !_disposed
        ? browser.SetViewportAsync(_surface.ActualWidth, _surface.ActualHeight, _aspect, _matrix) : Task.CompletedTask;
    private void BrowserFailed(Exception error)
    {
        if (_disposed || _unavailable) return;
        _unavailable = true; CloseBrowser(); _reasoning.Visibility = Visibility.Visible;
        // The full reasoning remains in transient state, even when the draft is on screen.
        var snapshot = _state.Read(-1, 0, 0, false);
        _reasoning.Text = snapshot.Reasoning; _reasoningLength = snapshot.ReasoningLength;
        _reasoning.ScrollToEnd(); _caption.Text = L("Unavailable");
        ComponentAuditFailures.Report(error);
        OwnedProcessRegistry.Log("vector_live_view_unavailable", "Vector.Viewer", detail: error.GetType().Name);
    }
    private void CloseBrowser()
    {
        _ready = false;
        if (_browser is not { } browser) return;
        _browser = null; browser.CloseView(); _surface.Children.Remove(browser);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel.Cancel(); CloseBrowser(); _cancel.Dispose();
    }
}
