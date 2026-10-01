namespace AIHub.Services;

public sealed record FinancialAnalysisStage(string Id, string Focus, bool Report = false)
{
    public FinancialExpertRole Expert => FinancialAnalysisRoles.For(Id);
    public int MaxTokens => Id switch { "final" => 8192, "professional" => 4096, "practical" => 1800, "simple" => 1000, _ => 900 };
}
public static class FinancialAnalysisPlan
{
    public const string BackgroundKind = "finance.analysis";
    public const int AnalysisVersion = 2;
    public static IReadOnlyList<FinancialAnalysisStage> Stages { get; } = Array.AsReadOnly<FinancialAnalysisStage>(
    [
        new("overview", "Overall picture and regular balance"), new("required", "Required regular expenses"),
        new("everyday", "Everyday food, communication and hygiene"), new("subscriptions", "Subscriptions and recurring services"),
        new("transport", "Regular transportation"), new("health", "Regular health expenses; do not recommend stopping essential treatment"),
        new("pleasures", "Pleasures and recurring hobbies without moral judgments"), new("personal", "Personal care"),
        new("pets", "Regular pet care"), new("education", "Education"), new("relatives", "Constant support for relatives"),
        new("external", "External coverage and personal burden, without treating coverage as income"),
        new("largest", "Largest expense shares and concentration"), new("missing", "Missing data and boundaries of known totals"),
        new("economy", "Optional practical savings, avoiding invented prices or guarantees"),
        new("resilience", "Resilience of recurring expenses to loss of external coverage or additional income"),
        new("alignment", "Stable income versus personal recurring expenses"),
        new("simple", "Short understandable explanation; adapt presentation only to the supplied age", true),
        new("practical", "Practical analysis and optional next steps; adapt presentation only to the supplied age", true),
        new("professional", "Professional interpretation of program indicators; explain terminology", true),
        new("final", "Final professional expert: reconcile all specialist conclusions, flag disagreements, produce a coherent full report", true)
    ]);
}
