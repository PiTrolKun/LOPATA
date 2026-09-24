using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed class LiteraryImportWorkspaceControl : UserControl, IDisposable
{
    private readonly ImportWorkspacePreparation _service;
    private readonly Func<string, string> _l;
    private readonly Action _open;
    private readonly string _root;
    private readonly Button _last, _fresh, _stop;
    private readonly TextBlock _status;
    private readonly StackPanel _reports = new();
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 16, 0, 12), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cancel;
    private Task _running = Task.CompletedTask;
    private bool _disposed;
    public bool IsBusy => _cancel is not null;
    public Task Running => _running;
    public LiteraryImportWorkspaceControl(ImportSession session, ImportPreparationAnswers answers, Func<string, string> l, Action open)
    {
        _l = l; _open = open; _root = session.State.ProjectPath; _service = new(session, answers, l);
        var body = new StackPanel();
        body.Children.Add(LiteraryUi.Text(T("Title"), true)); body.Children.Add(LiteraryUi.Text(T("Hint")));
        _last = LiteraryUi.Button(T("Last"), () => Start("last"), true);
        _fresh = LiteraryUi.Button(T("New"), () => Start("new"));
        foreach (var button in new[] { _last, _fresh })
        { button.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; button.Margin = new Thickness(0, 12, 0, 4); body.Children.Add(button); }
        _stop = LiteraryUi.Button(T("Stop"), () => _cancel?.Cancel()); _stop.Visibility = Visibility.Collapsed;
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        _status = LiteraryUi.Text("");
        body.Children.Add(_progress); body.Children.Add(_status); body.Children.Add(_stop); body.Children.Add(_reports); Content = body;
        try
        {
            if (_service.Load() is { } state)
            {
                _last.Content = T(state.Ready ? "Open" : "Resume"); _fresh.Visibility = Visibility.Collapsed;
                _status.Text = T(state.Ready ? "Ready" : "Saved");
            }
        }
        catch (Exception) { _status.Text = T("Failed"); }
    }
    private string T(string key) => _l("Literary.Import.Workspace." + key);
    private void Start(string choice) { if (!IsBusy && !_disposed) _running = RunAsync(choice); }
    private async Task RunAsync(string choice)
    {
        using var cancel = new CancellationTokenSource(); _cancel = cancel;
        _last.IsEnabled = _fresh.IsEnabled = false; _stop.Visibility = _progress.Visibility = Visibility.Visible;
        _reports.Children.Clear(); var success = false;
        try
        {
            var progress = new Progress<ImportWorkingPartsProgress>(p =>
            { if (_disposed) return; _status.Text = string.Format(T("Preparing"), p.Done, p.Total); _progress.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0; });
            var result = await _service.RunAsync(choice, progress, cancel.Token);
            if (_disposed) return;
            success = result.Ready;
            if (result.Report is { } report)
            {
                _status.Text = T("Failed");
                foreach (var (key, target) in new[] { ("OpenReport", Path.GetDirectoryName(report)!), ("ReportIssue", ImportRagPreparation.IssuesUrl) })
                {
                    var button = LiteraryUi.Button(_l("Literary.Import.Jelly." + key), () => Open(target));
                    button.Margin = new Thickness(0, 8, 0, 4); _reports.Children.Add(button);
                }
                Open(Path.GetDirectoryName(report)!);
            }
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = T("Saved"); }
        catch (Exception) { if (!_disposed) _status.Text = T("Failed"); }
        finally
        {
            _cancel = null; _last.IsEnabled = _fresh.IsEnabled = true;
            _stop.Visibility = _progress.Visibility = Visibility.Collapsed;
            if (!_disposed && File.Exists(Path.Combine(_root, "Import/workspace.json")))
            { _last.Content = T(success ? "Open" : "Resume"); _fresh.Visibility = Visibility.Collapsed; }
        }
        if (success && !_disposed) _open();
    }
    private void Open(string target)
    { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { _status.Text = T("Failed"); } }
    public async Task StopAsync() { _cancel?.Cancel(); await _running; }
    public void Dispose() { _disposed = true; _cancel?.Cancel(); }
}
