using System.Text.RegularExpressions;

namespace AIHub.Services.LiteraryImport;

public static class ImportReviewRules
{
    // Conservative flags only: no prose is deleted or rewritten by these rules.
    public static ImportDecision[] Apply(ImportInput input, ImportDecision[] first, ImportDecision[] assembled)
    {
        var units = input.Units.ToDictionary(u => u.Id);
        var previous = first.ToDictionary(d => d.Id);
        return assembled.Select(d =>
        {
            if (d.Kind != "MAIN") return d;
            var unit = units[d.Id];
            var reason = unit.Type == "REQUEST"
                ? "Реплика автора из чата: проверьте, что это текст книги / Author chat message: verify it belongs to the book."
                : previous.TryGetValue(d.Id, out var before) && before.Kind != "KEEP"
                    ? "Проходы расходятся в назначении фрагмента / Passes disagree about this fragment. " + before.Reason
                    : ChatFraming.IsMatch(unit.Text.TrimStart())
                        ? "Возможное пояснение модели вне книги / Possible model commentary outside the book."
                        : null;
            return reason is null ? d : d with { Kind = "DOUBT", Reason = reason };
        }).ToArray();
    }
    private static readonly Regex ChatFraming = new(
        @"^[#*\s]*(?:(?:хорошо|отлично|понял|конечно|да)[,.!:\s]+(?:мы|давай|теперь|я\s+(?:понял|напишу|перепишу))\b|теперь у нас\b|это идеальное попадание\b|если (?:ты )?хочешь\b|вот (?:исправленн|переработанн|новая версия)|(?:sure|certainly)[,!:]\s|here(?:'s| is) (?:the|your) (?:revised|updated)|if you (?:want|would like)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
