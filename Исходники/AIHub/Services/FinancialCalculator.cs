using System.Globalization;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Comparable conditional month: 30 days, 7 days per week. Never a currency conversion.</summary>
public static class FinancialCalculator
{
    public static decimal Monthly(decimal value, FinancialPeriod period) => period switch
    { FinancialPeriod.Day => value * 30m, FinancialPeriod.Week => value * 30m / 7m, FinancialPeriod.Month => value, _ => throw new ArgumentOutOfRangeException(nameof(period)) };
    public static decimal ForPeriod(decimal monthly, FinancialPeriod period) => period switch
    { FinancialPeriod.Day => monthly / 30m, FinancialPeriod.Week => monthly * 7m / 30m, FinancialPeriod.Month => monthly, _ => throw new ArgumentOutOfRangeException(nameof(period)) };
    public static bool TryAmount(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(" ", "").Replace("\u00a0", "").Replace(',', '.'),
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
        && value >= 0 && value <= 1_000_000_000_000m;
    public static void Validate(FinancialInput input)
    {
        if (input is null || input.Person is null || input.Unit is null || input.Answers is null)
            throw new InvalidDataException("Missing financial input.");
        if (input.Schema is not (1 or 2) || input.Person.Age is < 1 or > 120 || !FinancialQuestions.Statuses.Contains(input.Person.Status)
            || input.Unit.Length > 40 || input.Person.Name.Length > 120 || input.Person.Location.Length > 200
            || input.Answers.Select(a => a.Id).Distinct().Count() != input.Answers.Count)
            throw new InvalidDataException("Invalid financial input.");
        foreach (var a in input.Answers)
        {
            var question = FinancialQuestions.All.FirstOrDefault(q => q.Id == a.Id) ?? throw new InvalidDataException("Unknown question.");
            if (!Enum.IsDefined(a.Kind) || !Enum.IsDefined(a.Period) || !Enum.IsDefined(a.Coverage)
                || a.Amount < 0 || a.Amount > 1_000_000_000_000m || a.ExternalAmount < 0 || a.Source.Length > 200
                || (input.Schema == 1 && a.Coverage == FinancialCoverage.Partial && a.ExternalAmount > a.Amount)
                || (input.Schema == 2 && (a.ExternalAmount != 0 || (a.Coverage == FinancialCoverage.External && a.Amount != 0)))
                || (question.Income && (a.Coverage != FinancialCoverage.Self || a.ExternalAmount != 0)))
                throw new InvalidDataException("Invalid financial answer.");
        }
    }
    public static FinancialAnswer ForPersonalInput(FinancialAnswer answer, int schema) => schema == 2 ? answer : answer with
    {
        Amount = answer.Coverage switch { FinancialCoverage.External => 0, FinancialCoverage.Partial => answer.Amount - answer.ExternalAmount, _ => answer.Amount },
        ExternalAmount = 0,
        Source = "",
        Kind = answer.Coverage == FinancialCoverage.External ? FinancialAnswerKind.Known : answer.Kind
    };
    public static FinancialCalculation Calculate(FinancialInput input)
    {
        Validate(input);
        var missing = FinancialQuestions.All.Where(q => input.Answers.All(a => a.Id != q.Id || a.Kind != FinancialAnswerKind.Known)).Select(q => q.Id).ToArray();
        var known = input.Answers.Where(a => a.Kind == FinancialAnswerKind.Known).ToArray();
        decimal Income(string id) => known.Where(a => a.Id == id).Sum(a => Monthly(a.Amount, a.Period));
        var stable = Income("stable_income"); var additional = Income("additional_income");
        var groups = FinancialQuestions.All.Where(q => !q.Income).GroupBy(q => q.Category).Select(g =>
        {
            var answers = known.Where(a => g.Any(q => q.Id == a.Id)).ToArray();
            var total = answers.Sum(a => Monthly(a.Amount, a.Period));
            var external = input.Schema == 2 ? 0 : answers.Sum(a => Monthly(a.Coverage switch { FinancialCoverage.Self => 0, FinancialCoverage.External => a.Amount, _ => a.ExternalAmount }, a.Period));
            return new FinancialCategoryTotal(g.Key, total, external, total - external, null);
        }).ToArray();
        var expenses = groups.Sum(g => g.Total); var outside = groups.Sum(g => g.External); var personal = expenses - outside;
        var required = groups.Single(g => g.Id == "required").Personal;
        return new(stable + additional, stable, additional, expenses, outside, personal, stable + additional - personal,
            required, personal - required, missing.Length == 0 && personal != 0 ? stable / personal : null,
            groups.Select(g => g with { Share = missing.Length == 0 && expenses != 0 ? g.Total / expenses * 100m : null }).ToArray(), missing);
    }
}
