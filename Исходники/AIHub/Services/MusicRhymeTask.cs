using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Turns selected editorial opportunities into a task, keeping the user's scope intact.</summary>
public static class MusicRhymeTask
{
    public static HashSet<int> ReplyEditableLines(MusicRhymeStep step)
    {
        if (step.Reply.Length == 0 || step.Prompt.Length == 0) return EditableLines(step);
        // A saved reply belongs to the permission set of the task that actually produced it.
        try
        {
            using var prompt = JsonDocument.Parse(step.Prompt);
            if (prompt.RootElement.ValueKind != JsonValueKind.Object) throw new System.IO.InvalidDataException("Music.Rhyme.InvalidReply");
            if (!prompt.RootElement.TryGetProperty("editable_lines",out var allowed)) return EditableLines(step);
            if (allowed.ValueKind != JsonValueKind.Array) throw new System.IO.InvalidDataException("Music.Rhyme.InvalidReply");
            var count=1+Regex.Matches(step.Request.Text,@"\r\n|\r|\n").Count;
            var result=new HashSet<int>();
            foreach(var value in allowed.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1 || number > count)
                    throw new System.IO.InvalidDataException("Music.Rhyme.InvalidReply");
                result.Add(number-1);
            }
            return result;
        }
        catch(JsonException) { throw new System.IO.InvalidDataException("Music.Rhyme.InvalidReply"); }
    }

    public static HashSet<int> EditableLines(MusicRhymeStep step)
    {
        if (!step.Request.Tuning.Enabled("WholeStanzas"))
            return MusicRhymeEditorial.ForScope(step).Fragments.SelectMany(fragment => fragment.Lines)
                .Distinct().Select(number => step.Analysis!.Lines.Single(line => line.Number == number))
                .Where(line => line.Start >= step.Request.Start && line.Start + line.Length <= step.Request.Start + step.Request.Length)
                .Select(line => Position(step, line) - 1).ToHashSet();
        var stanzas = MusicRhymeWork.Issues(step)
            .Select(i => step.Analysis!.Lines.Single(l => l.Number == i.Line).Stanza).ToHashSet();
        return step.Analysis!.Lines.Where(l => stanzas.Contains(l.Stanza)
            && l.Start >= step.Request.Start && l.Start + l.Length <= step.Request.Start + step.Request.Length)
            .Select(l => Position(step, l) - 1).ToHashSet();
    }

    public static string Prompt(MusicRhymeStep step)
    {
        var lines = step.Analysis!.Lines;
        var issues = MusicRhymeWork.Issues(step).ToArray();
        var selection = MusicRhymeEditorial.ForScope(step);
        var localization = new LocalizationService(); localization.Load(step.Request.InterfaceLanguage);
        var stanzas = issues.Select(i => lines.Single(l => l.Number == i.Line).Stanza).ToHashSet();
        using var reference = JsonDocument.Parse(step.Reference);
        var books = reference.RootElement.TryGetProperty("books", out var found) ? found.EnumerateArray().Take(step.Request.Tuning.Integer("RagPromptHits")).Select(h => new {
            source = h.GetProperty("source").GetString(), text = h.GetProperty("text").GetString()![..Math.Min(step.Request.Tuning.Integer("RagPromptChars"), h.GetProperty("text").GetString()!.Length)]
        }).ToArray() : [];
        return JsonSerializer.Serialize(new {
            task = "Предложи осмысленную редакторскую альтернативу с сохранением смысла и сцены. "
                + "editorial_targets объясняют выбор мест: это могут быть несовпадение, неопределённость или добровольная вариация удачного текста. "
                + "Не навязывай неподтверждённую схему рифмы, не считай повтор и неизвестное ударение ошибками. "
                + "Для variation попробуй другой образ или оборот с сохранением имеющихся созвучий. "
                + "Сохраняй удачные пары preserve_rhyme_pairs; можно менять связанные строки вместе. "
                + "Номера editable_lines от 1, включая метки и пустые строки. Верни весь фрагмент в lyrics.",
            lyrics = step.Request.Text,
            editable_lines = EditableLines(step).Order().Select(i => i + 1),
            program_candidate = MusicRhymeReply.Identical(step.ProgramDraft, step.Request.Text) ? null : step.ProgramDraft,
            editorial_targets = selection.Fragments.Select(f => new { kind = f.Kind,
                lines = f.Lines.Select(n => Position(step, lines.Single(l => l.Number == n))),
                reason = MusicRhymeEditorial.Explain(f, localization.T) }),
            preserve_rhyme_pairs = selection.PreserveRhymePairs.Select(pair => pair.Select(n => lines.Single(l => l.Number == n))
                .Select(l => new { text = l.Text, ending = l.Ending })),
            candidates = step.Suggestions.Take(step.Request.Tuning.Integer("PromptCandidates")).Select(s => new { line = Position(step, lines.Single(l => l.Number == s.Line)),
                s.Word, s.Reading, s.Synonym, s.Relation, s.MeaningScore, s.SoundScore, s.GrammarKnown }),
            reference = new { books },
            // The fragment already contains its own stanzas; only add context outside the selected scope.
            stanza_context = lines.Where(l => stanzas.Contains(l.Stanza) && (l.Start < step.Request.Start
                || l.Start >= step.Request.Start + step.Request.Length)).Take(step.Request.Tuning.Integer("StanzaContextLines")).Select(l => l.Text)
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
