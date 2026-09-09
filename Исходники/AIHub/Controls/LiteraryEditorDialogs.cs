using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

internal static class LiteraryEditorDialogs
{
    private static bool? Show(Window window)
    {
        try { return window.ShowDialog(); }
        catch { window.Close(); throw; } // A failed modal start must not leave a clickable orphan window.
    }
    public static Window Create(FrameworkElement owner, string title, UIElement content, double width = 540)
    {
        var window = new Window { Title = title, Owner = Window.GetWindow(owner), Width = width,
            SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(20), Child = content } };
        if (window.Owner is { } parent) window.Resources = parent.Resources;
        window.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        return window;
    }

    public static int Choose(FrameworkElement owner, Func<string, string> l, string title, string message, params string[] choices)
    {
        int selected = -1;
        var panel = new StackPanel(); panel.Children.Add(LiteraryUi.Text(message));
        var window = Create(owner, l(title), panel);
        for (int i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = LiteraryUi.Button(l(choices[i]), () => { selected = index; window.DialogResult = true; });
            button.Margin = new Thickness(0, 12, 0, 0); button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            panel.Children.Add(button);
        }
        Show(window); return selected;
    }

    public static string? Edit(FrameworkElement owner, Func<string, string> l, string title, string initial, bool multiline)
    {
        var panel = new StackPanel();
        var input = LiteraryWorkspaceParts.TextArea(false); input.Text = initial;
        input.AcceptsReturn = multiline; input.Height = multiline ? 240 : 40;
        panel.Children.Add(input);
        var window = Create(owner, l(title), panel, multiline ? 700 : 540);
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        row.Children.Add(LiteraryUi.Button(l("Literary.Editor.Apply"), () => window.DialogResult = true, true));
        var cancel = LiteraryUi.Button(l("Literary.Editor.Cancel"), () => window.DialogResult = false); cancel.IsCancel = true;
        row.Children.Add(cancel); panel.Children.Add(row);
        window.Loaded += (_, _) => input.Focus();
        return Show(window) == true ? input.Text : null;
    }

    public static int? Interval(FrameworkElement owner, Func<string, string> l, int current)
    {
        var panel = new StackPanel(); panel.Children.Add(LiteraryUi.Text(l("Literary.Editor.IntervalHint")));
        var values = Services.LiteraryChapterStore.AutosaveIntervals;
        var combo = new ComboBox { ItemsSource = values.Select(n => string.Format(l("Literary.Editor.Seconds"), n)).ToArray(), SelectedIndex = values.ToList().IndexOf(current), MinHeight = 32 };
        panel.Children.Add(combo);
        var window = Create(owner, l("Literary.Editor.Interval"), panel, 380);
        var button = LiteraryUi.Button(l("Literary.Editor.Apply"), () => window.DialogResult = true, true);
        button.Margin = new Thickness(0, 14, 0, 0); panel.Children.Add(button);
        return Show(window) == true ? values[combo.SelectedIndex] : null;
    }
}
