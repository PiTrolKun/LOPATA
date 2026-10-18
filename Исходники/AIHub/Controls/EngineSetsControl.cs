using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed record EngineSetDisplay(string Id, string Date, string DescriptionKey, string LimitKey,
    string TechnicalDetails, bool Active, bool Recommended, bool Installed, long DownloadBytes, long InstalledBytes);

/// <summary>Presentation only; verification, acquisition and activation are owned by the engine-set service.</summary>
public sealed class EngineSetsControl : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    private readonly Button _refresh = new(), _cancel = new();
    private Func<string, string> _l = key => key;
    private IReadOnlyList<EngineSetDisplay> _sets = [];
    private bool _busy;
    private string? _statusKey;
    public event Func<Task>? RefreshRequested;
    public event Func<string, bool, Task>? ActionRequested;
    public event Action? CancelRequested;

    public EngineSetsControl()
    {
        var root = new StackPanel();
        var actions = new WrapPanel();
        ConfigureButton(_refresh, "EngineSets.Refresh", () => InvokeAsync(RefreshRequested));
        ConfigureButton(_cancel, "EngineSets.Cancel", () => { CancelRequested?.Invoke(); return Task.CompletedTask; });
        actions.Children.Add(_refresh); actions.Children.Add(_cancel);
        root.Children.Add(actions); root.Children.Add(_status); root.Children.Add(_rows);
        Content = root;
        AutomationProperties.SetAutomationId(this, "Settings.EngineSets");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
    }

    public void Localize(Func<string, string> localize)
    {
        _l = localize;
        _refresh.Content = _l("EngineSets.Refresh"); _cancel.Content = _l("EngineSets.Cancel");
        AutomationProperties.SetName(_refresh, _l("EngineSets.Refresh"));
        AutomationProperties.SetName(_cancel, _l("EngineSets.Cancel"));
        if (_statusKey is not null) _status.Text = _l(_statusKey);
        Render();
    }

    public void ShowSets(IReadOnlyList<EngineSetDisplay> sets) { _sets = sets; Render(); }
    public void Status(string key) { _statusKey = key; _status.Text = _l(key); }
    public void Progress(string message) { _statusKey = null; _status.Text = message; }
    public void Busy(bool busy) { _busy = busy; Render(); }
    public Task RunActionAsync(string id, bool download) =>
        InvokeAsync(() => ActionRequested?.Invoke(id, download) ?? Task.CompletedTask);

    private void Render()
    {
        _refresh.IsEnabled = !_busy; _cancel.IsEnabled = _busy;
        _rows.Children.Clear();
        foreach (var set in _sets)
        {
            var content = new StackPanel();
            var flags = new List<string>();
            if (set.Active) flags.Add(_l("EngineSets.Active"));
            if (set.Recommended) flags.Add(_l("EngineSets.Recommended"));
            flags.Add(_l(set.Installed ? "EngineSets.Installed" : "EngineSets.Available"));
            content.Children.Add(Label($"{set.Id} — {set.Date} · {string.Join(" · ", flags)}", true));
            content.Children.Add(Label(_l(set.DescriptionKey)));
            content.Children.Add(Label(string.Format(_l("EngineSets.Size"),
                Math.Ceiling(set.DownloadBytes / 1048576d), Math.Ceiling(set.InstalledBytes / 1048576d))));
            var details = new StackPanel(); details.Children.Add(Label(_l(set.LimitKey)));
            details.Children.Add(Label(set.TechnicalDetails));
            content.Children.Add(new Expander { Header = _l("EngineSets.Details"), IsExpanded = false,
                Content = details, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch });
            var action = new Button { Content = _l(set.Installed ? "EngineSets.Use" : "EngineSets.Download"),
                IsEnabled = !_busy && (!set.Active || !set.Installed), Margin = new(0, 10, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            action.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
            AutomationProperties.SetAutomationId(action, "EngineSets.Action." + set.Id);
            action.Click += async (_, _) => await RunActionAsync(set.Id, !set.Installed);
            content.Children.Add(action);
            var card = new Border { Child = content, Padding = new(16), Margin = new(0, 0, 0, 14),
                CornerRadius = new(12), BorderThickness = new(1) };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            card.SetResourceReference(Border.BorderBrushProperty, set.Active ? "AccentBrush" : "LineBrush");
            _rows.Children.Add(card);
        }
    }

    private static TextBlock Label(string text, bool heading = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8),
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, heading ? "TextPrimaryBrush" : "TextSecondaryBrush");
        return label;
    }

    private void ConfigureButton(Button button, string id, Func<Task> action)
    {
        button.Margin = new(0, 0, 10, 0);
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await action();
    }

    private async Task InvokeAsync(Func<Task>? action)
    {
        if (_busy || action is null) return;
        Busy(true);
        try { await action(); }
        catch (OperationCanceledException) { Status("EngineSets.Cancelled"); }
        catch (System.Net.Http.HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        { Status("EngineSets.NotPublished"); }
        catch (Exception error)
        {
            AIHub.Services.OwnedProcessRegistry.Log("engine_set_ui_failed", "EngineSets", detail: error.GetType().Name + ": " + error.Message);
            Status("EngineSets.Failed");
        }
        finally { Busy(false); }
    }
}
