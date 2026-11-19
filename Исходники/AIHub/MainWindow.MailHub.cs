using AIHub.Controls.MailHub;

namespace AIHub;

public partial class MainWindow
{
    private MailHubWindow? _mailHub;

    internal bool IsMailHubChatBusy() => ScenarioNavigationPage.LetItOutChat?.IsWorking == true;

    internal void ShowMailHubNotice(string status, string[] details)
    {
        var suffix = status == "MailHub.DeferredNotice"
            ? string.Join(", ", details.Select(L))
            : string.Join(", ", details);
        var text = L(status) + (suffix.Length == 0 ? "" : " " + suffix);
        if (_applicationTray?.Notify(L("MailHub.Title"), text, onOpen: OpenMailHub) != true)
            StatusText.Text = text;
    }

    private void OpenMailHub()
    {
        if (_mailHub is not null) { _mailHub.Activate(); return; }
        var window = new MailHubWindow(this, L);
        _mailHub = window;
        window.Closed += (_, _) => _mailHub = null;
        window.Show();
    }
}
