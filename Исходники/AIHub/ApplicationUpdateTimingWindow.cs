using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub;

public sealed class ApplicationUpdateTimingWindow : Window
{
    public bool? ApplyOnNextLaunch { get; private set; }

    public ApplicationUpdateTimingWindow(Window owner, Func<string, string> text, bool canSchedule, bool alreadyDownloaded)
    {
        Owner = owner; Resources = owner.Resources; Title = text("Updates.TimingTitle");
        Width = 580; MinWidth = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = text(alreadyDownloaded ? "Updates.TimingReady" : "Updates.TimingQuestion"),
            TextWrapping = TextWrapping.Wrap, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = text("Updates.TimingHelp"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var next = new Button { IsEnabled = canSchedule };
        ApplicationUpdateChoices.Button(next, text("Updates.NextLaunch"));
        if (!canSchedule) next.ToolTip = text("Updates.TransitionRequired");
        next.Click += (_, _) => { ApplyOnNextLaunch = true; DialogResult = true; };
        var now = new Button();
        ApplicationUpdateChoices.Button(now, text("Updates.Immediately"), primary: true);
        now.Click += (_, _) => { ApplyOnNextLaunch = false; DialogResult = true; };
        buttons.Children.Add(next); buttons.Children.Add(now); panel.Children.Add(buttons);
        if (!canSchedule)
            panel.Children.Add(new TextBlock { Text = text("Updates.TransitionRequired"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0) });
    }
}
