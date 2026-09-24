using System.Text.RegularExpressions;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportPreviewPart(string Id, string[] UnitIds, string ConversationId, string Conversation,
    string Variant, string Excerpt, long Characters);
public sealed record ImportPreviewVariant(string Name, IReadOnlyList<ImportPreviewPart> Parts, long Characters);
public sealed record ImportPreviewGroup(string Key, string Name, bool IsOther,
    IReadOnlyList<ImportPreviewVariant> Variants, long Characters);

/// <summary>Conservative, model-free title hints. Every selected source unit remains in exactly one group.</summary>
public static partial class ImportQuickPreview
{
    [GeneratedRegex(@"(?im)^\s*(?:название(?:\s+(?:книги|произведения|проекта))?|книга|роман|произведение|история|повесть)\s*[:—–-]\s*[«\""']?([^»\""'\r\n]{4,100})")]
    private static partial Regex TitleDeclaration();

    [GeneratedRegex("«([^»\\r\\n]{8,100})»")]
    private static partial Regex QuotedTitle();

    public static IReadOnlyList<ImportPreviewGroup> Scan(ImportInput input, IEnumerable<string> selectedDialogs,
        string workTitle, CancellationToken token = default)
    {
        var selected = selectedDialogs.ToHashSet(StringComparer.Ordinal);
        var conversations = input.Conversations.Where(c => selected.Contains(c.Id)).ToDictionary(c => c.Id);
        var units = input.Units.Where(u => selected.Contains(u.Conversation) && !u.Technical).ToArray();
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        void Add(string title, bool preferred = false)
        {
            title = CleanTitle(title);
            if (title.Length is < 4 or > 100 || candidates.Count >= 100) return;
            var key = ImportWorkNames.Key(title);
            if (key.Length < 4 || key.StartsWith("глава ", StringComparison.Ordinal)
                || key.StartsWith("часть ", StringComparison.Ordinal)) return;
            if (candidates.TryGetValue(key, out var existing))
            {
                existing.Hits++;
                if (preferred) existing.Preferred = true;
            }
            else candidates[key] = new Candidate(title, key, preferred);
        }
        Add(workTitle, true);
        foreach (var dialog in conversations.Values)
            if (ImportWorkNames.MatchesDialogTitle(workTitle, dialog.Title)
                || LooksLikeWorkTitle(dialog.Title)) Add(dialog.Title);
        foreach (var unit in units)
        {
            token.ThrowIfCancellationRequested();
            var beginning = unit.Text[..Math.Min(unit.Text.Length, 2400)];
            foreach (Match match in TitleDeclaration().Matches(beginning))
                Add(match.Groups[1].Value);
            foreach (Match match in QuotedTitle().Matches(beginning))
                if (LooksLikeWorkTitle(match.Groups[1].Value)) Add(match.Groups[1].Value);
        }

        var ordered = candidates.Values.OrderByDescending(c => c.Preferred).ThenByDescending(c => c.Hits).ToArray();
        var groups = new List<GroupBuilder>();
        foreach (var candidate in ordered)
        {
            var group = groups.FirstOrDefault(g => SameWork(g.Canonical.Key, candidate.Key));
            if (group is null) groups.Add(new GroupBuilder(candidate));
            else group.Aliases.Add(candidate);
        }
        var other = new GroupBuilder(new Candidate("", "__other__", false));
        foreach (var conversation in input.Conversations.Where(c => selected.Contains(c.Id)))
        {
            GroupBuilder? current = null;
            Candidate? currentAlias = null;
            foreach (var unit in units.Where(u => u.Conversation == conversation.Id))
            {
                token.ThrowIfCancellationRequested();
                var normalized = ImportWorkNames.Key(unit.Text);
                var matches = groups.SelectMany(g => g.Aliases.Select(a => (Group: g, Alias: a)))
                    .Where(pair => normalized.Contains(pair.Alias.Key, StringComparison.Ordinal)).ToArray();
                var distinct = matches.Select(m => m.Group).Distinct().ToArray();
                GroupBuilder target;
                string variant;
                if (distinct.Length == 1)
                {
                    current = target = distinct[0];
                    currentAlias = matches.Where(m => m.Group == target).OrderByDescending(m => m.Alias.Key.Length).First().Alias;
                    variant = currentAlias.Title;
                }
                else if (distinct.Length > 1)
                {
                    current = null; currentAlias = null; target = other; variant = conversation.Title;
                }
                else if (current is not null && currentAlias is not null && unit.Text.Trim().Length >= 80)
                {
                    target = current; variant = currentAlias.Title;
                }
                else { target = other; variant = conversation.Title; }
                target.Add(unit, conversation.Id, conversation.Title, variant);
            }
        }
        return groups.Where(g => g.Characters > 0)
            .OrderByDescending(g => g.Characters).ThenBy(g => g.Canonical.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Build(false)).Append(other.Build(true)).ToArray();
    }

    private static string CleanTitle(string title)
    {
        title = title.Trim().Trim('«', '»', '\'', '"', '*', '#', ':', '.', ' ', '—', '–');
        return title.Length == 0 ? title : char.ToUpper(title[0]) + title[1..];
    }

    private static bool LooksLikeWorkTitle(string title) =>
        Regex.IsMatch(title, @"(?i)^\s*(?:книга|роман|повесть|сказка|легенда)\b") && title.Length <= 100;

    private static bool SameWork(string left, string right)
    {
        if (ImportWorkNames.Similar(left, right)) return true;
        var a = left.Split(' '); var b = right.Split(' ');
        if (a.Length < 4 || a.Length != b.Length) return false;
        var different = a.Zip(b).Where(p => p.First != p.Second).ToArray();
        return different.Length == 1 && different[0].First.Length >= 7 && different[0].Second.Length >= 7
            && (different[0].First.StartsWith(different[0].Second[..5], StringComparison.Ordinal)
                || different[0].Second.StartsWith(different[0].First[..5], StringComparison.Ordinal));
    }

    private sealed class Candidate(string title, string key, bool preferred)
    {
        public string Title { get; } = title;
        public string Key { get; } = key;
        public bool Preferred { get; set; } = preferred;
        public int Hits { get; set; } = 1;
    }

    private sealed class GroupBuilder(Candidate canonical)
    {
        public Candidate Canonical { get; } = canonical;
        public List<Candidate> Aliases { get; } = [canonical];
        private readonly List<ImportPreviewPart> _parts = [];
        public long Characters => _parts.Sum(p => p.Characters);

        public void Add(ImportUnit unit, string conversationId, string conversation, string variant)
        {
            var previous = _parts.LastOrDefault();
            if (previous is not null && previous.ConversationId == conversationId && previous.Variant == variant
                && previous.UnitIds.Length < 12 && previous.Characters + unit.Text.Length <= 12000
                && previous.Id.StartsWith(unit.Message + ":", StringComparison.Ordinal))
            {
                _parts[^1] = previous with { UnitIds = [.. previous.UnitIds, unit.Id],
                    Characters = previous.Characters + unit.Text.Length };
                return;
            }
            var excerpt = Regex.Replace(unit.Text.Trim(), @"\s+", " ");
            if (excerpt.Length > 220) excerpt = excerpt[..220] + "…";
            _parts.Add(new(unit.Message + ":" + unit.Id, [unit.Id], conversationId, conversation, variant,
                excerpt, unit.Text.Length));
        }

        public ImportPreviewGroup Build(bool other)
        {
            var variants = _parts.GroupBy(p => p.Variant, StringComparer.Ordinal)
                .Select(g => new ImportPreviewVariant(g.Key, g.ToArray(), g.Sum(p => p.Characters)))
                .OrderByDescending(v => v.Characters).ToArray();
            return new(other ? "__other__" : Canonical.Key, Canonical.Title, other, variants, Characters);
        }
    }
}
