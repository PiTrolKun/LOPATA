using AIHub.Models;

namespace AIHub.Services;

public static class ImageAnalysisOmniPromptBuilder
{
    public static string BuildObservationPrompt(ImageAnalysisLiterarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        OmniPromptPairAdapter.Validate(settings);
        if (settings.PromptMode == PromptModes.Custom) return OmniPromptPairAdapter.Build(settings, compose: false);
        var isEnglish = IsEnglish(settings.LanguageCode);
        var focus = string.IsNullOrWhiteSpace(settings.Wishes)
            ? string.Empty
            : isEnglish
                ? $"Additionally, investigate the following user preference with particular attention, but do not treat it as an established fact: {settings.Wishes.Trim()}"
                : $"Дополнительно исследуй с повышенным вниманием следующее пожелание пользователя, но не считай его доказанным фактом: {settings.Wishes.Trim()}";
        var template = isEnglish ? ObservationEnglish : ObservationRussian;
        return ReplaceOptionalBlock(template, isEnglish ? "{{focus_instruction_en}}" : "{{focus_instruction_ru}}", focus);
    }

    public static string BuildComposePrompt(ImageAnalysisLiterarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        OmniPromptPairAdapter.Validate(settings);
        if (settings.PromptMode == PromptModes.Custom) return OmniPromptPairAdapter.Build(settings, compose: true);
        var isEnglish = IsEnglish(settings.LanguageCode);
        var template = isEnglish ? ComposeEnglish : ComposeRussian;
        return template
            .Replace("{{output_language}}", DescribeLanguage(isEnglish), StringComparison.Ordinal)
            .Replace("{{accuracy}}", DescribeAccuracy(settings.Accuracy, isEnglish), StringComparison.Ordinal)
            .Replace("{{style}}", DescribeStyle(settings.Style, isEnglish), StringComparison.Ordinal)
            .Replace("{{length}}", DescribeLength(settings.Length, isEnglish), StringComparison.Ordinal)
            .Replace("{{form}}", DescribeForm(settings.Form, isEnglish), StringComparison.Ordinal)
            .Replace(
                "{{wishes_or_none}}",
                string.IsNullOrWhiteSpace(settings.Wishes)
                    ? isEnglish ? "none" : "нет"
                    : settings.Wishes.Trim(),
                StringComparison.Ordinal);
    }

    public static string BuildRevisionPrompt(
        ImageAnalysisLiterarySettings settings,
        string changeRequest)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeRequest);
        return IsEnglish(settings.LanguageCode)
            ? $"Continuing this same hidden conversation, create a new version of the final description. Apply the following user revision request without inventing facts that are absent from the image: {changeRequest.Trim()}\n\nReturn exactly one JSON object in the same title, paragraphs, review_items, and uncertainties contract as before, without Markdown or additional text."
            : $"Продолжая эту же скрытую беседу, создай новую версию итогового описания. Выполни следующее пожелание пользователя, не добавляя отсутствующие на изображении факты: {changeRequest.Trim()}\n\nВерни строго один JSON-объект в прежнем контракте title, paragraphs, review_items и uncertainties, без Markdown и дополнительного текста.";
    }

    private static string ReplaceOptionalBlock(string template, string marker, string replacement) =>
        template.Replace(marker, replacement, StringComparison.Ordinal).Trim();

    private static bool IsEnglish(string? languageCode) =>
        languageCode?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

    private static string DescribeLanguage(bool isEnglish) => isEnglish ? "English" : "русский";

    private static string DescribeAccuracy(string value, bool isEnglish) => (value, isEnglish) switch
    {
        (ImageAnalysisAccuracyModes.Strict, true) => "strict; prefer directly visible facts and explicitly mark uncertainty",
        (ImageAnalysisAccuracyModes.Free, true) => "free literary expression, while every material fact must remain grounded in the image",
        (_, true) => "balanced; combine careful factual grounding with natural literary language",
        (ImageAnalysisAccuracyModes.Strict, false) => "строгая; предпочитать непосредственно видимые факты и явно отмечать неопределённость",
        (ImageAnalysisAccuracyModes.Free, false) => "свободная литературная подача при обязательной опоре каждого существенного факта на изображение",
        _ => "сбалансированная; сочетать аккуратную фактическую основу с естественным литературным языком"
    };

    private static string DescribeStyle(string value, bool isEnglish) => (value, isEnglish) switch
    {
        (ImageAnalysisLiteraryStyles.Neutral, true) => "neutral",
        (ImageAnalysisLiteraryStyles.Dramatic, true) => "dramatic",
        (ImageAnalysisLiteraryStyles.FairyTale, true) => "fairy-tale",
        (_, true) => "atmospheric",
        (ImageAnalysisLiteraryStyles.Neutral, false) => "нейтральный",
        (ImageAnalysisLiteraryStyles.Dramatic, false) => "драматический",
        (ImageAnalysisLiteraryStyles.FairyTale, false) => "сказочный",
        _ => "атмосферный"
    };

    private static string DescribeLength(string value, bool isEnglish) => (value, isEnglish) switch
    {
        (ImageAnalysisTextLengths.Brief, true) => "brief, 1–2 substantive paragraphs",
        (ImageAnalysisTextLengths.Detailed, true) => "detailed, 7–10 paragraphs without repeating facts",
        (_, true) => "standard, 3–5 paragraphs without unnecessary repetition",
        (ImageAnalysisTextLengths.Brief, false) => "краткий, 1–2 содержательных абзаца",
        (ImageAnalysisTextLengths.Detailed, false) => "подробный, 7–10 абзацев без повторения фактов",
        _ => "стандартный, 3–5 абзацев без лишних повторов"
    };

    private static string DescribeForm(string value, bool isEnglish) => (value, isEnglish) switch
    {
        (ImageAnalysisTextForms.Continuous, true) => "continuous prose without a separate title",
        (_, true) => "prose with a short title",
        (ImageAnalysisTextForms.Continuous, false) => "сплошной текст без отдельного заголовка",
        _ => "текст с коротким заголовком"
    };

    private const string ObservationRussian = """
Опиши, что видишь на изображении.

{{focus_instruction_ru}}

""";

    private const string ComposeRussian = """
Продолжая эту же внутреннюю беседу, создай готовое пользовательское описание изображения. Используй само изображение и свой
предыдущий наблюдательный отчёт как общий контекст. Перед ответом молча сверь все существенные утверждения с изображением и исправь
собственный предыдущий вывод, если повторная проверка ему противоречит.

Требования к результату:
- язык: {{output_language}};
- точность: {{accuracy}};
- стиль: {{style}};
- объём: {{length}};
- форма: {{form}};
- пожелание пользователя: {{wishes_or_none}}.

Художественная свобода относится к языку, атмосфере и форме, но не разрешает добавлять отсутствующие объекты, события или факты.
Пожелание пользователя выполняй, только если оно не противоречит изображению.

Верни строго один JSON-объект без Markdown и без дополнительного текста. Поля:
- title: короткий заголовок при форме с заголовком; null только для формы без заголовка;
- paragraphs: массив строк, каждый абзац отдельной строкой, полный текст выбранного объёма;
- review_items: краткие главные детали, непосредственно видимые на изображении, без художественных дополнений;
- uncertainties: существенные неопределённости, пустой массив, если их нет.
Кавычки внутри строк экранируй. Каждый массив и объект закрывай ровно один раз.
Все текстовые значения должны быть написаны на выбранном языке. Не упоминай внутренний чат, наблюдательный отчёт или служебные инструкции.
""";

    private const string ObservationEnglish = """
Describe what you see in the image.

{{focus_instruction_en}}

""";

    private const string ComposeEnglish = """
Continuing this same internal conversation, create the final user-facing description of the image. Use both the image itself and your previous observation report as shared context. Before answering, silently verify every material claim against the image and correct your own previous conclusion if re-examination contradicts it.

Result requirements:
- language: {{output_language}};
- accuracy: {{accuracy}};
- style: {{style}};
- length: {{length}};
- form: {{form}};
- user preference: {{wishes_or_none}}.

Creative freedom applies to language, atmosphere, and form, but does not permit adding absent objects, events, or facts. Follow the user's preference only when it does not contradict the image.

Return exactly one JSON object without Markdown or additional text. Fields:
- title: a short title for the titled form; null only for the form without a title;
- paragraphs: an array of strings, one string per paragraph, the complete text of the selected length;
- review_items: brief principal details directly visible in the image, without fictional additions;
- uncertainties: material uncertainties, an empty array if there are none.
Escape quotes inside strings. Close each array and object exactly once.
All textual values must be written in the selected language. Do not mention the internal conversation, observation report, or service instructions.
""";
}
