using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.Books;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    private readonly TextBox _logs = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 90,
        MaxHeight = 180
    };
    private string _lastProgressLog = "";
    private bool _hasLogEvents;
    private const int MaximumLogCharacters = 160000;

    private Expander CreateDiagnostics()
    {
        _logs.Text = L("LogsIdle");
        AutomationProperties.SetAutomationId(_logs, "BookConverter.Logs");
        AutomationProperties.SetName(_logs, L("Logs"));
        AutomationProperties.SetAutomationId(_report, "BookConverter.Reports");
        AutomationProperties.SetName(_report, L("Reports"));
        _report.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var tabs = new TabControl(); AutomationProperties.SetAutomationId(tabs, "BookConverter.DiagnosticsTabs");
        tabs.Items.Add(new TabItem { Header = L("Logs"), Content = _logs });
        tabs.Items.Add(new TabItem { Header = L("Reports"), Content = _report });
        tabs.SelectedIndex = 0;
        var details = new Expander { Header = L("Report"), Content = tabs, Margin = new(0, 5, 0, 0) };
        AutomationProperties.SetAutomationId(details, "BookConverter.Diagnostics");
        return details;
    }

    private void BeginDiagnostics(bool resumed = false)
    {
        _logs.Clear(); _report.Clear(); _lastProgressLog = ""; _hasLogEvents = false;
        AppendLog(L(resumed ? "LogResumed" : "LogStarted"));
    }

    private void AppendLog(string message)
    {
        // Progress<T> dispatches both stage and runtime events to this window's UI thread.
        var follow = _logs.ExtentHeight <= _logs.ViewportHeight + _logs.VerticalOffset + 2;
        message = message.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        if (message.Length == 0) return;
        if (!_hasLogEvents) { _logs.Clear(); _hasLogEvents = true; }
        if (message.Length > 4000) message = message[..4000] + "…";
        _logs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (_logs.Text.Length > MaximumLogCharacters)
        {
            var first = _logs.Text.IndexOf('\n', MaximumLogCharacters / 4);
            _logs.Text = _logs.Text[(first + 1)..];
        }
        if (follow) _logs.ScrollToEnd();
    }

    private string DescribeProgress(BookConversionProgress value)
    {
        var message = value.Message.Length > 0 ? Explain(value.Message) : L("Working");
        if (value.SectionCount > 0) message += " · " + string.Format(L("LogSection"), value.Section, value.SectionCount);
        if (value.ItemCount > 0) message += " · " + string.Format(L(value.Phase == "structure" ? "LogBatch" : "LogImage"), value.Item, value.ItemCount);
        if (value.Total > 0) message += $" {value.Completed}/{value.Total}";
        if (value.Source.Length > 0) message += " · " + Path.GetFileName(value.Source);
        return message;
    }

    private void CompleteDiagnostics(BookConversionResult result)
    {
        _status.Text = string.Format(L(result.Cancelled ? "CancelledSummary" : "Summary"), result.Outputs.Length,
            result.Failures.Length, result.Warnings.Length, result.Skipped.Length);
        var details = result.Warnings.Select(Explain).Concat(result.Failures.Select(value =>
            Path.GetFileName(value.Source) + ": " + Explain(value.Message))).ToArray();
        _report.Text = _status.Text + Environment.NewLine + Environment.NewLine + (details.Length > 0
            ? string.Join(Environment.NewLine, details) : L(result.Cancelled ? "Cancelled" : "NoWarnings"));
        AppendLog(_status.Text);
        foreach (var detail in details) AppendLog(detail);
    }

    private void ShowDiagnosticsError(Exception error)
    { _status.Text = L("Error"); _report.Text = Explain(error.Message); AppendLog(_report.Text); }
}
