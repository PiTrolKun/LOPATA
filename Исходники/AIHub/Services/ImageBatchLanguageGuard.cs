using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Conservative RU/EN script check, not a general language detector.</summary>
public static class ImageBatchLanguageGuard
{
    public static bool Matches(ImageBatchSection section, string language)
    {
        if (section.Paragraphs is null) return false;
        // Names, URLs and quoted inscriptions may legitimately retain their original spelling.
        var paragraphs = section.Paragraphs.Select(p => Regex.Replace(p ?? "",
            "https?://\\S+|\"[^\"]*\"|«[^»]*»|“[^”]*”", " ")).ToArray();
        return paragraphs.Append(string.Join(" ", paragraphs)).All(text => {
            int cyrillic = text.Count(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');
            int latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            int letters = cyrillic + latin;
            if (letters < 40) return true;
            return language == "ru" ? cyrillic >= letters * 0.15
                : language != "en" || latin >= letters * 0.85;
        });
    }
}
