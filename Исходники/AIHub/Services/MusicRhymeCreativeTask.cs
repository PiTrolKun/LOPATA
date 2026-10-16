using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>The second independent task deliberately receives no original lyrics or editing draft.</summary>
public static class MusicRhymeCreativeTask
{
    public static string SummaryPrompt(MusicRhymeStep step) =>
        "Кратко перескажи сюжет песни, сохранив героя, основные события, образы и настроение. Не цитируй строки. "
        + "Напиши пересказ на языке исходной песни.\n\n"
        + step.Request.Text;

    public static string ComposePrompt(MusicRhymeStep step)
    {
        if (string.IsNullOrWhiteSpace(step.CreativeSummary)) throw new InvalidDataException("Music.Rhyme.InvalidReply");
        var prompt = new StringBuilder("Напиши песню по этому описанию на его языке. Верни только текст песни, без пояснений.\n\n");
        prompt.Append(step.CreativeSummary);
        if (!string.IsNullOrWhiteSpace(step.Reference))
        {
            using var reference = JsonDocument.Parse(step.Reference);
            if (reference.RootElement.TryGetProperty("books", out var books))
            {
                var selected = books.EnumerateArray().Take(step.Request.Tuning.Integer("RagPromptHits")).ToArray();
                if (selected.Length > 0)
                {
                    prompt.Append("\n\nКнижная справка. Используй её при работе над песней:\n");
                    foreach (var book in selected)
                    {
                        var text = book.GetProperty("text").GetString() ?? "";
                        prompt.Append(book.GetProperty("source").GetString()).Append(":\n")
                            .Append(text.AsSpan(0, Math.Min(text.Length, step.Request.Tuning.Integer("RagPromptChars"))))
                            .Append('\n');
                    }
                }
            }
        }
        return prompt.ToString();
    }

    public static string SongFromReply(string reply)
    {
        var text = reply.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        // Preserve the full reply separately. Remove only recognizable presentation wrappers/afterword.
        var fenced = Regex.Match(text, @"\A[^\n]*(?:\n\s*)?```[^\n]*\n(?<song>[\s\S]*?)\n```",
            RegexOptions.CultureInvariant);
        if (fenced.Success) text = fenced.Groups["song"].Value.Trim();
        text = Regex.Replace(text,
            @"\A(?:Конечно[!,.]?\s*)?(?:Вот\s+(?:новая\s+|переработанная\s+)?(?:песня|версия песни)|Here(?:'s| is)\s+(?:the\s+|a\s+)?(?:new\s+)?song)[^\n]*\n+",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var afterword = Regex.Match(text,
            @"(?im)^(?:\s*---\s*\n)?\s*(?:\*\*|#{1,6}\s*)?(?:Примечани[еяя]|Пояснени[ея]|Комментарий|Комментарии|Notes?|Explanation|Commentary)(?:\s[^\n:]*)?\s*[:：](?:\*\*)?",
            RegexOptions.CultureInvariant);
        if (afterword.Success) text = text[..afterword.Index].TrimEnd();
        return text;
    }

    public static void ValidateSong(string song, MusicRhymeTuning tuning)
    {
        if (string.IsNullOrWhiteSpace(song) || song.Length > tuning.Integer("MaxAnswerChars")
            || song.Split('\n').Count(line => Regex.IsMatch(line, @"\p{L}")) < 2)
            throw new InvalidDataException("Music.Rhyme.InvalidReply");
    }
}
