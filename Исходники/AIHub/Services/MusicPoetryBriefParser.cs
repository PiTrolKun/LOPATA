using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Recognizes explicit brief headings and labels only; does not infer edits from prose.</summary>
internal static class MusicPoetryBriefParser
{
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static MusicPoetryBrief Parse(string text)
    {
        var parts = new[] { new List<string>(), new List<string>(), new List<string>() };
        var topic = 2;
        // Explicit colon-marked headings may follow another section on the same
        // line. Plain tag mentions and unknown brackets remain creative text.
        var sectionLines = Regex.Replace(text, @"[ \t]+(?=\[(?:Idea|Structure|Music)\][ \t]*:)", "\n", IgnoreCase);
        foreach (var line in sectionLines.Split('\n'))
        {
            var value = line.Trim();
            var tag = Regex.Match(value, @"^\[(Idea|Structure|Music)\]\s*:?[ \t]*(.*)$", IgnoreCase);
            if (tag.Success)
            {
                topic = tag.Groups[1].Value.ToLowerInvariant() switch { "idea" => 0, "structure" => 1, _ => 2 };
                value = tag.Groups[2].Value;
            }
            else
            {
                var heading = Regex.Match(value, @"^(Идея песни|Song idea|Текст и структура|Lyrics and structure|Музыка и исполнение|Music and performance|Музыкальные параметры|Musical parameters)(?:\s*:\s*(.*)|\s*)$", IgnoreCase);
                if (heading.Success)
                {
                    topic = heading.Groups[1].Value.ToLowerInvariant() switch {
                        "идея песни" or "song idea" => 0,
                        "текст и структура" or "lyrics and structure" => 1, _ => 2 };
                    value = heading.Groups[2].Value;
                }
            }
            if (value.Length == 0)
            {
                if (!tag.Success) parts[topic].Add("");
                continue;
            }
            var routed = Regex.IsMatch(value, @"^(Название|Исполнитель|Комментарий|Тема|Сюжет|Title|Artist|Comment|Theme|Plot|Music\.Output\.(Title|Artist|Comment))\s*:", IgnoreCase) ? 0
                : Regex.IsMatch(value, @"^(Язык|Рифма|Структура|Language|Rhyme|Structure|vocal_language)\s*[:=]", IgnoreCase) ? 1
                : Regex.IsMatch(value, @"^(Музыка|Жанр|Стиль|Вокал|Инструменты|Темп|Ритм|Настроение|Тональность|Размер|Пожелания|Music|Genre|Style|Vocals?|Instruments?|Tempo|Rhythm|Mood|Key|Time signature|Wishes|bpm|timesignature|keyscale)\s*[:=]", IgnoreCase) ? 2 : topic;
            topic = routed;
            parts[topic].Add(value);
        }
        return new(string.Join("\n", parts[0]).Trim(), string.Join("\n", parts[1]).Trim(), string.Join("\n", parts[2]).Trim());
    }
}
