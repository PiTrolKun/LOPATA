using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPoetryModelWindow
{
    private static void AddReasoningBadge(TextBlock name, string id, Func<string, string> localize)
    {
        if (!MusicPoetryReasoning.SupportsId(id)) return;
        var extended = MusicPoetryReasoning.Extended(id);
        var hint = extended
            ? localize("Music.Poetry.Selection.ReasoningBadgeExtended")
            : string.Format(localize("Music.Poetry.Selection.ReasoningBadgeFixed"), MusicPoetryReasoning.DefaultBudget(id));
        if (id == "gamma") hint += "\n" + localize("Music.Poetry.Selection.ReasoningBadgeEffort");
        var badge = new TextBlock { FontSize = name.FontSize, FontWeight = FontWeights.Normal,
            ToolTip = hint, Margin = new(8, 0, 0, 0) };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        badge.Inlines.Add(new Run("🧠") { FontFamily = new FontFamily("Segoe UI Emoji") });
        if (extended) badge.Inlines.Add(new Run(" II"));
        AutomationProperties.SetAutomationId(badge, "Music.Poetry.ReasoningBadge." + id);
        AutomationProperties.SetName(badge, hint);
        AutomationProperties.SetHelpText(badge, hint);
        name.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
    }
}
