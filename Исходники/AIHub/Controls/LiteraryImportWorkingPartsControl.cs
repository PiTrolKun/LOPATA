using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using ProgressBar = System.Windows.Controls.ProgressBar;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class LiteraryImportWorkingPartsControl : UserControl, IDisposable
{
    private readonly ImportWorkingPartsPreparation _service;
    private readonly string _root;
    private readonly Func<string, string> _l;
    private readonly StackPanel _body = new(), _review = new(), _report = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
    private readonly RadioButton _automatic, _manual;
    private readonly Button _prepare, _stop, _next;
    private ImportWorkingPartsPlan? _plan;
    private ImportReviewBook? _book;
    private CancellationTokenSource? _cancel;
    private Task _running = Task.CompletedTask;
    private bool _disposed, _loaded;
    public bool IsBusy => _cancel is not null;
    public Task Running => _running;

    public LiteraryImportWorkingPartsControl(ImportSession session, Func<string, string> localize, Action? next = null)
    {
        _service = new(session); _root = session.State.ProjectPath; _l = localize;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/LiteraryBookScrollResources.xaml", UriKind.Relative) });
        _body.Children.Add(LiteraryUi.Text(T("Title"), true));
        _body.Children.Add(LiteraryUi.Text(T("Hint")));
        _automatic = Choice("Automatic"); _manual = Choice("Manual"); _manual.IsChecked = true;
        _body.Children.Add(_automatic); _body.Children.Add(_manual);
        var actions = new WrapPanel();
        _prepare = LiteraryUi.Button(T("Prepare"), () => Start(false), true);
        _stop = LiteraryUi.Button(T("Stop"), () => _cancel?.Cancel()); _stop.Visibility = Visibility.Collapsed;
        actions.Children.Add(_prepare); actions.Children.Add(_stop); _body.Children.Add(actions);
        _next = LiteraryUi.Button(T("Next"), () => { if (!IsBusy) next?.Invoke(); }, true);
        _next.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _next.Margin = new Thickness(0, 16, 0, 12);
        _next.Visibility = Visibility.Collapsed; _body.Children.Add(_next);
        _status = LiteraryUi.Text("");
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        _body.Children.Add(_progress); _body.Children.Add(_status); _body.Children.Add(_report); _body.Children.Add(_review);
        Content = _body;
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; _running = LoadAsync(); } };
    }
    private string T(string key) => _l("Literary.Import.WorkingParts." + key);
    private RadioButton Choice(string key)
    {
        var item = new RadioButton { Content = LiteraryUi.Text(T(key)), Margin = new Thickness(0, 6, 0, 6) };
        item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); return item;
    }
    private void Busy(bool busy)
    {
        _prepare.IsEnabled = !busy; _automatic.IsEnabled = !busy; _manual.IsEnabled = !busy; _review.IsEnabled = !busy;
        _next.IsEnabled = !busy;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadAsync()
    {
        using var cancel = new CancellationTokenSource(); _cancel = cancel; Busy(true);
        try
        {
            var result = await Task.Run(() => (_service.Current(), _service.LoadDraft(), new ImportReviewBookStore(_root).Load()), cancel.Token);
            if (_disposed || cancel.IsCancellationRequested) return;
            _plan = result.Item2; _book = result.Item3;
            if (result.Item1 is { } ready)
            {
                _automatic.IsChecked = ready.Mode == "auto"; _manual.IsChecked = ready.Mode == "manual";
                _status.Text = string.Format(T("Complete"), ready.Parts.Length); _progress.Value = 100;
                _next.Visibility = Visibility.Visible;
            }
            else if (_plan is not null) { _automatic.IsChecked = _plan.Mode == "auto"; _manual.IsChecked = _plan.Mode == "manual"; RenderReview(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _status.Text = T("LoadFailed"); }
        finally { _cancel = null; Busy(false); }
    }
    private void Start(bool commit)
    {
        if (IsBusy || _disposed) return;
        _running = RunAsync(commit);
    }
    private async Task RunAsync(bool commit)
    {
        using var cancel = new CancellationTokenSource(); _cancel = cancel; Busy(true); _report.Children.Clear();
        _next.Visibility = Visibility.Collapsed;
        try
        {
            var progress = new Progress<ImportWorkingPartsProgress>(p =>
            {
                if (_disposed || cancel.IsCancellationRequested) return;
                _status.Text = string.Format(T(p.Stage), p.Done, p.Total, p.Attempt);
                _progress.IsIndeterminate = p.Total == 0;
                if (p.Total > 0) _progress.Value = 100.0 * p.Done / p.Total;
            });
            var result = commit
                ? await _service.CommitAsync(_plan!, progress, cancel.Token)
                : await _service.PrepareAsync(_automatic.IsChecked == true ? "auto" : "manual", progress, cancel.Token);
            if (_disposed) return;
            _plan = result.Plan;
            if (result.Report is { } report)
            {
                _status.Text = T("Failed");
                _report.Children.Add(LiteraryUi.Button(T("OpenReport"), () => Open(Path.GetDirectoryName(report)!)));
                _report.Children.Add(LiteraryUi.Button(T("ReportIssue"), () => Open(ImportRagPreparation.IssuesUrl)));
                Open(Path.GetDirectoryName(report)!);
            }
            else if (result.Ready) { _review.Children.Clear(); _status.Text = string.Format(T("Complete"), _plan!.Parts.Count); _next.Visibility = Visibility.Visible; }
            else
            {
                _book = await Task.Run(() => new ImportReviewBookStore(_root).Load(), cancel.Token);
                if (!_disposed) RenderReview();
            }
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = T("Stopped"); }
        catch (Exception) { if (!_disposed) _status.Text = T("FailedToStart"); }
        finally { _cancel = null; Busy(false); _progress.IsIndeterminate = false; }
    }

    private void RenderReview()
    {
        _review.Children.Clear();
        if (_plan is null || _book is null) return;
        _status.Text = string.Format(T("ReviewSummary"), _plan.Parts.Count, _plan.Warnings);
        _review.Children.Add(LiteraryUi.Text(T("ReviewHint")));
        var list = new ListBox { MaxHeight = 180, Margin = new Thickness(0, 8, 0, 8), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        foreach (var part in _plan.Parts)
        {
            var title = part.Title.Length == 0 ? T("Untitled") : part.Title;
            list.Items.Add(new TextBlock { Text = $"{part.Number}. {title} · {part.Length:N0}" + (part.ParagraphSplit ? " · " + T("ParagraphSplit") : ""), TextWrapping = TextWrapping.Wrap });
        }
        var detail = LiteraryUi.Text("");
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 230, Padding = new Thickness(10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsInactiveSelectionHighlightEnabled = true };
        text.SetResourceReference(TextBox.SelectionBrushProperty, "AccentBrush");
        text.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); text.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var move = LiteraryUi.Button(T("MoveBoundary"), () =>
        {
            try
            {
                // The editor is read-only: moving a cut cannot accidentally rewrite the confirmed book.
                var copy = System.Text.Json.JsonSerializer.Deserialize<ImportWorkingPartsPlan>(
                    System.Text.Json.JsonSerializer.Serialize(_plan))!;
                copy.MoveBoundary(_book, list.SelectedIndex, text.CaretIndex);
                _service.SaveDraft(copy); _plan = copy; RenderReview();
            }
            catch (InvalidDataException) { _status.Text = T("InvalidBoundary"); }
            catch (Exception) { _status.Text = T("SaveFailed"); }
        });
        list.SelectionChanged += (_, _) =>
        {
            var i = list.SelectedIndex; if (i < 0) return;
            var part = _plan.Parts[i]; var next = i + 1 < _plan.Parts.Count ? _plan.Parts[i + 1] : null;
            var paired = next?.Chapter == part.Chapter;
            text.Text = _book.Text.Substring(part.Start, part.Length + (paired ? next!.Length : 0));
            text.Select(part.Length, paired ? System.Globalization.StringInfo.GetNextTextElement(text.Text, part.Length).Length : 0);
            text.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (_disposed || list.SelectedIndex != i) return;
                text.UpdateLayout();
                var line = text.GetLineIndexFromCharacterIndex(part.Length);
                if (line >= 0) text.ScrollToLine(Math.Max(0, line - 2));
            }));
            move.IsEnabled = paired; detail.Text = string.Format(T(paired ? "BoundaryHint" : "SingleHint"), part.Number, part.Length);
            try { _plan.Selected = i; _service.SaveDraft(_plan); }
            catch (Exception) { _status.Text = T("SaveFailed"); }
        };
        _review.Children.Add(list); _review.Children.Add(detail); _review.Children.Add(text); _review.Children.Add(move);
        _review.Children.Add(LiteraryUi.Button(T(_plan.Warnings > 0 ? "AcceptWarnings" : "Accept"), () => Start(true), true));
        list.SelectedIndex = _plan.Selected;
    }
    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { _status.Text = T("OpenFailed"); }
    }
    public async Task StopAsync() { _cancel?.Cancel(); await _running; }
    public void Dispose() { _disposed = true; _cancel?.Cancel(); }
}
