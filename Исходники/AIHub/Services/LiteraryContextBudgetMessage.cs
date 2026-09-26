using System.Globalization;

namespace AIHub.Services;

public static class LiteraryContextBudgetMessage
{
    public static string Format(ImageAnalysisContextExhaustedException error, Func<string, string> localize,
        string inputKey = "Paragraph.ContextLimit", string outputKey = "Paragraph.OutputLimit")
    {
        if (error.OutputTruncated) return localize(outputKey);
        if (error.Budget is not { } budget) return localize(inputKey);
        if (budget.ExceedsModelContext)
            return string.Format(CultureInfo.CurrentCulture, localize("Literary.Context.ModelLimit"),
                budget.ModelContextTokens, budget.InputTokens);
        return string.Format(CultureInfo.CurrentCulture, localize("Literary.Context.BudgetDetails"),
            budget.ContextTokens, budget.InputTokens, budget.SafetyTokens, budget.MinimumReplyTokens);
    }
}
