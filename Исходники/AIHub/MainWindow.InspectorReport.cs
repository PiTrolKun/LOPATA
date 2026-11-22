using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Lopata.Inspector;
using Lopata.Island;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub;

public partial class MainWindow
{
    private static readonly JsonSerializerOptions InspectorReportJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private Window? _inspectorReportWindow;

    internal async Task OpenInspectorReportAsync()
    {
        OpenFromSecondInstance();
        if (_inspectorReportWindow is { } previous) { previous.Activate(); return; }
        if (ApplicationInspector.Store is not { } store) return;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_backgroundLifetime.Token);
        var status = new TextBlock { Text = L("Inspector.Report.Loading"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var body = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12) };
        body.SetResourceReference(TextBox.BackgroundProperty, "PanelBrush");
        body.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetName(body, L("Inspector.Report.TechnicalData"));
        var close = new Button { Content = L("Common.Close"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var layout = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(status);
        layout.Children.Add(close);
        layout.Children.Add(body);
        var window = new Window { Owner = this, Title = L("Inspector.Report.Title"), Content = layout,
            Width = 720, Height = 550, MinWidth = 440, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Resources.MergedDictionaries.Add(Resources);
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => _inspectorReportWindow = null;
        void CancelReport(object? sender, EventArgs args) => lifetime.Cancel();
        window.Closed += CancelReport;
        _inspectorReportWindow = window;
        try
        {
            window.Show();
            var report = await Task.Run(store.ReadReport, lifetime.Token);
            var evidence = BuildInspectorEvidence(report);
            body.Text = evidence;
            var source = Path.Combine(store.DirectoryPath, "latest-report.json");
            bool sourceSaved = false;
            try
            {
                await SaveInspectorSourceAsync(source, report, lifetime.Token);
                sourceSaved = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { ApplicationInspector.LogUnavailable(error); }
            if (report.Events.Length == 0 && report.Samples.Length == 0)
            {
                status.Text = L("Inspector.Report.Empty");
                return;
            }
            status.Text = L("Inspector.Report.Analyzing");
            var result = await ExplainInspectorReportAsync(report, evidence, lifetime.Token);
            status.Text = L(!sourceSaved && result.Status == "Inspector.Report.CoreMissing"
                ? "Inspector.Report.Unavailable" : result.Status);
            if (!string.IsNullOrWhiteSpace(result.Answer))
                body.Text = result.Answer + "\n\n" + L("Inspector.Report.TechnicalData") + "\n" + evidence;
            if (result.Status == "Inspector.Report.Ready" || sourceSaved && result.Status == "Inspector.Report.CoreMissing")
                PublishInspectorReportResult(result.Status == "Inspector.Report.Ready");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ApplicationInspector.LogUnavailable(error);
            status.Text = L("Inspector.Report.Unavailable");
        }
        finally { window.Closed -= CancelReport; }
    }

    private static void PublishInspectorReportResult(bool success) => ApplicationInspector.Publish(new()
    {
        OperationId = Guid.NewGuid().ToString("N"),
        Scenario = "pc-inspector-report",
        Kind = InspectorEventKind.ReportFinished,
        Stage = success ? "report_ready" : "report_core_unavailable",
        Outcome = success ? InspectorOutcome.Success : InspectorOutcome.Failure
    });

    internal static string BuildInspectorEvidence(InspectorReport report) =>
        JsonSerializer.Serialize(new
        {
            Events = report.Events.TakeLast(24),
            Samples = report.Samples.TakeLast(6),
            Notices = ReportNotices(report, 12),
            Experience = report.Experience.TakeLast(10).Select(item => new { item.Model, item.Backend,
                item.Width, item.Height, item.Warm, item.DurationSeconds, item.Outcome, item.PeakDedicatedBytes,
                item.PeakSharedBytes, item.Pressure })
        }, InspectorReportJson);

    private async Task<(string Status, string? Answer)> ExplainInspectorReportAsync(
        InspectorReport report, string evidence, CancellationToken token)
    {
        if (_backgroundOperations is not { } controller || controller.BlocksNewWork
            || ScenarioNavigationPage.LetItOutChat?.IsWorking == true) return ("Inspector.Report.Busy", null);
        using var operation = controller.BeginAuxiliary("Inspector.Report.Busy");
        using var modelStorage = await ModelStorageWorkGate.Current.EnterWorkAsync(token);
        using var audit = ComponentAudit.CaptureOperation();
        await IslandLink.WaitForCoreRetirementAsync(AppContext.BaseDirectory, token);
        var checkedModel = await Task.Run(() => _coreModelManager.Check(_storageSettings), token);
        if (checkedModel.Availability != CoreModelAvailability.Installed || checkedModel.ModelPath is not { } path)
            return ("Inspector.Report.CoreMissing", null);
        await EngineSetService.Shared.InitializeAsync(token);
        using var runtime = new LlamaServerRuntimeService(_userContextService);
        if (!runtime.IsAvailable) return ("Inspector.Report.CoreMissing", null);
        string? anomaly = null;
        try { anomaly = await InspectorAnomalyAnalysis.AnalyzeAsync(report, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { ApplicationInspector.LogUnavailable(error); }
        var model = new DebugModelInfo { Name = CoreModelManager.CoreModelDisplayName, Path = path,
            IsCoreModel = true, Format = "gguf", Role = "chat" };
        var prompt = L("Inspector.Report.Prompt") + "\n\n" + evidence;
        if (!string.IsNullOrWhiteSpace(anomaly)) prompt += "\n\n" + L("Inspector.Report.Anomalies") + "\n" + anomaly;
        var answer = await runtime.GenerateAsync(model, [], prompt, _ => { }, token);
        return ("Inspector.Report.Ready", answer);
    }

    private static async Task SaveInspectorSourceAsync(string path, InspectorReport report, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Application = "LOPATA",
            Version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
            Scenario = "pc-inspector",
            CreatedUtc = DateTimeOffset.UtcNow,
            Events = report.Events.TakeLast(256),
            Samples = report.Samples.TakeLast(256),
            Notices = ReportNotices(report, 64)
        }, InspectorReportJson);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<object> ReportNotices(InspectorReport report, int count) =>
        report.Notices.Where(notice => notice.Shown).TakeLast(count)
            .Select(notice => new { notice.TimestampUtc, notice.OperationId, notice.Text, notice.Outcome });
}
