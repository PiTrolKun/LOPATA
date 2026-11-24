using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services.ExternalAgents;

namespace AIHub;

public partial class MainWindow
{
    private readonly AgentPresenceStore _agentPresence = AgentPresenceStore.Current;
    private IReadOnlyList<InstalledAgentApp> _agentApps = [];
    private FileSystemWatcher? _agentWatcher;
    private int _agentChanged = 1;
    private bool _readingAgents, _showingAgent, _agentsClosed;
    private long _nextAgentRefresh;
    private AgentIdentity? _iconClient;
    private ImageSource? _agentIcon;

    private async void InitializeAgentIndicators()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => RefreshAgentIndicators();
        Activated += (_, _) => Interlocked.Exchange(ref _agentChanged, 1);
        Closed += (_, _) => { _agentsClosed = true; timer.Stop(); _agentWatcher?.Dispose(); };
        try
        {
            Directory.CreateDirectory(_agentPresence.DirectoryPath);
            _agentWatcher = new FileSystemWatcher(_agentPresence.DirectoryPath);
            _agentWatcher.Created += AgentFilesChanged;
            _agentWatcher.Changed += AgentFilesChanged;
            _agentWatcher.Deleted += AgentFilesChanged;
            _agentWatcher.Error += (_, _) => Interlocked.Exchange(ref _agentChanged, 1);
            _agentWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        timer.Start();
        try { _agentApps = await Task.Run(AgentAppDiscovery.Scan); }
        catch (Exception e) { Services.OwnedProcessRegistry.Log("agent_discovery_failed", "external_agents", detail: e.GetType().Name); }
        _iconClient = null;
        Interlocked.Exchange(ref _agentChanged, 1);
    }

    private void AgentFilesChanged(object sender, FileSystemEventArgs e) => Interlocked.Exchange(ref _agentChanged, 1);

    private async void RefreshAgentIndicators()
    {
        if (_readingAgents || _agentsClosed) return;
        bool changed = Interlocked.Exchange(ref _agentChanged, 0) != 0;
        bool reconcile = Environment.TickCount64 >= _nextAgentRefresh;
        if (!changed && !reconcile && !_showingAgent) return;
        if (reconcile) _nextAgentRefresh = Environment.TickCount64 + 15_000;
        _readingAgents = true;
        try
        {
            var apps = _agentApps;
            var work = await Task.Run(() => _agentPresence.Read().OrderByDescending(x => x.Running)
                .ThenByDescending(x => x.Updated).FirstOrDefault()?.Work);
            if (_agentsClosed) return;
            if (changed || reconcile)
            {
                var missing = await Task.Run(() => MissingAgentNames(apps));
                if (_agentsClosed) return;
                ScenarioNavigationPage.SetUnconnectedAgents(missing);
            }
            _showingAgent = work != null;
            if (work == null)
            {
                ExternalAgentActivityImage.Visibility = Visibility.Collapsed;
                return;
            }
            if (_iconClient != work.Client || _agentIcon == null)
            {
                var product = AgentClients.Match(work.Client.Name);
                var app = apps.FirstOrDefault(a => product != null && a.Product == product)
                    ?? apps.FirstOrDefault(a => string.Equals(a.Name, work.Client.Name, StringComparison.OrdinalIgnoreCase));
                var image = await Task.Run(() => AgentAppIcon.Load(work.Client, app));
                if (_agentsClosed) return;
                _agentIcon = image;
                // If discovery finished during the lookup, retry with the new installation list.
                _iconClient = ReferenceEquals(apps, _agentApps) ? work.Client : null;
            }
            var name = work.Client.Name.Length > 0 ? work.Client.Name : L("ExternalAgents.Activity.Unknown");
            var key = work.Tool switch
            {
                "project_create" => "Create", "project_scenarios" or "project_instructions" => "Scenarios",
                "logs_search" or "logs_read" => "Logs", "environment_prepare" => "Prepare",
                "environment_check" => "Check", "environment_cancel" => "Cancel", "environment_status" => "Status",
                _ => "Work"
            };
            var caption = string.Format(L("ExternalAgents.Activity." + key), name);
            ExternalAgentActivityImage.Source = _agentIcon;
            ExternalAgentActivityImage.ToolTip = caption;
            AutomationProperties.SetName(ExternalAgentActivityImage, caption);
            ExternalAgentActivityImage.Visibility = Visibility.Visible;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ExternalAgentActivityImage.Visibility = Visibility.Collapsed;
        }
        finally { _readingAgents = false; }
    }

    private string MissingAgentNames(IReadOnlyList<InstalledAgentApp> apps)
    {
        var installed = apps.Select(a => a.Product).OfType<AgentProduct>().Distinct();
        return string.Join(", ", installed.Where(p => !_agentPresence.HasConnected(p.Id)
            && (p.Id != "codex" || CodexPluginConnection.ReadCurrent() != true)).Select(p => p.Title).Order());
    }
}
