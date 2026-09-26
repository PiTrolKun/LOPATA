using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls;

public enum LiteraryMemoryRecoveryChoice { Cancel, Retry, UseRam }

internal static class LiteraryMemoryRecoveryDialog
{
    public static LiteraryMemoryRecoveryChoice Show(FrameworkElement owner, Func<string, string> l, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = LiteraryMemoryRecoveryChoice.Cancel;
        var panel = new StackPanel();
        panel.Children.Add(LiteraryUi.Text(l("Literary.MemoryRecovery.Message")));
        var row = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        panel.Children.Add(row);
        var window = LiteraryEditorDialogs.Create(owner, l("Literary.MemoryRecovery.Title"), panel, 600);
        foreach (var (choice, key) in new[]
        {
            (LiteraryMemoryRecoveryChoice.UseRam, "Literary.MemoryRecovery.Yes"),
            (LiteraryMemoryRecoveryChoice.Retry, "Literary.MemoryRecovery.Retry"),
            (LiteraryMemoryRecoveryChoice.Cancel, "Common.Cancel")
        })
        {
            var button = LiteraryUi.Button(l(key), () => { result = choice; window.DialogResult = choice != LiteraryMemoryRecoveryChoice.Cancel; });
            button.Margin = new Thickness(0, 0, 12, 0);
            button.IsCancel = choice == LiteraryMemoryRecoveryChoice.Cancel;
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "MemoryRecovery." + choice);
            row.Children.Add(button);
        }
        using var registration = token.Register(() => window.Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })));
        window.ShowDialog();
        token.ThrowIfCancellationRequested();
        return result;
    }
}
