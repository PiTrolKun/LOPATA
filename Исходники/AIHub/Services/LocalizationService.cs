using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed class LocalizationService
{
    private const string FallbackLanguageCode = "ru";

    private readonly Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentLanguageCode { get; private set; } = FallbackLanguageCode;

    public IReadOnlyList<LanguageOption> GetAvailableLanguages()
    {
        var codes = Directory
            .EnumerateFiles(BuiltInLocalizationDirectory, "*.json")
            .Concat(Directory.Exists(AppDataPaths.LocalizationDirectory)
                ? Directory.EnumerateFiles(AppDataPaths.LocalizationDirectory, "*.json")
                : Enumerable.Empty<string>())
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code == FallbackLanguageCode ? 0 : 1)
            .ThenBy(code => code)
            .Select(code => new LanguageOption(code, GetLanguageDisplayName(code)))
            .ToList();

        return codes.Count == 0
            ? [new LanguageOption(FallbackLanguageCode, "Русский")]
            : codes;
    }

    public bool HasLanguage(string languageCode)
    {
        var normalized = NormalizeLanguageCode(languageCode);
        return File.Exists(GetBuiltInLocalizationPath(normalized))
            || File.Exists(GetUserLocalizationPath(normalized));
    }

    public void Load(string languageCode)
    {
        var normalized = HasLanguage(languageCode)
            ? NormalizeLanguageCode(languageCode)
            : FallbackLanguageCode;

        _fallback.Clear();
        foreach (var pair in LoadDictionary(FallbackLanguageCode))
        {
            _fallback[pair.Key] = pair.Value;
        }

        _current.Clear();
        foreach (var pair in LoadDictionary(normalized))
        {
            _current[pair.Key] = pair.Value;
        }

        CurrentLanguageCode = normalized;
    }

    public string T(string key)
    {
        if (_current.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (_fallback.TryGetValue(key, out var fallbackValue) && !string.IsNullOrWhiteSpace(fallbackValue))
        {
            return fallbackValue;
        }

        return key;
    }

    internal Dictionary<string, string> IslandCaptions()
    {
        const string prefix = "NabyScape.";
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Open LOPATA"] = T("Island.Open"),
            ["Close island"] = T("Island.Close"),
            ["Above windows"] = T("Island.Above"),
            ["Updates are managed by LOPATA"] = T("Island.UpdatesViaLopata")
        };
        // The island has the built-in strings in its own package. Only send additions and
        // overrides from the selected user file, which may be a copy of the entire base.
        if (!File.Exists(GetUserLocalizationPath(CurrentLanguageCode))) return result;
        var builtIn = ReadDictionary(GetBuiltInLocalizationPath(CurrentLanguageCode));
        foreach (var (key, value) in _current)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length
                && !string.IsNullOrWhiteSpace(value)
                && (!builtIn.TryGetValue(key, out var original) || value != original))
                result[key[prefix.Length..]] = value;
        return result;
    }

    public static string GetWindowsLanguageCode()
    {
        return NormalizeLanguageCode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    public static string NormalizeLanguageCode(string? languageCode)
    {
        return string.IsNullOrWhiteSpace(languageCode)
            ? FallbackLanguageCode
            : languageCode.Trim().ToLowerInvariant();
    }

    private static string BuiltInLocalizationDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Localization");

    private static string GetBuiltInLocalizationPath(string languageCode) =>
        Path.Combine(BuiltInLocalizationDirectory, $"{languageCode}.json");

    private static string GetUserLocalizationPath(string languageCode) =>
        Path.Combine(AppDataPaths.LocalizationDirectory, $"{languageCode}.json");

    private static string GetLanguageDisplayName(string languageCode)
    {
        return languageCode switch
        {
            "ru" => "Русский",
            "en" => "English",
            _ => languageCode
        };
    }

    private static Dictionary<string, string> LoadDictionary(string languageCode)
    {
        var path = File.Exists(GetUserLocalizationPath(languageCode))
            ? GetUserLocalizationPath(languageCode)
            : GetBuiltInLocalizationPath(languageCode);
        return ReadDictionary(path);
    }

    private static Dictionary<string, string> ReadDictionary(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}
