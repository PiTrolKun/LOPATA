using System.Text.Json;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

/// <summary>Exact lexical retrieval only: an unknown ending is never replaced by an FTS neighbour.</summary>
public sealed class MusicRhymeLexicon : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, IReadOnlyList<RagLexiconHit>> _cache = new(StringComparer.Ordinal);
    public string Database { get; }
    public string Hash { get; }
    public int Records { get; }
    public MusicRhymeLexicon(string library, CancellationToken token)
    {
        var (database, manifest) = RagLexiconReader.Open(library, token);
        Database = database; Hash = manifest.GetProperty("database_sha256").GetString()!;
        Records = manifest.GetProperty("records").GetInt32();
        _connection = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        _connection.Open();
    }
    public IReadOnlyList<RagLexiconHit> Lookup(string word)
    {
        var key = RagLexiconReader.Normalize(word);
        if (_cache.TryGetValue(key, out var saved)) return saved;
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.word,e.kind,e.data,e.requires_context,s.id,s.metadata,e.locator
            FROM entries e JOIN sources s ON s.id=e.source_id JOIN lookup l ON l.entry_id=e.id
            WHERE l.key=$key ORDER BY CASE s.adapter WHEN 'ruaccent.omographs' THEN 0
            WHEN 'ruaccent.yo_homographs' THEN 1 WHEN 'ruaccent.accents' THEN 2
            WHEN 'cmudict' THEN 3 WHEN 'openrussian' THEN 4 ELSE 5 END,e.id LIMIT 80
            """;
        command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader();
        var hits = new List<RagLexiconHit>();
        while (reader.Read())
        {
            using var data = JsonDocument.Parse(reader.GetString(2)); using var metadata = JsonDocument.Parse(reader.GetString(5));
            hits.Add(new(reader.GetString(0), reader.GetString(1), data.RootElement.Clone(), reader.GetBoolean(3), reader.GetString(4),
                metadata.RootElement.GetProperty("license").GetString()!, metadata.RootElement.GetProperty("attribution").GetString()!, reader.GetString(6)));
        }
        if (hits.Any(h => h.Kind == "stress" && h.RequiresContext)) hits.RemoveAll(h => h.Kind == "stress" && !h.RequiresContext);
        _cache[key] = hits; return hits;
    }
    public string[] Synonyms(string word) => Lookup(word).Where(h => h.Kind == "meaning" && h.Data.TryGetProperty("synonyms", out _))
        .SelectMany(h => h.Data.GetProperty("synonyms").EnumerateArray()).Select(s => s.GetString() ?? "")
        .Where(s => s.Length is > 0 and < 80 && !s.Contains(' ')).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
    public string Reference(IEnumerable<string> words)
    {
        var items = words.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select(word => new {
            word, entries = Lookup(word).Where(h => h.Kind is "meaning" or "forms").Take(2).Select(h => new {
                h.Source, h.License, h.Locator, synonyms = Synonyms(word).Take(8),
                definitions = h.Data.TryGetProperty("senses", out var senses) ? senses.EnumerateArray().Take(2)
                    .SelectMany(s => s.GetProperty("glosses").EnumerateArray()).Take(2).Select(s => s.GetString()).ToArray() : []
            }).ToArray()
        }).ToArray();
        return JsonSerializer.Serialize(items);
    }
    public void Dispose() => _connection.Dispose();
}
