using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal static class LiteraryPromptDialogUi
{
    public static void Configure(Window window, Window? owner, string title)
    {
        PromptDialogUi.Configure(window, owner, title);
        window.UseLayoutRounding = true;
        window.SourceInitialized += (_, _) =>
        {
            if (window.TryFindResource("WindowBackgroundBrush") is System.Windows.Media.SolidColorBrush background)
            {
                var c = background.Color;
                AIHub.Services.WindowTitleBarThemeService.Apply(window, .2126*c.R + .7152*c.G + .0722*c.B < 128);
            }
        };
        // Reuse scoped themed selectors, without altering the application's global styles.
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/LiteraryRagEditorTheme.xaml", UriKind.Relative) });
        foreach (var type in new[] { typeof(TextBox), typeof(ListBox), typeof(ListBoxItem), typeof(Expander) })
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("WindowBackgroundBrush")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("LineBrush")));
            if (type == typeof(ListBoxItem))
            {
                var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
                selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AccentBrush")));
                selected.Setters.Add(new Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));
                style.Triggers.Add(selected);
            }
            window.Resources[type] = style;
        }
    }

    public static T Identify<T>(T control, string id) where T : FrameworkElement
    { AutomationProperties.SetAutomationId(control, id); return control; }

    public static Button Button(string label, Action click, string id, bool primary = false)
        => Identify(PromptDialogUi.Button(label, click, primary), id);

    public static FrameworkElement Area(Window owner, TextBox area, string defaults, string title, string id, Func<string,string> l)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        Identify(area, id); PromptDialogUi.Label(area, title);
        area.Height = 180;
        var toolbar = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        toolbar.Children.Add(Button(l("PromptPairs.Undo"), () => area.Undo(), id + ".Undo"));
        toolbar.Children.Add(Button(l("PromptPairs.Redo"), () => area.Redo(), id + ".Redo"));
        var wrap = Identify(new System.Windows.Controls.CheckBox { Content = l("PromptPairs.Wrap"), IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) }, id + ".Wrap");
        wrap.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        wrap.Click += (_, _) => area.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        toolbar.Children.Add(wrap);
        toolbar.Children.Add(Button(l("PromptPairs.Restore"), () =>
        {
            if (area.Text == defaults || PromptDialogUi.Confirm(owner, l("PromptPairs.RestoreConfirm"), owner.Title))
            { area.SelectAll(); area.SelectedText = defaults; }
        }, id + ".Restore"));
        var search = Identify(new TextBox { Width = 120, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center }, id + ".Search");
        PromptDialogUi.Label(search, l("PromptPairs.Find")); toolbar.Children.Add(search);
        void FindNext()
        {
            if (search.Text.Length == 0) return;
            var start = Math.Min(area.Text.Length, area.SelectionStart + area.SelectionLength);
            var index = area.Text.IndexOf(search.Text, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) index = area.Text.IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) return;
            area.Focus(); area.Select(index, search.Text.Length); area.ScrollToLine(area.GetLineIndexFromCharacterIndex(index));
        }
        toolbar.Children.Add(Button(l("PromptPairs.Find"), FindNext, id + ".Find"));
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } };
        area.PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); e.Handled = true; } };
        panel.Children.Add(toolbar); panel.Children.Add(area);
        return panel;
    }
}
