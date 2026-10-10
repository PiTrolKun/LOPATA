using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub;

public sealed class PublisherWizardWindow : Window
{
    private readonly Func<string, string> _text;
    private readonly PublisherCoordinator _service;
    private readonly IPublisherVkClient _vk;
    private readonly IPublisherGitHubClient _github;
    private readonly StackPanel _page = new();
    private readonly TextBlock _heading, _status = new();
    private readonly TextBox _community, _repository;
    private readonly PasswordBox _secret = new() { Margin = new(0, 4, 0, 12), Padding = new(8) };
    private readonly CheckBox _automatic = new();
    private readonly ComboBox _interval = new() { Margin = new(0, 4, 0, 12) };
    private readonly Button _back, _next;
    private readonly CancellationTokenSource _cancel = new();
    private int _step;
    private long _communityId;
    private string _communityName = "", _repositoryId = "";

    public PublisherWizardWindow(Window owner, Func<string, string> text, PublisherCoordinator service,
        IPublisherVkClient vk, IPublisherGitHubClient github)
    {
        _text = text; _service = service; _vk = vk; _github = github;
        PublisherUi.Prepare(this, owner, text("Publisher.Add"), 680, 620);
        Content = PublisherUi.Frame(out var header, out var body, out var footer);
        _heading = PublisherUi.Text(""); _heading.FontWeight = FontWeights.Bold; header.Children.Add(_heading);
        body.Content = _page; footer.Children.Add(_status);
        _community = PublisherUi.Input("Publisher.Wizard.Community", text("Publisher.Community"));
        _repository = PublisherUi.Input("Publisher.Wizard.Repository", text("Publisher.Repository"));
        AutomationProperties.SetAutomationId(_secret, "Publisher.Wizard.Token"); AutomationProperties.SetName(_secret, text("Publisher.Token"));
        PublisherUi.StyleSecret(_secret);
        _automatic.Content = PublisherUi.Text(text("Publisher.AutoMode"));
        AutomationProperties.SetAutomationId(_automatic, "Publisher.Wizard.Automatic");
        foreach (var minutes in new[] { 15, 60, 360 }) _interval.Items.Add(new ComboBoxItem { Content = string.Format(text("Publisher.Minutes"), minutes), Tag = minutes });
        _interval.SelectedIndex = 1;
        AutomationProperties.SetAutomationId(_interval, "Publisher.Wizard.Interval");
        var buttons = PublisherUi.Buttons(); footer.Children.Add(buttons);
        _back = PublisherUi.Button(text("Publisher.Back"), "Publisher.Wizard.Back", () => { _step--; Render(); });
        _next = PublisherUi.Button(text("Publisher.Next"), "Publisher.Wizard.Next", () => _ = NextAsync());
        buttons.Children.Add(_back); buttons.Children.Add(_next);
        buttons.Children.Add(PublisherUi.Button(text("Publisher.Cancel"), "Publisher.Wizard.Cancel", Close));
        Closed += (_, _) => { _cancel.Cancel(); _secret.Clear(); }; Render();
    }
    private void Render()
    {
        _page.Children.Clear(); _status.Text = ""; _back.IsEnabled = _step > 0;
        _heading.Text = string.Format(_text("Publisher.Step"), _step + 1, _text(new[] { "Publisher.Where", "Publisher.From", "Publisher.How" }[_step]));
        _next.Content = PublisherUi.Text(_text(_step == 2 ? "Publisher.Save" : "Publisher.CheckNext"));
        if (_step == 0)
        {
            _page.Children.Add(PublisherUi.Text(_text("Publisher.VkHelp")));
            _page.Children.Add(PublisherUi.Text(_text("Publisher.Community"))); _page.Children.Add(_community);
            _page.Children.Add(PublisherUi.Text(_text("Publisher.Token"))); _page.Children.Add(_secret);
            _page.Children.Add(PublisherUi.Text(_text("Publisher.SecurityHelp")));
            _page.Children.Add(PublisherUi.Button(_text("Publisher.VkDocs"), "Publisher.Wizard.VkDocs", () => PublisherUi.OpenLink("https://dev.vk.com/ru/method/wall.post")));
        }
        else if (_step == 1)
        {
            _page.Children.Add(PublisherUi.Text(_text("Publisher.GitHubHelp"))); _page.Children.Add(_repository);
            _page.Children.Add(PublisherUi.Text(_text("Publisher.AccessChecked")));
        }
        else
        {
            _page.Children.Add(PublisherUi.Text(_communityName + " ← " + _repositoryId));
            _page.Children.Add(PublisherUi.Text(_text("Publisher.ManualHelp"))); _page.Children.Add(_automatic);
            _page.Children.Add(PublisherUi.Text(_text("Publisher.CheckInterval"))); _page.Children.Add(_interval);
            _page.Children.Add(PublisherUi.Text(_text("Publisher.AutoWarning")));
            _page.Children.Add(PublisherUi.Text(_text("Publisher.BaselineHelp")));
        }
    }
    private async Task NextAsync()
    {
        _next.IsEnabled = _back.IsEnabled = _page.IsEnabled = false; _status.Text = _text("Publisher.Checking");
        try
        {
            if (_step == 0)
            { (_communityId, _communityName) = await _vk.CheckAsync(_community.Text, _secret.Password, _cancel.Token); _step++; Render(); }
            else if (_step == 1)
            {
                _repositoryId = PublisherGitHubClient.ParseRepository(_repository.Text);
                var releases = await _github.ReadAsync(_repositoryId, _cancel.Token); _step++; Render();
                if (releases.Count == 0) _status.Text = _text("Publisher.NoReleases");
            }
            else
            {
                if (_automatic.IsChecked == true && !PublisherUi.Confirm(this, _text, _text("Publisher.AutoWarning"))) return;
                await _service.AddAsync(new PublisherDirection
                { CommunityId = _communityId, CommunityName = _communityName, Repository = _repositoryId,
                    AutoPublish = _automatic.IsChecked == true, CheckMinutes = (int)((ComboBoxItem)_interval.SelectedItem).Tag }, _secret.Password, _cancel.Token);
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        { _status.Text = _text(_cancel.IsCancellationRequested ? "Publisher.Cancelled" : "Publisher.NetworkTimeout"); }
        catch (Exception error) { _status.Text = _text(error is PublisherException known ? known.Key : "Publisher.NetworkError"); }
        finally { _next.IsEnabled = _page.IsEnabled = true; _back.IsEnabled = _step > 0; }
    }
}
