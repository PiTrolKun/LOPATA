using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MailHub;
using AIHub.Services.VideoEditor;
using Lopata.MailHub;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Label = System.Windows.Controls.Label;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.MailHub;

internal sealed partial class MailHubWindow : Window
{
    private readonly Func<string, string> _localize;
    private readonly MailHubFiles _files;
    private readonly MailHubConfiguration _configuration;
    private readonly MailHubPlayback _playback;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _nowPlaying = new();
    private readonly ListBox _accounts = new() { MinHeight = 180 };
    private readonly Button _play = new(), _delete = new(), _stop = new(), _check = new(), _suspend = new();
    private MailHubSettings _settings;
    private readonly ComboBox _models = new();
    private readonly TextBlock _voice = new();
    private Guid[] _reconnectAccounts = [];

    internal MailHubWindow(Window owner, Func<string, string> localize, MailHubFiles? files = null)
    {
        _localize = localize;
        _files = files ?? new(AppDataPaths.BaseDirectory);
        _configuration = new(_files.Root);
        _settings = _configuration.Load();
        _playback = new(_files, this, localize, key => _status.Text = localize(key), ApplyTheme);
        Prepare(this, owner, T("Title"), 1020, 720);
        AutomationProperties.SetAutomationId(this, "MailHub.Window");

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(18) };
        var summary = PlayerPanel();
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);
        var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
        tabs.Items.Add(new TabItem { Header = T("Accounts"), Content = AccountsPanel() });
        tabs.Items.Add(new TabItem { Header = T("Schedule"), Content = SchedulePanel() });
        tabs.Items.Add(new TabItem { Header = T("Models"), Content = SettingsPanel() });
        tabs.Items.Add(new TabItem { Header = T("Policy"), Content = PromptPanel() });
        root.Children.Add(tabs);
        Content = root;
        _refresh.Tick += (_, _) => RefreshState();
        _playback.Changed += RefreshState;
        Loaded += async (_, _) => await LoadModelsAsync();
        Closed += (_, _) => { _refresh.Stop(); _playback.Changed -= RefreshState; _playback.Dispose(); };
        _refresh.Start();
        RefreshAccounts();
        RefreshState();
    }

    internal static void Prepare(Window window, Window owner, string title, int width, int height)
    {
        window.Owner = owner;
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.MinWidth = Math.Min(width, 540);
        window.MinHeight = Math.Min(height, 520);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Resources.MergedDictionaries.Add(owner.Resources);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/MailHub/MailHubResources.xaml", UriKind.Relative)
        });
        ApplyTheme(window);
    }

    private static void ApplyTheme(Window window)
    {
        var background = DependencyPropertyDescriptor.FromProperty(BackgroundProperty, typeof(Window));
        void UpdateCaption(object? sender, EventArgs e)
        {
            if (window.Background is SolidColorBrush brush)
            {
                var color = brush.Color;
                WindowTitleBarThemeService.Apply(window, .2126 * color.R + .7152 * color.G + .0722 * color.B < 128);
            }
        }
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        window.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        window.SourceInitialized += UpdateCaption;
        background.AddValueChanged(window, UpdateCaption);
        window.Closed += (_, _) => background.RemoveValueChanged(window, UpdateCaption);
    }

    private string T(string key) => _localize("MailHub." + key);
    private void Save(MailHubSettings proposed)
    {
        try { _configuration.Save(proposed); _settings = proposed; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = T("FileFailed"); }
    }

    private FrameworkElement PlayerPanel()
    {
        var body = new StackPanel();
        body.Children.Add(_nowPlaying);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        Attach(actions, _play, "Play", () => { if (_files.SafeLatest(_playback.PlayingId) is { } report) _playback.Toggle(report); });
        Attach(actions, _stop, "Stop", _playback.Stop);
        Attach(actions, _delete, "Delete", () => { if (_files.SafeLatest(_playback.PlayingId) is { } report) _playback.Delete(report); });
        Attach(actions, _check, "CheckNow", () =>
        {
            try { if (!_files.TryRequest(_settings.BindToIsland, DateTimeOffset.Now)) _status.Text = T("BindingClosed"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _status.Text = T("FileFailed"); }
        });
        Attach(actions, _suspend, "Suspend", SwitchPause);
        body.Children.Add(actions);
        body.Children.Add(_status);
        return body;
    }

    private FrameworkElement AccountsPanel()
    {
        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(_accounts);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Attach(actions, new Button(), "Add", () => EditAccount(null));
        Attach(actions, new Button(), "Edit", () => { if (_accounts.SelectedIndex >= 0) EditAccount(_settings.Accounts[_accounts.SelectedIndex]); });
        Attach(actions, new Button(), "MoveUp", () => MoveAccount(-1));
        Attach(actions, new Button(), "MoveDown", () => MoveAccount(1));
        Attach(actions, new Button(), "Disconnect", Disconnect);
        body.Children.Add(actions);
        return body;
    }

    private void EditAccount(MailHubAccount? original)
    {
        var dialog = new MailHubAccountWindow(this, _localize, _configuration, original);
        if (dialog.ShowDialog() != true || dialog.Result is not { } account || dialog.Secret is not { } secret) return;
        try
        {
            var previousSecret = _configuration.ReadSecret(account.Id);
            _configuration.WriteSecret(account.Id, secret);
            var accounts = _settings.Accounts.ToList();
            int index = accounts.FindIndex(item => item.Id == original?.Id);
            if (index >= 0) accounts[index] = account;
            else { index = accounts.Count; accounts.Add(account); }
            var proposed = _settings with { Accounts = accounts.ToArray() };
            Save(proposed);
            if (_settings != proposed)
            {
                if (previousSecret is null) _configuration.RemoveSecret(account.Id);
                else _configuration.WriteSecret(account.Id, previousSecret);
                return;
            }
            _files.Update(state => state with { ReconnectAccounts = state.ReconnectAccounts.Where(id => id != original?.Id).ToArray() });
            _reconnectAccounts = _files.Load().ReconnectAccounts;
            RefreshAccounts();
            _accounts.SelectedIndex = index;
            if (original is not null && original.Id != account.Id) _configuration.RemoveSecret(original.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _status.Text = T("FileFailed"); }
    }

    private void Disconnect()
    {
        if (_accounts.SelectedIndex is var index && index < 0) return;
        var account = _settings.Accounts[index];
        Save(_settings with { Accounts = _settings.Accounts.Where(item => item.Id != account.Id).ToArray() });
        if (_settings.Accounts.All(item => item.Id != account.Id))
        {
            try { _configuration.RemoveSecret(account.Id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status.Text = T("FileFailed"); }
        }
        RefreshAccounts();
    }

    private void MoveAccount(int offset)
    {
        int from = _accounts.SelectedIndex;
        int to = from + offset;
        if (from < 0 || to < 0 || to >= _settings.Accounts.Length) return;
        var reordered = _settings.Accounts.ToArray();
        (reordered[to], reordered[from]) = (reordered[from], reordered[to]);
        Save(_settings with { Accounts = reordered });
        RefreshAccounts();
        _accounts.SelectedIndex = to;
    }

    private void RefreshAccounts()
    {
        int selected = _accounts.SelectedIndex;
        _accounts.ItemsSource = _settings.Accounts.Select(account => account.Address + "  ·  " + string.Join(", ", account.Folders)
            + (_reconnectAccounts.Contains(account.Id) ? "  ·  " + T("Reconnect") : "")).ToArray();
        if (_settings.Accounts.Length > 0) _accounts.SelectedIndex = Math.Clamp(selected, 0, _settings.Accounts.Length - 1);
    }

    private FrameworkElement SettingsPanel()
    {
        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new Label { Content = T("ChatModel"), Target = _models });
        body.Children.Add(_models);
        _models.DisplayMemberPath = nameof(DebugModelInfo.Name);
        var configure = new Button();
        var voiceRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
        Attach(voiceRow, configure, "Narrator", ConfigureVoice);
        voiceRow.Children.Add(_voice);
        body.Children.Add(voiceRow);
        RefreshVoice();

        var attachments = new CheckBox { Content = T("Attachments"), IsChecked = _settings.MentionAttachments };
        attachments.Click += (_, _) => Save(_settings with { MentionAttachments = attachments.IsChecked == true });
        body.Children.Add(attachments);
        var empty = new ComboBox { ItemsSource = new[] { T("Skip"), T("Notify"), T("Speak") }, SelectedIndex = (int)_settings.EmptyResult };
        body.Children.Add(new Label { Content = T("WhenEmpty"), Target = empty });
        body.Children.Add(empty);
        empty.SelectionChanged += (_, _) => { if (empty.SelectedIndex >= 0) Save(_settings with { EmptyResult = (MailHubEmptyResult)empty.SelectedIndex }); };
        var days = new TextBox { Text = _settings.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        body.Children.Add(new Label { Content = T("Retention"), Target = days });
        body.Children.Add(days);
        days.LostKeyboardFocus += (_, _) =>
        {
            if (int.TryParse(days.Text, out var value) && value is >= 0 and <= 3650) Save(_settings with { RetentionDays = value });
            else days.Text = _settings.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        };
        return new ScrollViewer { Content = body };
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var available = await Task.Run(MailHubModels.Discover);
            if (!IsLoaded) return;
            _models.ItemsSource = available;
            var selected = _settings.ChatModel ?? MailHubModels.DefaultModel(available);
            _models.SelectedItem = available.FirstOrDefault(model =>
                string.Equals(model.Path, selected, StringComparison.OrdinalIgnoreCase));
            _models.SelectionChanged += (_, _) =>
            {
                if (_models.SelectedItem is DebugModelInfo model) Save(_settings with { ChatModel = model.Path });
            };
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("mail_hub_models_failed", "MailHub", detail: error.GetType().Name);
            _status.Text = T("ModelRequired");
        }
    }

    private void ConfigureVoice()
    {
        var current = _settings.Narrator ?? MailHubModels.DefaultNarrator(MailHubScheduler.InterfaceLanguage());
        var initial = new VideoNarratorChoice(current.Provider, current.Language, current.Rate, current.Options);
        var window = new VideoVoiceWindow(this, _localize, initial, true, prepare: null);
        ApplyTheme(window);
        if (window.ShowDialog() == true && window.Choice is { } voice)
        {
            Save(_settings with { Narrator = new(voice.Id, voice.Language, voice.Rate, voice.Options) });
            RefreshVoice();
        }
    }

    private void RefreshVoice()
    {
        var narrator = _settings.Narrator ?? MailHubModels.DefaultNarrator(MailHubScheduler.InterfaceLanguage());
        _voice.Text = VideoNarratorCatalog.Get(narrator.Provider).Name + " · " + narrator.Options.Speaker;
    }

    private FrameworkElement PromptPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        void AddProtected(string text)
        {
            if (text.Length == 0) return;
            var policy = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3)
            };
            AutomationProperties.SetName(policy, T("Policy"));
            panel.Children.Add(policy);
        }

        TextBox AddEditor(string key, string value)
        {
            var answer = new TextBox
            {
                Text = value, MaxLength = 2000, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MinHeight = 58
            };
            answer.SetResourceReference(ForegroundProperty, "ChatUserTextBrush");
            answer.SetResourceReference(BorderBrushProperty, "AccentBrush");
            AutomationProperties.SetName(answer, T(key));
            AutomationProperties.SetAutomationId(answer, "MailHub." + key);
            panel.Children.Add(new Label { Content = T(key), Target = answer, Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(0, 3, 0, 5) });
            panel.Children.Add(answer);
            return answer;
        }

        var sections = MailHubRuleTemplate.Sections(_localize);
        AddProtected(sections[0]);
        var topics = AddEditor("Topics", _settings.Priorities);
        AddProtected(sections[1]);
        var style = AddEditor("SpeechStyle", _settings.Style);
        AddProtected(sections[2]);
        void SaveRules()
        {
            if (_settings.Priorities != topics.Text || _settings.Style != style.Text)
                Save(_settings with { Priorities = topics.Text, Style = style.Text });
        }
        topics.LostKeyboardFocus += (_, _) => SaveRules();
        style.LostKeyboardFocus += (_, _) => SaveRules();
        Closing += (_, _) => SaveRules();

        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(BackgroundProperty, "PanelBrush");
        return scroll;
    }

    private void RefreshState()
    {
        try
        {
            var state = _files.Load();
            if (!_reconnectAccounts.SequenceEqual(state.ReconnectAccounts))
            {
                _reconnectAccounts = state.ReconnectAccounts;
                RefreshAccounts();
            }
            var report = _files.SafeLatest(_playback.PlayingId);
            bool readable = report is not null;
            _nowPlaying.Text = report is not null ? string.Format(T("Recording"), report.Created.LocalDateTime.ToString("g")) : T("NoRecording");
            _play.IsEnabled = _delete.IsEnabled = readable;
            _stop.IsEnabled = _playback.PlayingId is not null;
            _play.Content = _playback.PlayingId is null ? T("Play") : _playback.Paused ? T("Resume") : T("Pause");
            _check.IsEnabled = _settings.Accounts.Length > 0 && _files.IsProcessOpen(_settings.BindToIsland)
                && state.Pending is null && state.ManualRequest is null;
            _suspend.IsEnabled = state.Pending is not null;
            _suspend.Content = T(state.Paused ? "ResumeWork" : "Suspend");
            if (!string.IsNullOrEmpty(state.Status))
                _status.Text = _localize(state.Status) + (state.UnavailableAccounts.Length == 0 ? "" : " " + string.Join(", ", state.UnavailableAccounts));
            else if (!_files.IsProcessOpen(_settings.BindToIsland)) _status.Text = T("BindingClosed");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _status.Text = T("FileFailed"); }
    }

    private void SwitchPause()
    {
        try
        {
            _files.Update(state => state.Pending is null ? state : state with
            {
                Paused = !state.Paused, Status = state.Paused ? "MailHub.Waiting" : "MailHub.Paused"
            });
            RefreshState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = T("FileFailed"); }
    }

    private void Attach(Panel row, Button button, string key, Action run)
    {
        button.Content = T(key);
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        button.Margin = new Thickness(0, 0, 6, 4);
        button.Padding = new Thickness(10, 5, 10, 5);
        button.Click += (_, _) => run();
        AutomationProperties.SetAutomationId(button, "MailHub." + key);
        row.Children.Add(button);
    }
}
