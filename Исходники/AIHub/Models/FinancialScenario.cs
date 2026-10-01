namespace AIHub.Models;

public enum FinancialPeriod { Day, Week, Month }
public enum FinancialAnswerKind { Known, Unknown, Skipped }
public enum FinancialCoverage { Self, External, Partial }
public sealed record FinancialAnswer(string Id, FinancialAnswerKind Kind, decimal Amount = 0,
    FinancialPeriod Period = FinancialPeriod.Month, FinancialCoverage Coverage = FinancialCoverage.Self,
    decimal ExternalAmount = 0, string Source = "");
public sealed record FinancialPerson(string Name = "", int? Age = null, string Location = "", string Status = "employed");
public sealed record FinancialInput
{
    public int Schema { get; init; } = 1;
    public FinancialPerson Person { get; init; } = new();
    public string Unit { get; init; } = "";
    public string Language { get; init; } = "ru";
    public List<FinancialAnswer> Answers { get; init; } = [];
}
public sealed record FinancialCategoryTotal(string Id, decimal Total, decimal External, decimal Personal, decimal? Share);
public sealed record FinancialCalculation(decimal Income, decimal StableIncome, decimal AdditionalIncome,
    decimal Expenses, decimal External, decimal Personal, decimal Balance,
    decimal RequiredPersonal, decimal OtherPersonal, decimal? StableCoverageRatio,
    IReadOnlyList<FinancialCategoryTotal> Categories, IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}
public sealed record FinancialRun(FinancialInput Input, DebugModelInfo Model, string Revision);
public sealed record FinancialStageResult(string Id, string Revision, string ModelPath, string ModelName,
    string Text, double Seconds, DateTimeOffset CompletedUtc, int AnalysisVersion = 0);
public sealed record FinancialWorkReference(string Directory);
