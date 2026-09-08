using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

public enum LiteraryWorkspaceAction
{
    RenameChapter, History, Export, FinishChapter, WriterNewChat, AdvisorNewChat,
    WriterSend, AdvisorSend, WriterAttach, AdvisorAttach
}

internal static class LiteraryWorkspaceParts
{
    public static Border Card(UIElement child)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        return border;
    }

    public static TextBox TextArea(bool readOnly = true)
    {
        var text = new TextBox { IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(1) };
        text.SetResourceReference(Control.BackgroundProperty, "SecondaryButtonBackgroundBrush");
        text.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        text.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        text.SetResourceReference(Control.FontSizeProperty, "UiBodyFontSize");
        return text;
    }

    public static UIElement Chat(string role, Func<string, string> l, Func<string, LiteraryWorkspaceAction, Button> button)
    {
        bool writer = role == "Writer";
        var panel = new DockPanel();
        var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(LiteraryUi.Text(l("Literary.Workspace." + role), true));
        var fresh = button(l("Literary.Workspace.NewChat"), writer ? LiteraryWorkspaceAction.WriterNewChat : LiteraryWorkspaceAction.AdvisorNewChat);
        fresh.Margin = new Thickness(10, 0, 0, 0); header.Children.Add(fresh);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var inputRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var actions = new StackPanel();
        actions.Children.Add(button("＋", writer ? LiteraryWorkspaceAction.WriterAttach : LiteraryWorkspaceAction.AdvisorAttach));
        actions.Children.Add(button("➤", writer ? LiteraryWorkspaceAction.WriterSend : LiteraryWorkspaceAction.AdvisorSend));
        DockPanel.SetDock(actions, Dock.Right); inputRow.Children.Add(actions);
        var input = TextArea(); input.Height = 68; input.Text = l("Literary.Workspace.ChatPending");
        inputRow.Children.Add(input); DockPanel.SetDock(inputRow, Dock.Bottom); panel.Children.Add(inputRow);
        panel.Children.Add(new ScrollViewer { Content = LiteraryUi.Text(l("Literary.Workspace." + role + "Hint")), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return Card(panel);
    }
}
