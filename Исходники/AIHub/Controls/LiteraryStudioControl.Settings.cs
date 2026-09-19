using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private void ConfigureTransferSettings()
    {
        foreach (var target in new FrameworkElement[] { _role, _send, _sendWriter })
        {
            var menu = new ContextMenu();
            var settings = new MenuItem { Header = _l("Studio.TransferSettings") };
            settings.Click += (_,e) => { e.Handled = true; OpenTransferSettings(); };
            menu.Items.Add(settings); target.ContextMenu = menu;
            ContextMenuService.SetShowOnDisabled(target,true);
            menu.Opened += (_,_) => settings.IsEnabled = !IsWorking && !_blocked();
        }
    }

    private void OpenTransferSettings()
    {
        if (IsWorking || _blocked() || Window.GetWindow(this) is not { } owner) return;
        new LiteraryStudioSettingsWindow(owner,State,_l,Save).ShowDialog();
        Render();
    }
}
