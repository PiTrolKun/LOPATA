using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using ProgressBar = System.Windows.Controls.ProgressBar;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class LiteraryImportJellyControl : UserControl, IDisposable
{
    private readonly ImportJellyPreparation _service;
    private readonly string _root, _executor, _language;
    private readonly Func<string, string> _l;
    private readonly StackPanel _body = new(), _tips = new(), _report = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 12, 0, 12) };
    private readonly RadioButton _automatic, _manual;
    private readonly Button _start, _stop, _review, _next;
    private LiteraryJellyBatch? _pending;
    private LiteraryChatRuntime? _runtime;
    private CancellationTokenSource? _cancel;
    private Task _running = Task.CompletedTask;
    private bool _disposed, _loaded, _restoring, _reviewing;
    public bool IsBusy => _cancel is not null || _reviewing;
    public Task Running => _running;

    public LiteraryImportJellyControl(ImportSession session, string language, Func<string, string> localize, Action? next = null)
    {
        _root = session.State.ProjectPath; _language = language; _l = localize;
        _executor = LiteraryProjectStore.ReadProject(_root).JellyExecutor; _service = new(session, _executor);
        _body.Children.Add(LiteraryUi.Text(T("Title"), true)); _body.Children.Add(LiteraryUi.Text(T("Hint")));
        _automatic = Choice("Automatic"); _manual = Choice("Manual"); _manual.IsChecked = true;
        _body.Children.Add(_automatic); _body.Children.Add(_manual);
        var actions = new WrapPanel();
        _start = LiteraryUi.Button(T("Start"), Start, true);
        _stop = LiteraryUi.Button(T("Stop"), () => _cancel?.Cancel()); _stop.Visibility = Visibility.Collapsed;
        _review = LiteraryUi.Button(T("Review"), Review, true); _review.Visibility = Visibility.Collapsed;
        actions.Children.Add(_start); actions.Children.Add(_stop); actions.Children.Add(_review); _body.Children.Add(actions);
        _next = LiteraryUi.Button(_l("Literary.Import.Workspace.Next"), () => next?.Invoke(), true);
        _next.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; _next.Margin = new Thickness(0, 16, 0, 12);
        _next.Visibility = Visibility.Collapsed; _body.Children.Add(_next);
        _status = LiteraryUi.Text(T("Pending"));
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        _body.Children.Add(_progress); _body.Children.Add(_status); _body.Children.Add(_report); _body.Children.Add(_tips);
        Content = _body;
        _automatic.Checked += (_, _) => SaveMode(); _manual.Checked += (_, _) => SaveMode();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; _running = LoadAsync(); } };
    }
    private string T(string key) => _l("Literary.Import.Jelly." + key);
    private RadioButton Choice(string key)
    {
        var item = new RadioButton { Content = LiteraryUi.Text(T(key)), Margin = new Thickness(0, 6, 0, 6) };
        item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); return item;
    }
    private void SaveMode()
    {
        if (_restoring || !_loaded || IsBusy || _disposed) return;
        try { _service.SaveMode(_automatic.IsChecked == true ? "auto" : "manual"); }
        catch (Exception) { _status.Text = T("LoadFailed"); }
    }
    private void Busy(bool busy)
    {
        _start.IsEnabled = _automatic.IsEnabled = _manual.IsEnabled = _review.IsEnabled = !busy;
        _next.IsEnabled = !busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadAsync()
    {
        using var cancel = new CancellationTokenSource(); _cancel = cancel; Busy(true); _restoring = true;
        try
        {
            var state = await Task.Run(_service.LoadState, cancel.Token);
            if (_disposed || state is null) return;
            _automatic.IsChecked = state.Mode == "auto"; _manual.IsChecked = state.Mode == "manual";
            _progress.Value = state.Total > 0 ? 100.0 * state.Done / state.Total : 0;
            _status.Text = state.Status == "ready" ? T("Complete") : string.Format(T("Saved"), state.Done, state.Total);
            _next.Visibility = state.Status == "ready" ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _status.Text = T("LoadFailed"); }
        finally { _cancel = null; _restoring = false; Busy(false); }
    }
    private void Start() { if (!IsBusy && !_disposed) _running = RunAsync(); }
    private async Task RunAsync()
    {
        using var cancel = new CancellationTokenSource(); _cancel = cancel; Busy(true);
        _next.Visibility = Visibility.Collapsed;
        _report.Children.Clear(); _review.Visibility = Visibility.Collapsed; _progress.IsIndeterminate = true;
        _status.Text = T("Starting");
        _tips.Children.Add(LiteraryUi.Text(_l("Literary.Import.TipsHold")));
        _tips.Children.Add(new LiteraryFloatingTips(LiteraryTipCatalog.Load(_language), _l("Literary.Import.TipsHold"),
            () => IsBusy && !_disposed, Path.Combine(AppDataPaths.BaseDirectory, "Literary/tips-history.json")));
        try
        {
            _runtime ??= new LiteraryChatRuntime(_root);
            var progress = new Progress<ImportJellyProgress>(p =>
            {
                if (_disposed || cancel.IsCancellationRequested) return;
                _status.Text = string.Format(T(p.Stage), p.Done, p.Total, p.Attempt);
                _progress.IsIndeterminate = p.Total == 0;
                if (p.Total > 0) _progress.Value = 100.0 * p.Done / p.Total;
            });
            ImportJellyOutcome? outcome = null;
            await _runtime.WithJellyExecutorAsync(_executor, async extract =>
            {
                outcome = await _service.RunAsync(_automatic.IsChecked == true ? "auto" : "manual", extract, progress, cancel.Token);
                return outcome.Ready;
            }, cancel.Token);
            if (_disposed) return;
            if (outcome!.Report is { } report) ShowReport(report);
            else if (outcome.Ready) { _pending = null; _status.Text = T("Complete"); _progress.Value = 100; _next.Visibility = Visibility.Visible; }
            else
            {
                _pending = outcome.Review; _review.Visibility = Visibility.Visible;
                _status.Text = string.Format(T("ReviewPending"), _pending!.Number, _pending.Facts.Length);
            }
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = T("Stopped"); }
        catch (Exception) { if (!_disposed) _status.Text = T("LoadFailed"); }
        finally
        {
            _cancel = null; Busy(false); _progress.IsIndeterminate = false; _tips.Children.Clear();
            if (_disposed) { _runtime?.Dispose(); _runtime = null; }
        }
    }
    private void Review()
    {
        if (IsBusy || _disposed || _pending is null) return;
        _reviewing = true;
        try
        {
            async Task Save(IReadOnlyList<LiteraryJellyFact> decisions, bool warnings)
            {
                var report = await _service.ConfirmAsync(_pending, decisions, warnings, default);
                if (report is not null) { ShowReport(report); throw new IOException("Memory save failed."); }
            }
            var accepted = LiteraryJellyReviewDialog.Show(this, _l,
                _pending.Facts.Select(f => new LiteraryJellyReviewItem(f, _pending.Number, _pending.SourceText)).ToArray(),
                decisions => Save(decisions, false), language: _language,
                acceptWarnings: decisions => Save(decisions, true), saveDraft: decisions => _service.SaveReviewDraft(_pending, decisions));
            _pending = _service.ReadReviewDraft(_pending);
            if (accepted) { _review.Visibility = Visibility.Collapsed; _status.Text = T("ReviewSaved"); }
        }
        catch (Exception) { _status.Text = T("LoadFailed"); }
        finally { _reviewing = false; }
    }
    private void ShowReport(string path)
    {
        _status.Text = T("Failed"); _report.Children.Clear();
        _report.Children.Add(LiteraryUi.Button(T("OpenReport"), () => Open(Path.GetDirectoryName(path)!)));
        _report.Children.Add(LiteraryUi.Button(T("ReportIssue"), () => Open(ImportRagPreparation.IssuesUrl)));
        Open(Path.GetDirectoryName(path)!);
    }
    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { _status.Text = T("OpenFailed"); }
    }
    public async Task StopAsync() { _cancel?.Cancel(); await _running; }
    public void Dispose() { _disposed = true; _cancel?.Cancel(); if (!IsBusy) { _runtime?.Dispose(); _runtime = null; } }
}
