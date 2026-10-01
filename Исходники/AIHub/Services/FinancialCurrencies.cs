using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record FinancialCurrency(string Code, string Ru, string En)
{
    public string Name(string language) => language == "en" ? En : Ru;
}

/// <summary>Offline ISO 4217 catalog. Selecting a unit never converts questionnaire amounts.</summary>
public static class FinancialCurrencies
{
    public static IReadOnlyList<FinancialCurrency> All { get; } = Load();
    public static string Default(string language) => language == "en" ? "USD" : "RUB";
    public static string Resolve(string unit, string language) => string.IsNullOrWhiteSpace(unit) ? Default(language) : unit.Trim();
    public static FinancialCurrency? Find(string unit) => All.FirstOrDefault(c => c.Code == unit);

    private static IReadOnlyList<FinancialCurrency> Load()
    {
        using var stream = typeof(FinancialCurrencies).Assembly.GetManifestResourceStream("AIHub.Content.FinancialCurrencies.json")
            ?? throw new InvalidDataException("Missing offline currency catalog.");
        using var document = JsonDocument.Parse(stream);
        var currencies = document.RootElement.GetProperty("Currencies").Deserialize<FinancialCurrency[]>()
            ?? throw new InvalidDataException("Invalid offline currency catalog.");
        if (currencies.Length == 0 || currencies.Select(c => c.Code).Distinct().Count() != currencies.Length ||
            currencies.Any(c => c.Code.Length != 3 || string.IsNullOrWhiteSpace(c.Ru) || string.IsNullOrWhiteSpace(c.En)) ||
            !currencies.Any(c => c.Code == "RUB") || !currencies.Any(c => c.Code == "USD"))
            throw new InvalidDataException("Invalid offline currency entries.");
        return Array.AsReadOnly(currencies);
    }
}
