using System.Windows.Controls;
using System.Windows.Markup;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>WPF uses the Windows ISpellChecker backend on supported Windows versions.</summary>
public static class LiterarySpellChecking
{
    public static void Enable(TextBox input, string language)
    {
        input.Language = XmlLanguage.GetLanguage(language.StartsWith("en",StringComparison.OrdinalIgnoreCase) ? "en-US" : "ru-RU");
        SpellCheck.SetIsEnabled(input,true);
        // Keep the native spelling context menu; suggestions only apply on explicit selection.
    }
}
