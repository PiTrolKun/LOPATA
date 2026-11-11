using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.GameSounds;

internal sealed partial class GameSoundsControl
{
    private ContextMenu? _chatTextMenu;

    private TextBox SelectableChatText(string text, bool secondary = false)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0),
            Margin = new(0, 1, 0, 4), FontSize = 13, ContextMenu = _chatTextMenu ??= CreateChatTextMenu()
        };
        box.SetResourceReference(ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(box, "GameSounds.ChatText");
        return box;
    }

    private ContextMenu CreateChatTextMenu()
    {
        var menu = new ContextMenu { FontSize = 13 };
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/VoiceNotifications/VoiceNotificationMenuResources.xaml", UriKind.Relative)
        });
        var copy = new MenuItem { Command = ApplicationCommands.Copy };
        var selectAll = new MenuItem { Command = ApplicationCommands.SelectAll };
        menu.Items.Add(copy); menu.Items.Add(selectAll);
        menu.Opened += (_, _) =>
        {
            copy.Header = T("CopyChatText");
            selectAll.Header = T("SelectChatText");
            copy.CommandTarget = selectAll.CommandTarget = menu.PlacementTarget;
        };
        menu.Closed += (_, _) => copy.CommandTarget = selectAll.CommandTarget = null;
        return menu;
    }

    private void ScrollChatToEnd()
    {
        if (!_history.IsKeyboardFocusWithin && _chatTextMenu?.IsOpen != true) _chatScroll.ScrollToEnd();
    }
}
