using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services;
using AIHub.Services.ExternalAgents;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

internal sealed class ExternalAgentConnectionWindow : Window
{
    private readonly Func<string, string> _text;
    private readonly AgentConnectionSetup _setup = AgentConnectionSetup.Current();
    private readonly CancellationTokenSource _closing = new();
    private readonly WrapPanel _actions = new();
    private readonly TextBlock _status;
    private readonly Button _start;

    internal ExternalAgentConnectionWindow(Window owner, Func<string, string> text)
    {
        _text = text;
        Owner = owner;
        Title = text("ExternalAgents.Title");
        ShowInTaskbar = false;
        Width = Math.Min(690, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 48);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative)
        });
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "ExternalAgents.Connection");
        var frame = new DockPanel { Margin = new Thickness(24) };
        var body = new StackPanel();
        body.Children.Add(Label(text("ExternalAgents.Description")));
        AddSection("ExternalAgents.ProjectTitle", "ExternalAgents.ProjectExample");
        body.Children.Add(ScenarioList());
        AddSection("ExternalAgents.HelpTitle", "ExternalAgents.Help");
        AddSection("ExternalAgents.ConnectTitle", "ExternalAgents.ConnectHelp");
        var privacy = Label(text("ExternalAgents.Privacy"));
        privacy.Margin = new Thickness(0, 10, 0, 2);
        privacy.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        body.Children.Add(privacy);
        var connect = Action("ExternalAgents.ConnectCodex", "ExternalAgents.Codex");
        connect.Click += async (_, _) => await ConnectCodexAsync();
        var other = Action("ExternalAgents.Other", "ExternalAgents.Other");
        other.Click += (_, _) => RunAction(CopyInstructions);
        _start = Action("ExternalAgents.StartCodex", "ExternalAgents.Start");
        _start.Visibility = Visibility.Collapsed;
        _start.Click += (_, _) => RunAction(() => CodexPluginClient.Open(CodexPluginClient.NewChat(_text("ExternalAgents.Draft"))));
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(_actions);
        _status = Label("");
        _status.Visibility = Visibility.Collapsed;
        _status.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom);
        frame.Children.Add(footer);
        frame.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = frame;
        RunAction(() => { if (_setup.HasRegistration) _start.Visibility = Visibility.Visible; });
        Closed += (_, _) => _closing.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        void AddSection(string titleKey, string bodyKey)
        {
            var heading = Label(text(titleKey));
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 14, 0, 3);
            body.Children.Add(heading);
            body.Children.Add(Label(text(bodyKey)));
        }
    }

    private Expander ScenarioList()
    {
        var available = AgentProjects.NavigationEntries().ToArray();
        var rows = new StackPanel();
        var groups = available.ToLookup(scenario => Direction(scenario).Id);
        foreach (var direction in ScenarioNavigationCatalog.Nodes.Where(n => groups.Contains(n.Id)))
        {
            var heading = Label(_text(direction.TitleKey));
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 6, 0, 2);
            rows.Children.Add(heading);
            rows.Children.Add(Label(string.Join(" · ", groups[direction.Id].Select(scenario =>
            {
                var name = _text(scenario.TitleKey);
                if (scenario.Id is ScenarioNavigationCatalog.OfficeEditor or ScenarioNavigationCatalog.SpeechAssets)
                    return _text(ScenarioNavigationCatalog.Get(scenario.ParentId!).TitleKey) + " / " + name;
                return name;
            }))));
        }
        var list = new Expander
        {
            Header = string.Format(_text("ExternalAgents.Scenarios"), available.Length),
            Content = rows, Margin = new Thickness(0, 12, 0, 0), IsExpanded = true
        };
        AutomationProperties.SetAutomationId(list, "ExternalAgents.Scenarios");
        return list;

        static ScenarioNavigationNode Direction(ScenarioNavigationNode item)
        {
            while (item.ParentId is { } parent) item = ScenarioNavigationCatalog.Get(parent);
            return item;
        }
    }

    private TextBlock Label(string content)
    {
        var label = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return label;
    }

    private Button Action(string key, string automationId)
    {
        var button = new Button {
            Content = _text(key), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 4)
        };
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        AutomationProperties.SetAutomationId(button, automationId);
        _actions.Children.Add(button);
        return button;
    }

    private async Task ConnectCodexAsync()
    {
        _actions.IsEnabled = false;
        ShowStatus(_text("ExternalAgents.Registering"));
        try
        {
            _setup.PrepareMarketplace(_text("ExternalAgents.PluginSummary"));
            await CodexPluginClient.RegisterAsync(_setup, _closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            CodexPluginClient.Open(CodexPluginClient.PluginCard(_setup));
            _setup.RecordRegistration();
            _start.Visibility = Visibility.Visible;
            ShowStatus(_text("ExternalAgents.ConfirmInstall"));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { _actions.IsEnabled = true; }
    }

    private void CopyInstructions()
    {
        var path = _setup.CreateInstructions();
        Clipboard.SetDataObject(string.Format(_text("ExternalAgents.Clipboard"), path), true);
        ShowStatus(_text("ExternalAgents.Copied"));
    }

    private void RunAction(System.Action action)
    {
        try { action(); }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error)
    {
        if (error is FileNotFoundException { Message: "codex.exe" } || error is Win32Exception { NativeErrorCode: 1155 })
            ShowStatus(_text("ExternalAgents.CodexMissing"));
        else
            ShowStatus(string.Format(_text("ExternalAgents.Failed"), error.Message));
    }

    private void ShowStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = Visibility.Visible;
    }
}
