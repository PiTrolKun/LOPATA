using System.Windows;

namespace AIHub;

public partial class MainWindow
{
    private AboutWindow? _aboutWindow;

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is not null) { _aboutWindow.Activate(); return; }
        _aboutWindow = new AboutWindow(this, L("App.ProductName"), GetAppVersion(), L,
            () => ApplicationUpdates_Click(sender, e));
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }
}
