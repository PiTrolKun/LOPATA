using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private Button _search = null!;
    private bool _searchEnabled, _searchConnected = true, _searchInitializing, _searchOfflineAttempt;
    private AliceBrowserWindow? _alice;
    private readonly DispatcherTimer _searchBlink = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly Dictionary<string, (BlockUIContainer Block, Expander Expander, TextBox Text)> _searchViews = [];
    private UserContextService _profileContext = null!;

    private void InitializeSearch(Panel actions, UserContextService context)
    {
        _profileContext = context;
        try { _aliceWasSignedIn = _preferences.Load().AlicePreviouslySignedIn; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { _aliceWasSignedIn = false; }
        _search = Action(actions, "Search", () => _ = ToggleSearchAsync(), "🔍");
        AutomationProperties.SetName(_search, L("Search.Title"));
        _search.ToolTip = L("Search.Off");
        var menu = new ContextMenu(); menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        var external = new MenuItem { Header = L("Search.OpenExternal") };
        external.Click += (_, _) => Process.Start(new ProcessStartInfo(AliceBrowserWindow.HomeUrl) { UseShellExecute = true });
        var embedded = new MenuItem { Header = L("Search.ShowBrowser") };
        embedded.Click += (_, _) => _ = ShowAliceAsync();
        menu.Items.Add(external); menu.Items.Add(embedded); _search.ContextMenu = menu;
        _searchBlink.Tick += (_, _) => _search.Opacity = _search.Opacity == 1 ? .45 : 1;
        NetworkChange.NetworkAvailabilityChanged += SearchNetworkChanged;
        UpdateSearchIndicator();
    }

    private AliceBrowserWindow Alice()
    {
        if (_alice?.RequiresRestart == true) { _alice.Dispose(); _alice = null; }
        if (_alice is null)
        {
            _alice = new(L);
            _alice.ConnectivityChanged += connected => { _searchConnected = connected; if (connected) _searchOfflineAttempt = false; UpdateSearchIndicator(); };
        }
        return _alice;
    }

    private async Task ToggleSearchAsync()
    {
        if (_searchInitializing) return;
        if (_searchEnabled || _searchOfflineAttempt) { _searchEnabled = false; _searchOfflineAttempt = false; _operation?.Cancel(); UpdateSearchIndicator(); Recount(); return; }
        if (IsWorking) return;
        _searchInitializing = true; _search.IsEnabled = false;
        try
        {
            await Alice().EnsureAsync(ApplicationBackgroundOperations.ExitToken);
            var state = await Alice().SnapshotAsync();
            RememberAliceAuth(state.Auth);
            if (state.Auth == "guest" && !_preferences.Load().AliceGuestAcknowledged)
            {
                var choice = AskAliceSignIn();
                if (choice is null) return;
                if (choice == true) await Alice().ShowBrowserAsync(ApplicationBackgroundOperations.ExitToken);
                else _preferences.AcknowledgeAliceGuest();
            }
            _searchEnabled = true; _status.Text = L("Search.Transmission");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchFailure(ex); }
        finally { _searchInitializing = false; _search.IsEnabled = true; UpdateSearchIndicator(); Recount(); }
    }

    private async Task ShowAliceAsync()
    {
        try { await Alice().ShowBrowserAsync(ApplicationBackgroundOperations.ExitToken); }
        catch (Exception ex) { SearchFailure(ex); }
    }

    private bool? AskAliceSignIn()
    {
        bool? choice = null;
        var root = new StackPanel { Margin = new(18) };
        root.Children.Add(Label(L("Search.SignInPrompt")));
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        root.Children.Add(buttons);
        var window = new Window { Owner = DialogOwner, Title = L("Search.Title"), Content = root, Width = 540,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Resources = Resources,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        Action(buttons, "Search.SignIn", () => { choice = true; window.Close(); });
        Action(buttons, "Search.Guest", () => { choice = false; window.Close(); });
        window.ShowDialog(); return choice;
    }

    private void SearchFailure(Exception ex)
    {
        if (ex.Message == "Music.Poetry.Search.Offline") { _searchConnected = false; _searchOfflineAttempt = true; }
        _status.Text = L("Error") + " " + (ex.Message.StartsWith("Music.Poetry.", StringComparison.Ordinal) ? _l(ex.Message) : L("Search.BrowserFailed"));
        UpdateSearchIndicator();
    }

    private void SearchNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) => Dispatcher.BeginInvoke(new Action(() => {
        if (_viewClosed) return; _searchConnected = e.IsAvailable; UpdateSearchIndicator();
    }));

    private void UpdateSearchIndicator()
    {
        if (_search is null) return;
        var alert = !_searchConnected && (_searchEnabled || _searchOfflineAttempt);
        if (_searchEnabled || alert)
        {
            _search.Foreground = new SolidColorBrush(alert ? Color.FromRgb(235, 65, 65) : Color.FromRgb(45, 120, 255));
            if (!_searchBlink.IsEnabled) _searchBlink.Start();
        }
        else { _searchBlink.Stop(); _search.Opacity = 1; _search.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush"); }
        _search.ToolTip = L(alert ? "Search.Offline" : _searchEnabled ? "Search.On" : "Search.Off");
        AutomationProperties.SetHelpText(_search, _search.ToolTip.ToString());
    }

    private async Task PrepareSearchAsync(string turn, string language, MusicPoetryReasoningPreference preference,
        double creativity, CancellationToken token)
    {
        var exchange = new MusicPoetrySearchExchange { TurnId = turn };
        _session.SearchExchanges.Add(exchange); Persist(); RenderChat();
        _session.SearchRequest = new(true);
        try
        {
            _status.Text = L("Search.Preparing");
            var raw = await _runtime.GenerateAsync(_model!, _session, "", context => {
                _capacity = context.Capacity; Meter(context.Tokens, context.Estimated);
            }, token, false, _reasoningMode, null, language, preference, creativity);
            exchange.Question = MusicPoetrySearchProtocol.Question(raw);
            exchange.State = "ready"; Persist(); RenderChat();
            var browser = Alice(); await browser.NavigateChatAsync(_session.AliceChatUrl, token);
            var auth = await browser.SnapshotAsync();
            RememberAliceAuth(auth.Auth);
            var input = MusicPoetrySearchProtocol.AliceInput(exchange.Question, _session.Lyrics, language);
            _status.Text = L("Search.Waiting");
            var answer = await browser.AskAsync(input, state => {
                exchange.State = state;
                if (!Persist()) throw new IOException("Music.Poetry.SaveFailed");
                RenderChat();
            }, text => { exchange.Answer = text; _session.AliceChatUrl = browser.CurrentChatUrl; RenderChat(); _save.Start(); }, token);
            exchange.Answer = answer.Text; exchange.Links = answer.Links; exchange.State = "received";
            _session.AliceChatUrl = browser.CurrentChatUrl;
            _session.SearchRequest = new(false, exchange.Question, exchange.Answer, exchange.Links);
            Persist(); RenderChat();
        }
        catch (Exception ex)
        {
            exchange.State = ex is OperationCanceledException ? "interrupted" : "error";
            exchange.Error = ex.Message.StartsWith("Music.Poetry.", StringComparison.Ordinal) ? ex.Message : "Music.Poetry.Search.BrowserFailed";
            _session.AliceChatUrl = _alice?.CurrentChatUrl ?? _session.AliceChatUrl;
            if (ex.Message == "Music.Poetry.Search.Offline") { _searchConnected = false; UpdateSearchIndicator(); }
            Persist(); RenderChat(); throw;
        }
    }

    private bool _aliceWasSignedIn;
    private void RememberAliceAuth(string auth)
    {
        if (auth == "unknown") return;
        var signedIn = auth == "signed-in";
        if (!signedIn && _aliceWasSignedIn)
        {
            ProgramMessage("notice", L("Search.SignInExpired"), Guid.NewGuid().ToString("N"));
            RenderChat();
        }
        if (signedIn != _aliceWasSignedIn) _preferences.RememberAliceSignIn(signedIn);
        _aliceWasSignedIn = signedIn;
    }
    private (string Key, Block Block) SearchJournal(MusicPoetrySearchExchange exchange)
    {
        if (!_searchViews.TryGetValue(exchange.TurnId, out var view))
        {
            var text = Area("Search.Exchange", true); text.MaxHeight = 440;
            var expander = new Expander { Content = text, Header = L("Search.Journal"), IsExpanded = exchange.State is not ("completed" or "error" or "interrupted"), Margin = new(0, 4, 0, 10) };
            expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            view = (new BlockUIContainer(expander), expander, text); _searchViews[exchange.TurnId] = view;
        }
        var terminal = exchange.State is "completed" or "error" or "interrupted";
        if (terminal && view.Expander.Tag as string != exchange.State) view.Expander.IsExpanded = false;
        view.Expander.Tag = exchange.State;
        view.Expander.Header = L("Search.Journal") + " · " + L("Search.State." + exchange.State);
        var content = L("Search.Question") + "\n" + exchange.Question + "\n\n" + L("Search.Alice") + "\n" + exchange.Answer
            + (exchange.Links.Length > 0 ? "\n\n" + string.Join("\n", exchange.Links) : "")
            + (exchange.Error is not null ? "\n\n" + _l(exchange.Error) : "");
        if (view.Text.Text != content) view.Text.Text = content;
        return ("search:" + exchange.TurnId, view.Block);
    }

    private void CloseSearch()
    {
        NetworkChange.NetworkAvailabilityChanged -= SearchNetworkChanged;
        _searchBlink.Stop(); _alice?.Dispose(); _alice = null;
    }

    private string DisplayedSystem()
    {
        _session.UserDisplayName = _profileContext.DisplayName;
        if (_session.SearchRequest is not null) return MusicPoetrySearchProtocol.System(_session, _interfaceLanguage());
        if (!_searchEnabled || IsWorking) return MusicPoetryProtocol.System(_session, _guidance.Text, _interfaceLanguage());
        return MusicPoetrySearchProtocol.System(new MusicPoetrySession { Lyrics = _session.Lyrics,
            UserDisplayName = _session.UserDisplayName, SearchRequest = new(true) }, _interfaceLanguage());
    }
}
