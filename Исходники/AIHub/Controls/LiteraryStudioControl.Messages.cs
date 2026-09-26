using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private TextBox? _streamBox;
    private void RenderMessages()
    {
        _messages.Children.Clear(); _streamBox = null;
        foreach (var message in State.Messages.Where(m=>(_showArchive || m.Session==State.Session)
            && (!State.DirectRequest || m.Role!="Task")))
        {
            var panel = new StackPanel { Margin = new Thickness(0,4,6,14) };
            var header = new DockPanel();
            var inContext = LiteraryStudioContext.Contains(State,message);
            var mark = LiteraryUi.Text(inContext ? "●" : "◷");
            mark.ToolTip = _l(inContext ? "Studio.InContext" : "Studio.OnlyHistory");
            mark.Margin = new Thickness(0,0,8,0); DockPanel.SetDock(mark,Dock.Left); header.Children.Add(mark);
            var title = LiteraryUi.Text(_l("Studio.Role." + message.Role) + (message.Complete ? "" : " · " + _l("Literary.Writer.Incomplete")));
            header.Children.Add(title); panel.Children.Add(header);
            var text = MessageText(message.Text, message.Role);
            text.Opacity = inContext ? 1 : .58; panel.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (message.Role == "Writer") buttons.Children.Add(CopyMessageButton(text,message.Id));
            if (buttons.Children.Count > 0) panel.Children.Add(buttons);
            _messages.Children.Add(panel);
        }
        RenderContextFailure();
        _archive.Content = _l(_showArchive ? "Studio.CurrentSession" : "Studio.Archive"); _scroll.ScrollToEnd();
    }
    private TextBox MessageText(string value, string role)
    {
        var text = LiteraryWorkspaceParts.TextArea();
        text.Text = value; text.MinHeight = 0; text.BorderThickness = new Thickness(0); text.Background = Brushes.Transparent;
        text.Padding = new Thickness(0,5,0,5); text.FontSize = 16; text.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var background = (TryFindResource("WindowBackgroundBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        var dark = background.R + background.G + background.B < 384;
        text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(role switch
        {
            "Advisor" => dark ? "#90BFFF" : "#2255A0", "Writer" => dark ? "#8EDCC1" : "#167352",
            "Task" => dark ? "#DBC0FF" : "#694296", _ => dark ? "#F0DCAC" : "#76570E"
        }));
        return text;
    }
    private System.Windows.Controls.Button MessageButton(string label, Action action)
    {
        var button = LiteraryUi.Button(label,()=>RunUi(action)); Compact(button); button.FontSize = 12; return button;
    }
    private System.Windows.Controls.Button CopyMessageButton(TextBox messageText, string messageId)
    {
        var button = new System.Windows.Controls.Button { Content = _l("Studio.CopyAll"), ToolTip = _l("Studio.CopyMessageHint") };
        Compact(button); button.FontSize = 12;
        System.Windows.Automation.AutomationProperties.SetAutomationId(button,"CopyWriterMessage_" + messageId);
        button.Click += (_,e) =>
        {
            e.Handled = true;
            RunUi(() =>
            {
                // This button owns a single rendered answer, independent of chat selection or active role.
                System.Windows.Clipboard.SetText(messageText.Text);
                _status.Text = _l("Studio.MessageCopied");
            });
        };
        return button;
    }
    private void ShowStream(string text, string role)
    {
        if (_streamBox is null) { _streamBox = MessageText("",role); _messages.Children.Add(_streamBox); }
        _streamBox.Text = text; _scroll.ScrollToEnd();
    }
    private void RenderQuotes()
    {
        _quotes.Children.Clear();
        foreach (var quote in State.Quotes.ToArray())
        {
            var row = new DockPanel(); var remove = MessageButton("×", () => { State.Quotes.Remove(quote); _dirty = true; RenderQuotes(); });
            remove.ToolTip = _l("Studio.RemoveQuote"); DockPanel.SetDock(remove,Dock.Right); row.Children.Add(remove);
            var label = LiteraryUi.Text("❝ " + quote.Source + " · " + quote.Text); label.MaxHeight = 55;
            label.ToolTip = quote.Source + "\n" + quote.Text; row.Children.Add(label); _quotes.Children.Add(row);
        }
    }
}
