using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Turns conservative findings into a stanza-level editing task, keeping the user's scope intact.</summary>
public static class MusicRhymeTask
{
    public static HashSet<int> EditableLines(MusicRhymeStep step)
    {
        var stanzas = MusicRhymeWork.Issues(step)
            .Select(i => step.Analysis!.Lines.Single(l => l.Number == i.Line).Stanza).ToHashSet();
        return step.Analysis!.Lines.Where(l => !l.Performance && stanzas.Contains(l.Stanza)
            && l.Start >= step.Request.Start && l.Start < step.Request.Start + step.Request.Length)
            .Select(l => Position(step, l) - 1).ToHashSet();
    }

    public static string Prompt(MusicRhymeStep step)
    {
        var lines = step.Analysis!.Lines;
        var issues = MusicRhymeWork.Issues(step).ToArray();
        var stanzas = issues.Select(i => lines.Single(l => l.Number == i.Line).Stanza).ToHashSet();
        using var reference = JsonDocument.Parse(step.Reference);
        var books = reference.RootElement.TryGetProperty("books", out var found) ? found.EnumerateArray().Take(2).Select(h => new {
            source = h.GetProperty("source").GetString(), text = h.GetProperty("text").GetString()![..Math.Min(450, h.GetProperty("text").GetString()!.Length)]
        }).ToArray() : [];
        return JsonSerializer.Serialize(new {
            task = "Сделай рифмованный вариант этой строфы с тем же смыслом. "
                + "Если схема не определена (Review), попробуй рифмовать строки 1–3 и 2–4 в каждом четверостишии; "
                + "если естественнее парная рифма, используй её. Это предложение для редакции, не диагноз исходника. "
                + "Можно менять связанные строки вместе. Неизвестное ударение само по себе не ошибка. "
                + "Номера editable_lines от 1, включая метки и пустые строки. Верни весь фрагмент в lyrics.",
            lyrics = step.Request.Text,
            editable_lines = EditableLines(step).Order().Select(i => i + 1),
            program_candidate = MusicRhymeReply.Identical(step.ProgramDraft, step.Request.Text) ? null : step.ProgramDraft,
            problems = issues.GroupBy(i => i.Kind).Select(g => new { kind = g.Key,
                lines = g.Select(i => Position(step, lines.Single(l => l.Number == i.Line))),
                pairs = g.Where(i => i.Partner is not null).Select(i => new { line = Position(step, lines.Single(l => l.Number == i.Line)),
                    partner = lines.Single(l => l.Number == i.Partner).Text }) }),
            candidates = step.Suggestions.Select(s => new { line = Position(step, lines.Single(l => l.Number == s.Line)), s.Word, s.Reading, s.Synonym }),
            reference = new { books },
            // The fragment already contains its own stanzas; only add context outside the selected scope.
            stanza_context = lines.Where(l => stanzas.Contains(l.Stanza) && (l.Start < step.Request.Start
                || l.Start >= step.Request.Start + step.Request.Length)).Take(32).Select(l => l.Text)
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static string SearchQuery(MusicRhymeStep step)
    {
        var ru = MusicRhymeWork.Issues(step).Count(i => step.Analysis!.Lines.Single(l => l.Number == i.Line).Language == "ru");
        return ru > 0
            ? "Русское стихосложение: рифма от ударной гласной, перекрёстная и парная рифмовка строфы, ритм, сохранение смысла."
            : "English verse: rhyme from the stressed vowel, alternate and paired rhyme in a stanza, rhythm and preservation of meaning.";
    }

    private static int Position(MusicRhymeStep step, MusicRhymeLine line) => 1 + Regex.Matches(
        step.Request.Source.Substring(step.Request.Start, line.Start - step.Request.Start), @"\r\n|\r|\n").Count;
}
