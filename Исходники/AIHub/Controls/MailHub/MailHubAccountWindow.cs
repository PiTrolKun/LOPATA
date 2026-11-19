using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AIHub.Services.MailHub;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Label = System.Windows.Controls.Label;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.MailHub;

internal sealed class MailHubAccountWindow : Window
{
    private readonly MailHubConfiguration _configuration;
    private readonly Func<string, string> _text;
    private readonly MailHubAccount? _original;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _provider = new();
    private readonly TextBox _address = new(), _login = new(), _host = new(), _port = new(), _clientId = new();
    private readonly CheckBox _startTls = new(), _appPassword = new();
    private readonly PasswordBox _password = new(), _clientSecret = new();
    private readonly StackPanel _manual = new(), _oauth = new(), _folders = new();
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap }, _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _check = new(), _save = new();
    private (MailHubAccount Account, MailHubSecret Secret)? _checked;
    private bool _busy;
    private int _formRevision;

    internal MailHubAccount? Result { get; private set; }
    internal MailHubSecret? Secret { get; private set; }

    internal MailHubAccountWindow(Window owner, Func<string, string> text,
        MailHubConfiguration configuration, MailHubAccount? original = null)
    {
        _configuration = configuration; _text = text; _original = original;
        MailHubWindow.Prepare(this, owner, text(original is null ? "MailHub.Add" : "MailHub.Edit"), 585, 680);
        var form = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var preset in MailHubPreset.All)
            _provider.Items.Add(new ComboBoxItem { Content = text(preset.Label), Tag = preset });
        Field(form, "MailHub.Service", _provider);
        Field(form, "MailHub.Address", _address);
        Field(form, "MailHub.Login", _login);
        form.Children.Add(_help);
        Field(_manual, "MailHub.Server", _host);
        Field(_manual, "MailHub.Port", _port);
        _startTls.Content = text("MailHub.StartTls"); _manual.Children.Add(_startTls);
        form.Children.Add(_manual);
        _appPassword.Content = text("MailHub.UseAppPassword"); form.Children.Add(_appPassword);
        Field(form, "MailHub.AppPassword", _password);
        Field(_oauth, "MailHub.ClientId", _clientId);
        Field(_oauth, "MailHub.ClientSecret", _clientSecret);
        form.Children.Add(_oauth);
        _check.Content = text("MailHub.CheckLogin"); _check.Margin = new Thickness(0, 12, 0, 8);
        form.Children.Add(_check);
        form.Children.Add(_status);
        form.Children.Add(new TextBlock { Text = text("MailHub.Folders"), Margin = new Thickness(0, 12, 0, 4) });
        form.Children.Add(new ScrollViewer { MaxHeight = 175, Content = _folders });
        _save.Content = text("MailHub.Apply"); _save.Margin = new Thickness(0, 12, 0, 0); _save.IsEnabled = false;
        form.Children.Add(_save);

        _address.Text = original?.Address ?? "";
        _login.Text = original?.Login ?? "";
        _host.Text = original?.Host ?? "";
        _port.Text = (original?.Port ?? 993).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _startTls.IsChecked = original?.StartTls == true;
        _appPassword.IsChecked = original is not { Provider: MailHubProvider.Gmail, Authentication: MailHubAuthentication.OAuth };
        _provider.SelectedIndex = MailHubPreset.All.ToList().FindIndex(preset => preset.Provider == (original?.Provider ?? MailHubProvider.Gmail));
        _provider.SelectionChanged += (_, _) => { _login.Clear(); RefreshProvider(); Invalidate(); };
        _appPassword.Click += (_, _) => { RefreshProvider(); Invalidate(); };
        foreach (var box in new[] { _address, _login, _host, _port, _clientId })
            box.TextChanged += (_, _) => Invalidate();
        foreach (var box in new[] { _password, _clientSecret })
            box.PasswordChanged += (_, _) => Invalidate();
        _startTls.Click += (_, _) => Invalidate();
        _check.Click += async (_, _) => await CheckAsync();
        _save.Click += (_, _) => Confirm();
        Closed += (_, _) => _lifetime.Cancel();
        RefreshProvider();
    }

    private MailHubPreset Preset => (MailHubPreset)((ComboBoxItem)_provider.SelectedItem).Tag;
    private MailHubAuthentication Authentication => Preset.Provider == MailHubProvider.Gmail && _appPassword.IsChecked == true
        ? MailHubAuthentication.AppPassword : Preset.Authentication;

    private void RefreshProvider()
    {
        _help.Inlines.Clear();
        _help.Inlines.Add(new Run(_text(Preset.HelpKey)));
        var gmail = Preset.Provider == MailHubProvider.Gmail;
        if (gmail)
            AddPasswordGuide("https://myaccount.google.com/apppasswords");
        else if (Preset.Provider == MailHubProvider.MailRu)
            AddPasswordGuide("https://account.mail.ru/user/2-step-auth/passwords/");
        else if (Preset.Provider == MailHubProvider.Yandex)
        {
            AddHelpLink("MailHub.Help.YandexImapLink", "https://mail.yandex.ru/?dpda=yes");
            AddPasswordGuide("https://passport.yandex.ru/security/app-passwords");
        }
        else if (Preset.Provider == MailHubProvider.Outlook)
        {
            AddHelpLink("MailHub.Help.OutlookImapLink", "https://outlook.live.com/mail/0/options/mail/forwarding?nlp=1");
            AddHelpLink("MailHub.Help.OutlookOAuthLink", "https://learn.microsoft.com/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth");
            _help.Inlines.Add(new LineBreak());
            _help.Inlines.Add(new Run(_text("MailHub.Help.OutlookSteps")));
        }
        else if (Preset.Provider == MailHubProvider.ICloud)
            AddPasswordGuide("https://account.apple.com/");
        _manual.Visibility = Preset.Provider == MailHubProvider.Other ? Visibility.Visible : Visibility.Collapsed;
        _appPassword.Visibility = gmail ? Visibility.Visible : Visibility.Collapsed;
        var oauth = Authentication == MailHubAuthentication.OAuth;
        _password.Visibility = oauth ? Visibility.Collapsed : Visibility.Visible;
        _oauth.Visibility = oauth ? Visibility.Visible : Visibility.Collapsed;
        if (oauth && string.IsNullOrWhiteSpace(_clientId.Text))
            _clientId.Text = MailHubOAuth.PublicClient(Preset.Provider).Id;
    }

    private void AddPasswordGuide(string url)
    {
        var key = Preset.HelpKey + "Password";
        AddHelpLink(key + "Link", url);
        _help.Inlines.Add(new LineBreak());
        _help.Inlines.Add(new Run(_text(key + "Steps")));
    }

    private void AddHelpLink(string key, string url)
    {
        var link = new Hyperlink(new Run(_text(key))) { NavigateUri = new Uri(url) };
        link.SetResourceReference(TextElement.ForegroundProperty, "ChatUserTextBrush");
        link.RequestNavigate += (_, args) =>
        {
            args.Handled = true;
            try { using var browser = Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            { _status.Text = _text(key + "Failed"); }
        };
        _help.Inlines.Add(new LineBreak());
        _help.Inlines.Add(link);
    }

    private MailHubAccount Input()
    {
        var account = MailHubAccount.Create(Preset.Provider, _address.Text, _login.Text,
            _host.Text, int.TryParse(_port.Text, out var port) ? port : 0, _startTls.IsChecked == true, Authentication);
        return _original is { } original && account.Provider == original.Provider
            && account.Address.Equals(original.Address, StringComparison.OrdinalIgnoreCase)
            && account.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase)
            && account.Port == original.Port && account.StartTls == original.StartTls && account.Login == original.Login
            ? account with { Id = original.Id } : account;
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        Invalidate();
        int revision = _formRevision;
        _busy = true; _check.IsEnabled = false;
        _status.Text = _text("MailHub.CheckingLogin");
        try
        {
            var account = Input();
            var oldSecret = _original is { } original && account.Id == original.Id && account.Authentication == original.Authentication
                ? _configuration.ReadSecret(account.Id) : null;
            MailHubSecret secret;
            if (account.Authentication == MailHubAuthentication.OAuth)
            {
                var publicClient = MailHubOAuth.PublicClient(account.Provider);
                var id = _clientId.Text.Trim();
                if (id.Length == 0) id = oldSecret?.ClientId ?? publicClient.Id;
                var clientSecret = _clientSecret.Password;
                if (clientSecret.Length == 0)
                    clientSecret = oldSecret is { } saved && saved.ClientId == id ? saved.ClientSecret
                        : id == publicClient.Id ? publicClient.Secret : "";
                secret = await MailHubOAuth.AuthorizeAsync(account, id, clientSecret, _lifetime.Token);
            }
            else
            {
                var password = _password.Password.Length > 0 ? _password.Password : oldSecret?.AppPassword;
                if (string.IsNullOrWhiteSpace(password)) throw new MailHubInputException("MailHub.PasswordRequired");
                secret = new(AppPassword: password);
            }
            var folders = await new MailHubImap(_configuration).CheckAsync(account, secret, _lifetime.Token);
            if (revision != _formRevision) return;
            foreach (var folder in folders)
            {
                var field = new CheckBox { Content = folder, Tag = folder,
                    IsChecked = _original is { } saved && saved.Id == account.Id && saved.Folders.Contains(folder, StringComparer.Ordinal) };
                field.Click += (_, _) => UpdateSave();
                _folders.Children.Add(field);
            }
            _checked = (account, secret);
            _status.Text = _text("MailHub.SelectFolders");
            UpdateSave();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (MailHubInputException error) { _status.Text = _text(error.Message); }
        catch (MailKit.Security.SslHandshakeException) { _status.Text = _text("MailHub.TlsFailed"); }
        catch (MailKit.Security.AuthenticationException) { _status.Text = _text("MailHub.LoginFailed"); }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or
            System.Net.Http.HttpRequestException or OperationCanceledException)
        { _status.Text = _text("MailHub.ProviderUnavailable"); }
        catch (Exception error)
        {
            AIHub.Services.OwnedProcessRegistry.Log("mail_hub_login_failed", "MailHub", detail: error.GetType().Name);
            _status.Text = _text("MailHub.LoginFailed");
        }
        finally { _busy = false; _check.IsEnabled = true; }
    }

    private void Invalidate() { _formRevision++; _checked = null; _folders.Children.Clear(); _save.IsEnabled = false; }
    private void UpdateSave() => _save.IsEnabled = _checked is not null &&
        _folders.Children.OfType<CheckBox>().Any(box => box.IsChecked == true);

    private void Confirm()
    {
        if (_checked is not { } result) return;
        var chosen = _folders.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();
        if (chosen.Length == 0) return;
        Result = result.Account with { Folders = chosen };
        Secret = result.Secret;
        DialogResult = true;
    }

    private void Field(Panel parent, string key, Control control)
    {
        var label = new Label { Content = _text(key), Target = control, Padding = new Thickness(0, 8, 0, 4) };
        parent.Children.Add(label);
        parent.Children.Add(control);
        control.MinHeight = 28;
    }
}
