namespace AIHub.Services;

internal static class MusicPoetryLanguage
{
    internal static string Instruction(string? language)
    {
        var code = LocalizationService.NormalizeLanguageCode(language);
        if (code == "ru" || code.StartsWith("ru-", StringComparison.Ordinal))
            return "Язык интерфейса — русский. Весь текст рассуждений пиши только на русском языке, даже если песня запрошена на другом языке. " +
                "Песню по умолчанию пиши на русском; явная просьба человека о другом языке песни имеет приоритет только для песни. " +
                "Не смешивай язык рассуждений с языком песни.";
        if (code == "en" || code.StartsWith("en-", StringComparison.Ordinal))
            return "The interface language is English. Write all reasoning exclusively in English, even when the requested lyrics are in another language. " +
                "Write lyrics in English by default; an explicit request for another lyric language overrides only the lyric language. " +
                "Keep the reasoning language separate from the lyric language.";
        return "Write all reasoning exclusively in the interfaceLanguage specified below, independently of the requested lyric language.";
    }
}
