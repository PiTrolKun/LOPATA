using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Numbers are printed by the calculator. Interpretations must not invent a second numeric report.</summary>
public static class FinancialReplyQuality
{
    public static IReadOnlyList<string> Issues(string text, string language, string unit = "")
    {
        var issues = new List<string>();
        var prose = Regex.Replace(text, @"(?m)^\s*\d{1,2}[.)]\s+", "");
        if (Regex.IsMatch(prose, @"\p{N}")) issues.Add("numeric prose; leave all figures to the program tables");
        var cyrillic = Regex.Matches(prose, @"[А-Яа-яЁё]").Count;
        var latin = Regex.Matches(prose, @"[A-Za-z]").Count;
        if (language == "en" ? latin < cyrillic || latin == 0 : cyrillic < latin || cyrillic == 0) issues.Add("wrong response language");
        var selected = FinancialCurrencies.Resolve(unit, language);
        if (FinancialCurrencies.Find(selected) is not null &&
            FinancialCurrencies.All.Any(c => c.Code != selected && Regex.IsMatch(prose, @"\b" + c.Code + @"\b")))
            issues.Add("wrong currency code; use only the selected unit " + selected);
        var currencyName = FinancialCurrencies.Find(selected)?.Name(language);
        if (currencyName is not null && (selected != "RUB" && Regex.IsMatch(prose, @"рубл|ruble|rouble", RegexOptions.IgnoreCase) ||
            selected != "EUR" && Regex.IsMatch(prose, @"евро|\beuros?\b", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(currencyName, "доллар|dollar", RegexOptions.IgnoreCase) && Regex.IsMatch(prose, "доллар|dollar", RegexOptions.IgnoreCase)))
            issues.Add("wrong currency name; use only the selected unit " + selected);
        return issues;
    }
}
