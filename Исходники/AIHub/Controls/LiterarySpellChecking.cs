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
        input.Language = XmlLanguage.GetLanguage(language.StartsWith("en",StringComparison.OrdinalIgnoreCase) ? "en-US" : "ru-RU");
        SpellCheck.SetIsEnabled(input,true);
        ApplyMenuTheme(input.Resources);
        // Keep the native spelling context menu; suggestions only apply on explicit selection.
    }

    /// <summary>Native spelling menu presentation, with dynamic application theme resources.</summary>
    public static void ApplyMenuTheme(ResourceDictionary resources)
    {
        var source = new Uri("/AIHub;component/Controls/SpellingMenuResources.xaml", UriKind.Relative);
        if (!resources.MergedDictionaries.Any(dictionary => dictionary.Source == source))
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
    }
}
