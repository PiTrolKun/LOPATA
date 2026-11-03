using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private void RenderRecallSources(Entry entry, StackPanel body)
    {
        if (_library is null || entry.Role != "assistant" || !entry.Complete) return;
        foreach (var source in _library.RecallLinks(_sessionId, _entries.IndexOf(entry)))
        {
            var button = new Button { Content = string.Format(_l("LetItOut.Recall.Source"),
                string.IsNullOrWhiteSpace(source.Title) ? _l("LetItOut.Library.Untitled") : source.Title, source.Number),
                FontSize = 12, Padding = new(6), Margin = new(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                ToolTip = source.Excerpt };
            button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            AutomationProperties.SetAutomationId(button, "LetItOut.Recall." + source.ConversationId + "." + source.Number);
            button.Click += (_, _) => ShowRecallSource(source); body.Children.Add(button);
        }
    }
    private void ShowRecallSource(LetItOutRecallHit reference)
    {
        if (_library?.RecallSource(_sessionId, reference, "", default) is not { } source) return;
        var owner = Window.GetWindow(this);
        var window = new Window { Title = string.Format(_l("LetItOut.Recall.Source"), reference.Title, reference.Number),
            Owner = owner, Width = 780, Height = 650, MinWidth = 450, MinHeight = 300 };
        if (owner is not null) window.Resources.MergedDictionaries.Add(owner.Resources);
        window.SetResourceReference(BackgroundProperty, "HeaderBackgroundBrush"); window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(window, "LetItOut.Recall.Window");
        var panel = new StackPanel { Margin = new(18) };
        panel.Children.Add(Label(_l("LetItOut.Recall.Disclaimer"), 13));
        void Pair(AIHub.Models.LetItOutMemoryPair pair)
        {
            panel.Children.Add(Label("#" + pair.Number + " · " + pair.UserUtc?.ToLocalTime().ToString("g"), 12));
            foreach (var text in new[] { _l("LetItOut.You") + ": " + pair.User, _l("LetItOut.Assistant") + ": " + pair.Answer })
            {
                var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 8), Padding = new(8) };
                box.SetResourceReference(BackgroundProperty, "PanelBrush"); box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); panel.Children.Add(box);
            }
        }
        Pair(source.Original);
        foreach (var pair in source.Nearby) Pair(pair);
        if (source.Later.Count > 0) { panel.Children.Add(Label(_l("LetItOut.Recall.Later"), 16)); foreach (var pair in source.Later) Pair(pair); }
        var open = new Button { Content = _l("LetItOut.Recall.Open"), Padding = new(10), Margin = new(0, 12, 0, 0) };
        open.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        open.Click += (_, _) => { if (OpenConversation(reference.ConversationId)) window.Close(); }; panel.Children.Add(open);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.Show();
    }
}
