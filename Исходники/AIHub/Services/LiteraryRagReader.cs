using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Read-only project capability. Model arguments never become filesystem paths.</summary>
public sealed class LiteraryRagReader(LiteraryEditorSnapshot snapshot)
{
    private readonly LiteraryProjectLayout _layout = new(snapshot.Directory);
    private sealed record Section(string Source, string Name, string Text);
    private List<Section> References()
    {
        _layout.EnsurePresent();
        var file = Path.Combine(_layout.Rag, "Source", "text.json");
        if (!File.Exists(file)) return [];
        var hashesPath = Path.Combine(_layout.Rag, "Source", "sources.json");
        using (var hashes = JsonDocument.Parse(LiteraryChapterFiles.Read(hashesPath)))
        {
            var i = 0;
            foreach (var source in hashes.RootElement.EnumerateArray())
            {
                var name = source.GetProperty("file").GetString()!;
                if (Path.GetFileName(name) != name) throw new InvalidDataException("Unsafe source name.");
                using var original = File.OpenRead(Path.Combine(_layout.Root, "Materials", $"{++i:D4}_" + name));
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)) != source.GetProperty("sha256").GetString())
                    throw new InvalidDataException("Reference file changed; rebuild its index before reading.");
            }
        }
        using var data = JsonDocument.Parse(LiteraryChapterFiles.Read(file));
        return data.RootElement.EnumerateArray().Select(s => new Section(s.GetProperty("source").GetString()!,
            s.GetProperty("section").GetString()!, s.GetProperty("text").GetString()!)).ToList();
    }
    public object Catalog()
    {
        var sections = References();
        return new { kind = "reference_catalog", totalSections = sections.Count,
            sections = sections.Take(12).Select((s, i) => new { number = "ref:" + i, source = s.Source, section = s.Name }),
            nextOffset = sections.Count > 12 ? (int?)12 : null };
    }
    public async Task<LiteraryReadResult> ExecuteAsync(LiteraryReadAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); _layout.EnsurePresent();
        object data;
        try
        {
            data = action.Action switch
            {
                "list_reference" => List(action.Offset),
                "read_reference" => Read(action.Number, action.Offset),
                "search_reference" => await SearchReferenceAsync(action.Query, action.Offset, ct),
                "semantic_reference" or "semantic_project" => await SearchAsync(action.Query, action.Action == "semantic_reference", ct),
                _ => new { error = "Unknown RAG operation." }
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or LiteraryEmbeddingException)
        { data = new { error = "RAG unavailable: " + ex.Message, action.Action }; }
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(data))!.AsObject();
        json["projectId"] = snapshot.ProjectId;
        var envelope = json.ToJsonString();
        return new(JsonSerializer.Serialize(action), envelope);
    }
    private object List(int offset)
    {
        var sections = References();
        return new { kind = "reference_catalog", total = sections.Count, offset,
            sections = sections.Skip(offset).Take(12).Select((s, i) => new { number = "ref:" + (offset + i), source = s.Source, section = s.Name }),
            nextOffset = offset + 12 < sections.Count ? (int?)(offset + 12) : null };
    }
    private object Read(string number, int offset)
    {
        var sections = References();
        if (!number.StartsWith("ref:") || !int.TryParse(number[4..], out var i) || i < 0 || i >= sections.Count)
            throw new InvalidDataException("Unknown reference section.");
        var section = sections[i];
        if (offset < 0 || offset > section.Text.Length) throw new InvalidDataException("Reference offset is outside its text.");
        if (offset > 0 && offset < section.Text.Length && char.IsLowSurrogate(section.Text[offset])) offset--;
        var end = Math.Min(section.Text.Length, offset + LiteraryProjectReader.FragmentCharacters);
        if (end > offset && char.IsHighSurrogate(section.Text[end - 1])) end--;
        return new { kind = "reference", number, source = section.Source, section = section.Name,
            revision = LiteraryWorkIndex.Revision(section.Text), offset, totalCharacters = section.Text.Length,
            nextOffset = end < section.Text.Length ? (int?)end : null, text = section.Text[offset..end] };
    }
    private object SearchWords(string query, int offset)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidDataException("Empty query.");
        var sections = References(); var hits = new List<object>();
        var i = offset;
        for (; i < sections.Count && i - offset < 32 && hits.Count < 5; i++)
        {
            var at = sections[i].Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) hits.Add(Read("ref:" + i, Math.Max(0, at - 180)));
        }
        return new { kind = "reference_search", query, hits, nextOffset = i < sections.Count ? (int?)i : null };
    }
    private async Task<object> SearchReferenceAsync(string query, int offset, CancellationToken ct)
    {
        var exact = SearchWords(query, offset);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(exact));
        var count = json.RootElement.GetProperty("hits").GetArrayLength();
        if (count > 0 && !query.Any(char.IsWhiteSpace)) return exact;
        // A small model often puts a question, not a literal quotation, in a lexical search.
        // Empty lexical matches are not evidence of absence; resolve the same request semantically.
        return new { kind = "reference_hybrid_search", query, exactMatches = count,
            note = "A multiword query is also resolved semantically. Matching phrases alone do not identify all events or prove absence of a fact.",
            semantic = await SearchAsync(query, true, ct) };
    }
    private async Task<object> SearchAsync(string query, bool reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 120) throw new InvalidDataException("Invalid query.");
        var collections = new List<(string Id, LiterarySource? Source)>();
        var sections = References();
        if (reference)
        {
            var path = Path.Combine(_layout.Rag, "Source", "manifest.json");
            if (!File.Exists(path)) return new { error = "Reference index is not ready; use direct reading." };
            var manifest = JsonSerializer.Deserialize<LiterarySourceIndex.Manifest>(LiteraryChapterFiles.Read(path))!;
            if (manifest.ModelRevision != GigaEmbeddingInstallation.Revision || manifest.Kind != "reference") throw new InvalidDataException("Reference index is incompatible.");
            collections.Add((manifest.Id, null));
        }
        else foreach (var source in snapshot.Sources.Where(s => s.Id != snapshot.ActiveId))
        {
            var text = LiteraryChapterFiles.Read(Path.Combine(_layout.Root, "chapters", source.FileName));
            if (LiteraryWorkIndex.Current(_layout, source, text) is { } manifest) collections.Add((manifest.Collection, source));
        }
        if (collections.Count == 0) return new { error = "No current indexed parts. Use list/read/search for saved files." };
        var cache = _layout.EnsureFolder(Path.Combine("Rag", "Queries", LiteraryWorkIndex.Revision(GigaEmbeddingInstallation.Revision + query)));
        var input = Path.Combine(cache, "query.json"); var output = Path.Combine(cache, "vector.jsonl");
        if (!File.Exists(output + ".ready"))
        {
            LiteraryChapterFiles.Write(input, JsonSerializer.Serialize(new[] { new { text = query } }));
            await new GigaSourceEmbedding("cpu", query: true).EmbedAsync(input, output, new InlineProgress<LiteraryPreparationProgress>(_ => { }), ct);
            File.WriteAllText(output + ".ready", GigaEmbeddingInstallation.Revision);
        }
        using var vectorFile = JsonDocument.Parse(File.ReadLines(output).First());
        LiterarySourceIndex.ValidatePoint(vectorFile.RootElement);
        var vector = vectorFile.RootElement.GetProperty("vector").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var matches = new List<(double Score, string Number, object Data)>();
        var runtime = _layout.CreateRuntime();
        try
        {
            foreach (var collection in collections)
            {
                _layout.EnsurePresent();
                foreach (var hit in await runtime.SearchLiteraryAsync(collection.Id, vector, ct))
                {
                    var payload = hit.GetProperty("payload"); var text = payload.GetProperty("text").GetString()!;
                    var sourceId = payload.GetProperty("source").GetString(); var sectionId = payload.GetProperty("section").GetString();
                    string whole, number, kind;
                    if (reference)
                    {
                        var i = sections.FindIndex(s => s.Source == sourceId && s.Name == sectionId);
                        if (i < 0 || payload.GetProperty("kind").GetString() != "reference") throw new InvalidDataException("Foreign reference hit.");
                        whole = sections[i].Text; number = "ref:" + i; kind = "reference";
                    }
                    else
                    {
                        var source = collection.Source!;
                        if (sourceId != source.Id || payload.GetProperty("kind").GetString() != "project") throw new InvalidDataException("Foreign project hit.");
                        whole = LiteraryChapterFiles.Read(Path.Combine(_layout.Root, "chapters", source.FileName));
                        if (LiteraryWorkIndex.Current(_layout, source, whole)?.Collection != collection.Id) throw new InvalidDataException("Stale project hit.");
                        number = source.Number; kind = "project_history";
                    }
                    var offset = CodePointOffset(whole, payload.GetProperty("offset").GetInt32());
                    if (offset + text.Length > whole.Length || whole.Substring(offset, text.Length) != text) throw new InvalidDataException("RAG fragment does not match its source.");
                    var excerpt = Compact(text, query);
                    offset += excerpt.Offset; text = excerpt.Text;
                    matches.Add((hit.GetProperty("score").GetDouble(), number, new { kind, number, source = sourceId, section = sectionId,
                        revision = LiteraryWorkIndex.Revision(whole), offset, text, totalCharacters = whole.Length,
                        nextOffset = offset + text.Length < whole.Length ? (int?)(offset + text.Length) : null }));
                }
            }
        }
        finally { await runtime.StopAsync(); }
        var ranked = matches.OrderByDescending(m => m.Score).ToArray();
        // Nearby chunks from one scene must not hide later events in other sections.
        var diverse = ranked.DistinctBy(m => m.Number).Take(6).ToList();
        foreach (var hit in ranked)
            if (diverse.Count < 6 && !diverse.Contains(hit)) diverse.Add(hit);
        return new { kind = reference ? "reference_search" : "project_search", query,
            matches = diverse.OrderByDescending(m => m.Score).Select(m => new { score = m.Score, fragment = m.Data }) };
    }
    private static (int Offset, string Text) Compact(string text, string query)
    {
        const int limit = 900;
        if (text.Length <= limit) return (0, text);
        var words = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{N}]{3,}")
            .Select(m => m.Value[..Math.Min(4, m.Value.Length)]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var best = 0; var bestScore = -1;
        foreach (var word in words)
        {
            for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var start = Math.Clamp(at - 300, 0, text.Length - limit);
                var candidate = text.Substring(start, limit);
                var score = words.Count(w => candidate.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (score > bestScore) { bestScore = score; best = start; }
            }
        }
        if (best > 0 && char.IsLowSurrogate(text[best])) best--;
        var end = Math.Min(text.Length, best + limit);
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return (best, text[best..end]);
    }
    private static int CodePointOffset(string text, int count)
    {
        var offset = 0;
        foreach (var rune in text.EnumerateRunes()) { if (count-- == 0) return offset; offset += rune.Utf16SequenceLength; }
        if (count > 0) throw new InvalidDataException("Invalid source offset.");
        return offset;
    }
}
