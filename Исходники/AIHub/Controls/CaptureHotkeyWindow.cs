using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Window = System.Windows.Window;

namespace AIHub.Controls;

public sealed class CaptureHotkeyWindow : Window
{
    private readonly TextBlock _held = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly TextBlock _candidate = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
    public int[] Selection { get; private set; } = [];
    public CaptureHotkeyWindow(Window owner, CaptureHotkeys hotkeys, Func<string, string> text)
    {
        // Owner is not a WPF resource parent. Auxiliary windows must share its theme explicitly.
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Owner = owner; Title = text("Capture.Assign"); Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(24) };
        panel.SetResourceReference(TextElement.FontSizeProperty, "UiBodyFontSize");
        panel.Children.Add(CaptureUi.Text(text("Capture.AssignHint")));
        _held.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _candidate.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_held, "Capture.Held");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_candidate, "Capture.Selection");
        _held.Text = text("Capture.Held") + ": —"; _candidate.Text = text("Capture.Selection") + ": —";
        panel.Children.Add(_held); panel.Children.Add(_candidate);
        var buttons = new WrapPanel();
        var save = new Button { Content = text("Capture.Save"), Margin = new(0, 0, 10, 0), MinWidth = 100, IsEnabled = false };
        var cancel = new Button { Content = text("Capture.Cancel"), MinWidth = 100 };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
        var peak = 0;
        void Changed(int[] keys)
        {
            // Clicks on Save/Cancel are not part of a keyboard or extra-mouse assignment.
            var actual = keys.Where(k => k is not (1 or 2)).Order().ToArray();
            _held.Text = text("Capture.Held") + ": " + CaptureHotkeys.Display(actual);
            if (actual.Length == 0) peak = 0;
            if (actual.Length > 0 && actual.Length >= peak)
            { peak = actual.Length; Selection = actual; _candidate.Text = text("Capture.Selection") + ": " + CaptureHotkeys.Display(actual); save.IsEnabled = true; }
        }
        Loaded += (_, _) => { hotkeys.HeldChanged += Changed; hotkeys.BeginRecording(); };
        Activated += (_, _) => hotkeys.BeginRecording();
        Deactivated += (_, _) => hotkeys.EndRecording();
        Closed += (_, _) => { hotkeys.HeldChanged -= Changed; hotkeys.EndRecording(); };
        PreviewKeyDown += (_, e) => e.Handled = true;
        save.Click += (_, _) => DialogResult = true; cancel.Click += (_, _) => DialogResult = false;
    }
}
