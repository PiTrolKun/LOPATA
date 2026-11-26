using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Lopata.Inspector;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub;

public partial class MainWindow
{
    private CheckBox? _inspectorSwitch;
    private TextBlock? _inspectorStatus;
    private bool _refreshingInspector;
    private InspectorState? _inspectorState;
    private bool _inspectorRunning;
    private Task _inspectorTransition = Task.CompletedTask;

    internal void InitializeSystemInspector()
    {
        ApplicationInspector.Initialize();
        Window? ActiveWindow() => _processShutdownPending ? null : System.Windows.Application.Current.Windows
            .OfType<Window>().FirstOrDefault(window => window.IsVisible && window.IsActive);
        ApplicationInspector.CanConfirmPreflight = token => Dispatcher.InvokeAsync(
            () => ActiveWindow() is not null, DispatcherPriority.Background, token).Task;
        ApplicationInspector.ConfirmPreflight = (result, token) => Dispatcher.InvokeAsync(() =>
        {
            if (ActiveWindow() is not { } owner) return false;
            if (result.NoticeId is { } notice)
                try { ApplicationInspector.Store?.ConfirmNoticeDisplayed(notice); }
                catch (Exception error) { ApplicationInspector.LogUnavailable(error); }
            var panel = new StackPanel();
            panel.Children.Add(LiteraryUi.Text(result.Warning ?? ""));
            var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(buttons);
            var dialog = LiteraryEditorDialogs.Create(owner, L("Inspector.Title"), panel, 500);
            var proceed = LiteraryUi.Button(L("Inspector.Preflight.Run"), () => dialog.DialogResult = true);
            var defer = LiteraryUi.Button(L("Inspector.Preflight.Defer"), () => dialog.DialogResult = false);
            proceed.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetAutomationId(proceed, "Inspector.Preflight.Run");
            AutomationProperties.SetAutomationId(defer, "Inspector.Preflight.Defer");
            defer.IsCancel = true;
            defer.IsDefault = true;
            buttons.Children.Add(proceed);
            buttons.Children.Add(defer);
            return BackgroundUserDecision.ShowDialog(dialog, owner, token) == true;
        }, DispatcherPriority.Normal, token).Task;
        QueueInspectorChange();
    }

    private Border CreateInspectorSettings(SettingsCardFactory cards)
    {
        _inspectorSwitch = cards.Switch("Inspector.Title", "Settings.PcInspector", enabled =>
        {
            if (!_refreshingInspector)
                QueueInspectorChange(store => store.SetEnabled(enabled));
        });
        var explanation = new Expander
        {
            Header = cards.Label("Inspector.Learning.Title"),
            Content = cards.Label("Inspector.Learning.Help"),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 5)
        };
        AutomationProperties.SetAutomationId(explanation, "Settings.PcInspectorLearning");
        _inspectorStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _inspectorStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var card = cards.Card(_inspectorSwitch, cards.Label("Inspector.Help"), explanation, _inspectorStatus);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await ReadInspectorSettingsAsync();
        card.IsVisibleChanged += (_, _) =>
        {
            if (card.IsVisible) { timer.Start(); _ = ReadInspectorSettingsAsync(); }
            else timer.Stop();
        };
        Closed += (_, _) => timer.Stop();
        return card;
    }

    private void QueueInspectorAudit(bool fullAudit) =>
        QueueInspectorChange(store => store.SetFullAudit(fullAudit));

    private void QueueInspectorChange(Action<InspectorStore>? change = null)
    {
        if (ApplicationInspector.Store is not { } store || Environment.ProcessPath is not { } executable) return;
        var previous = _inspectorTransition;
        _inspectorTransition = Task.Run(async () =>
        {
            await previous;
            try
            {
                change?.Invoke(store);
                InspectorStartup.Reconcile(AppDataPaths.BaseDirectory, executable);
            }
            catch (Exception error) { ApplicationInspector.LogUnavailable(error); }
        });
        _ = RefreshAfterInspectorChangeAsync(_inspectorTransition);
    }

    private async Task RefreshAfterInspectorChangeAsync(Task transition)
    {
        RefreshInspectorSettings();
        await transition;
        await ReadInspectorSettingsAsync();
    }

    private async Task ReadInspectorSettingsAsync()
    {
        if (!_inspectorTransition.IsCompleted || ApplicationInspector.Store is not { } store) return;
        try
        {
            var pending = _inspectorTransition;
            var status = await Task.Run(() => (store.ReadState(), store.IsWorkerRunning()));
            if (pending != _inspectorTransition) return;
            (_inspectorState, _inspectorRunning) = status;
        }
        catch (Exception error) { ApplicationInspector.LogUnavailable(error); }
        RefreshInspectorSettings();
    }

    private void RefreshInspectorSettings()
    {
        if (_inspectorSwitch is null || _inspectorStatus is null) return;
        var fullAudit = _inspectorState?.FullAudit ?? _appSettings.Behavior.ComponentAuditEnabled;
        var enabled = fullAudit || _inspectorState?.Enabled != false;
        var changing = !_inspectorTransition.IsCompleted;
        _refreshingInspector = true;
        try
        {
            _inspectorSwitch.IsChecked = enabled;
            _inspectorSwitch.IsEnabled = !fullAudit && !changing && _inspectorState is not null;
            _inspectorStatus.Text = L(changing ? "Inspector.Status.Updating"
                : enabled && !_inspectorRunning ? "Inspector.Status.Unavailable"
                : fullAudit ? "Inspector.Status.FullAudit"
                : enabled ? "Inspector.Status.Running" : "Inspector.Status.Disabled");
        }
        finally { _refreshingInspector = false; }
    }
}
