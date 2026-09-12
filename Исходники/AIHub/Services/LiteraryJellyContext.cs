using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>A bounded immutable selection; old revisions remain on disk, not in inference history.</summary>
public sealed class LiteraryJellyContext
{
    private readonly LiteraryJellyEntry[] _ranked;
    private readonly int _stale;
    private readonly Action<string, object> _log;
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public LiteraryJellyContext(LiteraryProjectLayout layout, LiteraryEditorSnapshot editor, string query, Action<string, object> log)
    {
        _log = log;
        var entries = new LiteraryJellyStore(layout).Read();
        var revisions = new Dictionary<string, string>();
        foreach (var part in editor.Sources.Where(s => s.Id != editor.ActiveId && entries.Any(e => e.PartId == s.Id)))
            revisions[part.Id] = LiteraryWorkIndex.Revision(LiteraryChapterFiles.Read(Path.Combine(layout.Root, "chapters", part.FileName)));
        var valid = entries.Where(e => revisions.TryGetValue(e.PartId, out var revision) && e.Revision == revision).ToArray();
        _stale = entries.Count - valid.Length;
        var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value.Length > 5 ? m.Value[..5] : m.Value).Distinct().ToArray();
        _ranked = valid.OrderByDescending(e => words.Count(w => (e.Fact.Subject + " " + e.Fact.Relation + " " + e.Fact.Value).Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    public string Build(int budget)
    {
        var selected = new List<object>();
        foreach (var entry in _ranked)
        {
            var item = new { id = entry.Id, part = entry.Number, sourceRevision = entry.Revision, version = entry.Version,
                subject = entry.Fact.Subject, relation = entry.Fact.Relation, value = entry.Fact.Value, kind = entry.Fact.Kind,
                userEdited = entry.Fact.Edited,
                sourceExcerpt = entry.Fact.Edited ? null : entry.Fact.Evidence[..Math.Min(entry.Fact.Evidence.Length, 500)] };
            selected.Add(item);
            if (JsonSerializer.Serialize(selected, Json).Length > budget || selected.Count > 12) selected.RemoveAt(selected.Count - 1);
        }
        var packet = new { kind = "confirmed_project_memory", facts = selected, omitted = _ranked.Length - selected.Count, stale = _stale,
            note = "Only confirmed PROJECT facts are listed. Current draft is preliminary and may describe later events. Sources are numbered in story order; this is not original-book canon. Omitted facts remain on disk; this selection is not the complete memory." };
        _log("jelly_context", packet);
        return JsonSerializer.Serialize(packet, Json);
    }
}
