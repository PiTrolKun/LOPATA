using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

/// <summary>WPF uses the Windows ISpellChecker backend on supported Windows versions.</summary>
public static class LiterarySpellChecking
{
    private static readonly DependencyProperty SuspendedProperty = DependencyProperty.RegisterAttached(
        "Suspended", typeof(bool), typeof(LiterarySpellChecking), new PropertyMetadata(false));
    static LiterarySpellChecking()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is not ContextMenu menu || menu.PlacementTarget is not TextBox input
                    || input.ContextMenu is not null || !SpellCheck.GetIsEnabled(input)) return;
                // WPF creates a private menu subclass whose Aero template hardcodes a light background.
                // Apply presentation only; native suggestion items and editing commands remain intact.
                if (input.TryFindResource("Spelling.ContextMenu") is Style menuStyle) menu.Style = menuStyle;
                if (input.TryFindResource("Spelling.MenuItem") is Style itemStyle)
                    foreach (var item in menu.Items.OfType<MenuItem>()) item.Style = itemStyle;
            }));
    }
    public static void Enable(TextBox input, string language)
    {
        // Dispose WPF's native checker on its owning dispatcher, before the editor
        // becomes garbage. Its COM finalizer can otherwise crash the whole process.
        input.Loaded -= Resume;
        input.Unloaded -= Suspend;
        input.Loaded += Resume;
        input.Unloaded += Suspend;
        input.SetValue(SuspendedProperty, false);
        input.Language = XmlLanguage.GetLanguage(language.StartsWith("en",StringComparison.OrdinalIgnoreCase) ? "en-US" : "ru-RU");
        SpellCheck.SetIsEnabled(input,true);
        ApplyMenuTheme(input.Resources);
        // Keep the native spelling context menu; suggestions only apply on explicit selection.
    }

    private static void Resume(object sender, RoutedEventArgs _)
    {
        var input = (TextBox)sender;
        if (!(bool)input.GetValue(SuspendedProperty)) return;
        input.SetValue(SuspendedProperty, false);
        SpellCheck.SetIsEnabled(input, true);
    }
    private static void Suspend(object sender, RoutedEventArgs _) => ReleaseInput((TextBox)sender);
    private static void ReleaseInput(TextBox input)
    {
        if (!SpellCheck.GetIsEnabled(input)) return;
        input.SetValue(SuspendedProperty, true);
        SpellCheck.SetIsEnabled(input, false);
    }

    /// <summary>Release native spelling synchronously before replacing a logical editor tree.</summary>
    internal static void ReleaseTree(DependencyObject? root)
    {
        if (root is null) return;
        root.Dispatcher.VerifyAccess();
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (item is TextBox input) ReleaseInput(input);
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) pending.Push(child);
        }
    }

    /// <summary>Native spelling menu presentation, with dynamic application theme resources.</summary>
    public static void ApplyMenuTheme(ResourceDictionary resources)
    {
        var source = new Uri("/AIHub;component/Controls/SpellingMenuResources.xaml", UriKind.Relative);
        if (!resources.MergedDictionaries.Any(dictionary => dictionary.Source == source))
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
    }
}
