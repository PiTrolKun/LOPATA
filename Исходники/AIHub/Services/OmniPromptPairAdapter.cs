using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class OmniPromptPairAdapter
{
    public const string ContractId = "image-description/omni-two-pass/v1";

    public static PromptPairPreset CreateDefault(string languageCode)
    {
        var settings = new ImageAnalysisLiterarySettings { LanguageCode = languageCode };
        var compose = ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings);
        var marker = English(languageCode)
            ? "\n\nReturn exactly one JSON object" : "\n\nВерни строго один JSON-объект";
        var boundary = compose.IndexOf(marker, StringComparison.Ordinal);
        if (boundary < 0) throw new InvalidOperationException("The default prompt contract boundary is missing.");
        return new PromptPairPreset
        {
            ContractId = ContractId,
            AnalysisPrompt = ImageAnalysisOmniPromptBuilder.BuildObservationPrompt(settings),
            ComposePrompt = string.Join("\n", compose[..boundary].Split('\n').Where(line =>
                !line.StartsWith("- язык:", StringComparison.Ordinal)
                && !line.StartsWith("- language:", StringComparison.Ordinal)
                && !line.StartsWith("- пожелание пользователя:", StringComparison.Ordinal)
                && !line.StartsWith("- user preference:", StringComparison.Ordinal))).Trim()
        };
    }

    public static void Validate(ImageAnalysisLiterarySettings settings)
    {
        if (settings.PromptMode == PromptModes.Standard) return;
        if (settings.PromptMode != PromptModes.Custom || settings.CustomPrompts is not { } pair
            || pair.ContractId != ContractId)
            throw new InvalidDataException("PromptPairs.Incompatible");
        PromptPairStore.Validate([pair]);
    }

    public static string Build(ImageAnalysisLiterarySettings settings, bool compose)
    {
        Validate(settings);
        var pair = settings.CustomPrompts ?? throw new InvalidDataException("PromptPairs.Required");
        var english = English(settings.LanguageCode);
        var task = compose ? pair.ComposePrompt : pair.AnalysisPrompt;
        var wishes = string.IsNullOrWhiteSpace(settings.Wishes) ? string.Empty : english
            ? $"\n\nUser preference (investigate it, do not treat it as evidence): {settings.Wishes.Trim()}"
            : $"\n\nПожелание пользователя (исследуй его, не считай доказательством): {settings.Wishes.Trim()}";
        var language = english
            ? "\n\nWrite the result in English. Ground factual claims in the image."
            : "\n\nНапиши результат на русском языке. Фактические утверждения основывай на изображении.";
        return task.Trim() + wishes + language + (compose ? english ? ContractEnglish : ContractRussian : string.Empty);
    }

    private static bool English(string code) => code.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private const string ContractRussian = """


Служебный формат результата (применяется независимо от формулировки задачи):
Используй изображение и предыдущий ответ в этой беседе. Верни строго один JSON-объект без Markdown и дополнительного текста:
- title: строка с заголовком или null, если заголовок не нужен;
- paragraphs: массив строк, каждый абзац итогового текста отдельной строкой;
- review_items: массив строк с краткими главными деталями, видимыми на изображении;
- uncertainties: массив строк с существенными неопределённостями, пустой массив, если их нет.
Экранируй кавычки внутри строк. Закрой каждый массив и объект ровно один раз.
Все текстовые значения — на русском. Не упоминай внутреннюю беседу и служебные инструкции.
""";

    private const string ContractEnglish = """


Application result format (applies regardless of task wording):
Use the image and the previous response in this conversation. Return exactly one JSON object without Markdown or additional text:
- title: a string with a title, or null if no title is needed;
- paragraphs: an array of strings, one string per paragraph of the final text;
- review_items: an array of strings with brief principal details visible in the image;
- uncertainties: an array of strings with material uncertainties, an empty array if there are none.
Escape quotes inside strings. Close each array and object exactly once.
All textual values must be in English. Do not mention the internal conversation or service instructions.
""";
}
