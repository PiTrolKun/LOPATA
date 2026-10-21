using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private MusicCloudProvider _cloudProvider = MusicCloudProviders.Alice;
    private readonly Dictionary<string, CloudAssistantBrowserWindow> _cloudBrowsers = [];
    private CancellationTokenSource? _cloudInitialization;

    private string ProviderName(string id) => id == MusicCloudProviders.AliceId ? L("Cloud.Alice") : MusicCloudProviders.Get(id).Name;
    private string CloudText(string key, string? id = null) => string.Format(L(key), ProviderName(id ?? _cloudProvider.Id));

    private CloudAssistantBrowserWindow CloudBrowser()
    {
        if (_cloudBrowsers.TryGetValue(_cloudProvider.Id, out var existing))
        {
            if (!existing.RequiresRestart) return existing;
            existing.Dispose(); _cloudBrowsers.Remove(_cloudProvider.Id);
        }
        var provider = _cloudProvider;
        var browser = new CloudAssistantBrowserWindow(L, provider);
        browser.ConnectivityChanged += connected => {
            if (_cloudProvider.Id != provider.Id) return;
            _searchConnected = connected;
            if (connected) _searchOfflineAttempt = false;
            UpdateSearchIndicator();
        };
        _cloudBrowsers.Add(provider.Id, browser);
        return browser;
    }

    private void InitializeCloudMenu()
    {
        var menu = new ContextMenu(); menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        _search.ContextMenu = menu;
        menu.Opened += (_, _) => {
            menu.Items.Clear();
            try
            {
                var defaultId = _preferences.Load().CloudAssistantId;
                if (!_searchEnabled && !_searchInitializing && defaultId is not null) _cloudProvider = MusicCloudProviders.Get(defaultId);
                var preferred = new MenuItem { Header = L("Cloud.Default"), IsEnabled = !IsWorking && !_searchInitializing };
                foreach (var id in new string?[] { MusicCloudProviders.AliceId, MusicCloudProviders.DeepSeekId, null })
                {
                    var item = new MenuItem { Header = id is null ? L("Cloud.AlwaysAsk") : ProviderName(id),
                        IsCheckable = true, IsChecked = defaultId == id };
                    item.Click += (_, _) => {
                        try { _preferences.SetCloudAssistant(id);
                            if (!_searchEnabled && !_searchInitializing && id is not null) _cloudProvider = MusicCloudProviders.Get(id);
                            UpdateSearchIndicator(); }
                        catch (Exception ex) { SearchFailure(ex); }
                    };
                    preferred.Items.Add(item);
                }
                menu.Items.Add(preferred); menu.Items.Add(new Separator());
                var external = new MenuItem { Header = CloudText("Search.OpenExternal") };
                external.Click += (_, _) => Process.Start(new ProcessStartInfo(_cloudProvider.HomeUrl) { UseShellExecute = true });
                var embedded = new MenuItem { Header = CloudText("Search.ShowBrowser") };
                embedded.Click += (_, _) => _ = ShowCloudAsync();
                menu.Items.Add(external); menu.Items.Add(embedded);
            }
            catch (Exception ex) { SearchFailure(ex); }
        };
    }

    private string? ChooseCloudProvider()
    {
        string? choice = null;
        var content = new StackPanel { Margin = new(18) };
        content.Children.Add(Label(L("Cloud.ChooseDescription")));
        var window = new Window { Owner = DialogOwner, Title = L("Cloud.ChooseTitle"), Content = content,
            Width = 580, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Resources = Resources };
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(window, "Music.Poetry.Cloud.Choose");
        var recommended = MusicCloudProviders.Recommended(_interfaceLanguage());
        foreach (var provider in MusicCloudProviders.All)
        {
            var card = new StackPanel { Margin = new(10) };
            card.Children.Add(Label(ProviderName(provider.Id) + (recommended == provider.Id ? " · " + L("Cloud.Recommended") : "")));
            card.Children.Add(Label(L(provider.RequiresSignIn ? "Cloud.DeepSeekDetails" : "Cloud.AliceDetails")));
            var button = Action(content, "Cloud.Choose." + provider.Id, () => { choice = provider.Id; window.Close(); });
            button.Content = card; button.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
            button.Margin = new(0, 8, 0, 0); button.Padding = new(0);
            if (provider.Id == recommended) { button.SetResourceReference(BorderBrushProperty, "AccentBrush"); button.BorderThickness = new(2); }
            AutomationProperties.SetName(button, ProviderName(provider.Id));
        }
        content.Children.Add(Label(L("Cloud.CoreScore")));
        content.Children.Add(Label(L("Cloud.ScoreNotice")));
        content.Children.Add(Label(L("Cloud.TransmissionNotice")));
        Action(content, "Cloud.Cancel", window.Close).HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        window.ShowDialog(); return choice;
    }

    private async Task ShowCloudAsync()
    {
        try { await CloudBrowser().ShowBrowserAsync(ApplicationBackgroundOperations.ExitToken); }
        catch (Exception ex) { SearchFailure(ex); }
    }

    private string CloudChatUrl => _cloudProvider.Id == MusicCloudProviders.AliceId ? _session.AliceChatUrl : _session.DeepSeekChatUrl;
    private void RememberCloudUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (_cloudProvider.Id == MusicCloudProviders.AliceId) _session.AliceChatUrl = url;
        else _session.DeepSeekChatUrl = url;
    }
}
