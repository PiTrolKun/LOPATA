using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Two serial RAG stages, with one shared progress surface and a resumable boundary.</summary>
public sealed class LiteraryImportRagControl : UserControl, IDisposable
{
    private readonly ImportSession _session;
    private readonly ImportPreparationAnswers _answers;
    private readonly Func<string, string> _l;
    private readonly string _language;
    private readonly StackPanel _body = new();
    private readonly TextBlock _reference, _book, _status;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _start, _cancel, _next;
    private readonly StackPanel _tips = new(), _report = new();
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private bool _disposed;
    public bool IsBusy => _cancellation is not null;
    public Task Running => _running;

    public LiteraryImportRagControl(ImportSession session, ImportPreparationAnswers answers, string language, Func<string, string> localize, Action? next = null)
    {
        _session = session; _answers = answers; _l = localize; _language = language;
        _body.Children.Add(LiteraryUi.Text(T("Title"), true));
        _body.Children.Add(LiteraryUi.Text(T("Hint")));
        _reference = LiteraryUi.Text(T("Reference")); _book = LiteraryUi.Text(T("Book"));
        _status = LiteraryUi.Text(session.State.RagStatus == "ready" ? T("Saved") : T("Pending"));
        _body.Children.Add(_reference); _body.Children.Add(_book); _body.Children.Add(_progress); _body.Children.Add(_status);
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        var actions = new WrapPanel();
        _start = LiteraryUi.Button(T("Start"), () => { if (!IsBusy) _running = RunAsync(); }, true);
        _cancel = LiteraryUi.Button(T("Stop"), Cancel); _cancel.Visibility = Visibility.Collapsed;
        actions.Children.Add(_start); actions.Children.Add(_cancel); _body.Children.Add(actions);
        _next = LiteraryUi.Button(T("Next"), () => { if (!IsBusy && _session.State.RagStatus == "ready") next?.Invoke(); }, true);
        _next.Visibility = next is not null && session.State.RagStatus == "ready" ? Visibility.Visible : Visibility.Collapsed;
        _body.Children.Add(_next);
        _body.Children.Add(_report); _body.Children.Add(_tips);
        Content = _body;
    }
    private string T(string key) => _l("Literary.Import.RagStages." + key);
    public void Cancel() => _cancellation?.Cancel();
    public async Task StopAsync() { Cancel(); await _running; }

    private async Task RunAsync()
    {
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _start.Visibility = Visibility.Collapsed; _cancel.Visibility = Visibility.Visible; _report.Children.Clear();
        _next.Visibility = Visibility.Collapsed;
        _reference.Text = T("Reference"); _book.Text = T("Book");
        try
        {
            var progress = new Progress<ImportRagProgress>(p =>
            {
                if (_disposed || cancellation.IsCancellationRequested) return;
                var row = p.Area == "Reference" ? _reference : _book;
                var state = p.Stage switch
                {
                    "Ready" => T("Ready"), "Skipped" => T("Skipped"), "Retry" => T("Retry"),
                    "Checking" => T("Checking"), _ => _l("Literary.Rag." + p.Stage)
                };
                // Backend details can contain file names; only localized stages reach the UI.
                if (state.StartsWith("Literary.", StringComparison.Ordinal)) state = T("Working");
                row.Text = T(p.Area) + " · " + state;
                _status.Text = p.Attempt > 0 ? string.Format(T("Attempt"), p.Attempt) : "";
                _progress.IsIndeterminate = p.Percent < 0;
                if (p.Percent >= 0) _progress.Value = Math.Clamp(p.Percent, 0, p.Stage is "Ready" or "Skipped" ? 100 : 99);
                SetTips(p.Characters > 20_000 && p.Stage is not ("Ready" or "Skipped"));
            });
            // IO and hashing also stay off the UI thread; progress returns through the dispatcher.
            var result = await Task.Run(() => new ImportRagPreparation(_session, _answers).RunAsync(progress, cancellation.Token));
            if (_disposed) return;
            _status.Text = T(result.Ready ? "Complete" : "Failed");
            if (result.Ready) _next.Visibility = Visibility.Visible;
            if (result.Report is { } report)
            {
                _report.Children.Add(LiteraryUi.Button(T("OpenReport"), () => Open(Path.GetDirectoryName(report)!)));
                _report.Children.Add(LiteraryUi.Button(T("ReportIssue"), () => Open(ImportRagPreparation.IssuesUrl)));
                Open(Path.GetDirectoryName(report)!);
            }
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = T("Stopped"); }
        catch (Exception) { if (!_disposed) _status.Text = T("FailedToStart"); }
        finally
        {
            _cancellation = null;
            _progress.IsIndeterminate = false; SetTips(false);
            _start.Visibility = Visibility.Visible; _cancel.Visibility = Visibility.Collapsed;
        }
    }
    private void SetTips(bool visible)
    {
        if (!visible) { _tips.Children.Clear(); return; }
        if (_tips.Children.Count > 0) return;
        _tips.Children.Add(LiteraryUi.Text(_l("Literary.Import.TipsHold")));
        _tips.Children.Add(new LiteraryFloatingTips(LiteraryTipCatalog.Load(_language), _l("Literary.Import.TipsHold"),
            () => IsBusy && !_disposed, Path.Combine(AppDataPaths.BaseDirectory, "Literary", "tips-history.json")));
    }
    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { _status.Text = T("OpenFailed"); }
    }
    public void Dispose() { _disposed = true; Cancel(); SetTips(false); }
}
