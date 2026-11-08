using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private readonly Grid _footer = new();
    private readonly TextBlock _statisticsText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 10, 0) };
    private readonly DispatcherTimer _statisticsTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _statisticsRequest;
    private int _statisticsRevision;

    private void BuildFooter()
    {
        _footer.Margin = new(14, 0, 14, 2);
        _footer.ColumnDefinitions.Add(new());
        _footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetRow(_footer, 3); _root.Children.Add(_footer);
        AutomationProperties.SetAutomationId(_footer, "Office.Reader.Footer");
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _footer.Children.Add(_status);
        Grid.SetColumn(_statisticsText, 1); _footer.Children.Add(_statisticsText);
        AutomationProperties.SetAutomationId(_statisticsText, "Office.Reader.Statistics");
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nav, 2); _footer.Children.Add(nav);
        _previous = Action("‹", "Previous", () => Move(-1), T("Previous")); nav.Children.Add(_previous);
        _pageNumber.VerticalAlignment = VerticalAlignment.Center; _pageNumber.Width = 42; _pageNumber.Padding = new(4, 1, 4, 1);
        nav.Children.Add(_pageNumber);
        _pageNumber.KeyDown += (_, e) => { if (e.Key == Key.Enter) { if (int.TryParse(_pageNumber.Text, out var page)) Go(page - 1); e.Handled = true; } };
        _counter.VerticalAlignment = VerticalAlignment.Center; _counter.Margin = new(4, 0, 6, 0); nav.Children.Add(_counter);
        _next = Action("›", "Next", () => Move(1), T("Next")); nav.Children.Add(_next);
        foreach (var button in new[] { _previous, _next }) { button.Padding = new(6, 1, 6, 1); button.Margin = new(2, 0, 2, 0); }
        AutomationProperties.SetAutomationId(_status, "Office.Reader.Status");
        AutomationProperties.SetAutomationId(_pageNumber, "Office.Reader.PageNumber");
        AutomationProperties.SetAutomationId(_zoomLabel, "Office.Reader.Zoom");
        UpdateFooterFont(); ResetStatistics();
        _selection.Changed += QueueStatistics;
        _statisticsTimer.Tick += (_, _) => { _statisticsTimer.Stop(); _ = RefreshStatisticsAsync(); };
    }

    private void UpdateFooterFont()
    {
        // WPF uses 96-DPI units; the requested 8/10 are typographic points.
        var size = (_fullscreen ? 10.0 : 8.0) * 96 / 72;
        TextElement.SetFontSize(_footer, size);
        foreach (var text in new[] { _status, _statisticsText, _counter }) text.FontSize = size;
        foreach (var control in new Control[] { _previous, _next, _pageNumber }) control.FontSize = size;
    }

    private void CancelStatistics()
    {
        _statisticsRevision++; _statisticsTimer.Stop();
        _statisticsRequest?.Cancel(); _statisticsRequest?.Dispose(); _statisticsRequest = null;
    }

    private void ResetStatistics()
    {
        CancelStatistics(); _statisticsText.Text = FormatStatistics("0", "0", "≈0");
        _statisticsText.ToolTip = T("StatisticsDocument") + "\n" + T("StatisticsHelp");
    }

    private void QueueStatistics()
    {
        CancelStatistics();
        if (_closed || _document is null || _load is null) return;
        _statisticsText.Text = FormatStatistics("…", "…", "≈…");
        _statisticsText.ToolTip = T(_selection.HasSelection ? "StatisticsSelection" : "StatisticsDocument") + "\n" + T("StatisticsHelp");
        _statisticsTimer.Start();
    }

    private string FormatStatistics(string lines, string characters, string tokens) =>
        string.Format(CultureInfo.CurrentCulture, T("Statistics"), lines, characters, tokens);

    private async Task RefreshStatisticsAsync()
    {
        var document = _document; var lifetime = _load;
        if (document is null || lifetime is null || _closed) return;
        var revision = _statisticsRevision;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _statisticsRequest = request;
        var bounds = _selection.HasSelection ? _selection.Bounds : null;
        try
        {
            var statistics = bounds is { } range
                ? await document.Statistics.ReadSelectionAsync(range.Start.Page, range.Start.Offset, range.End.Page, range.End.Offset, request.Token)
                : await document.Statistics.ReadAllAsync(request.Token);
            if (_closed || request.IsCancellationRequested || revision != _statisticsRevision || !ReferenceEquals(document, _document)) return;
            _statisticsText.Text = FormatStatistics(statistics.Lines.ToString("N0"), statistics.Characters.ToString("N0"), "≈" + statistics.ApproximateTokens.ToString("N0"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (_closed || revision != _statisticsRevision) return;
            _statisticsText.Text = T("StatisticsUnavailable");
            OwnedProcessRegistry.Log("office_reader_statistics_failed", "Office.Reader", detail: error.GetType().Name + ": " + error.Message);
        }
        finally { if (ReferenceEquals(_statisticsRequest, request)) _statisticsRequest = null; }
    }
}
