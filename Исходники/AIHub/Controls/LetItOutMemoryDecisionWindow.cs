using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AIHub.Controls;

/// <summary>Closing or Escape cancels; only the user can decide to leave other applications running.</summary>
internal sealed class LetItOutMemoryDecisionWindow : Window
{
    internal string Choice { get; private set; } = "cancel";
    internal LetItOutMemoryDecisionWindow(Window? owner, Func<string, string> localize, string message)
    {
        MusicWishUi.PrepareWindow(this, localize("LetItOut.Memory.Title"), "LetItOut.Memory.Window", 700);
        Owner = owner;
        if (owner is not null) Resources.MergedDictionaries.Insert(0, owner.Resources);
        SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 50;
        var root = new DockPanel { Margin = new(24) };
        var buttons = new WrapPanel { Margin = new(0, 18, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        foreach (var (key, value) in new[] { ("Retry", "retry"), ("Continue", "hybrid"), ("Cancel", "cancel") })
        {
            var button = MusicWishUi.Button(localize("LetItOut.Memory." + key), "LetItOut.Memory." + key,
                () => { Choice = value; DialogResult = true; });
            if (value == "cancel") button.IsCancel = true;
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var text = MusicWishUi.Text(message);
        AutomationProperties.SetAutomationId(text, "LetItOut.Memory.Message");
        root.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
