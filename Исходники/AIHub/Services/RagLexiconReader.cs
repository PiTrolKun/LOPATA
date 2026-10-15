using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

public sealed record RagLexiconHit(string Word, string Kind, JsonElement Data, bool RequiresContext,
    string Source, string License, string Attribution, string Locator);

/// <summary>Read-only lexical retrieval, independent of model availability and vector-book publication.</summary>
public static class RagLexiconReader
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Verified = new();
    public static bool Exists(string library) => File.Exists(Path.Combine(library, "lexicon", "current.json"));
    public static string Normalize(string text)
    {
        text = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        text = Regex.Replace(text, "\\+(?=[аеёиоуыэюя])|(?<=[аеёиоуыэюя])'", "");
        return Regex.Replace(text.Replace("\u0301", "").Replace("\u0300", "").Replace('ё', 'е'), "\\s+", " ").Trim();
    }
    public static (string Database, JsonElement Manifest) Open(string library, CancellationToken token, bool verifyDatabase = true)
    {
        var root = Path.Combine(library, "lexicon");
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "current.json")));
        var version = pointer.RootElement.GetProperty("version").GetString();
        if (!Guid.TryParseExact(version, "N", out _)) throw new InvalidDataException("RagBooks.InvalidLexicon");
        var folder = Path.Combine(root, "versions", version!); var manifestPath = Path.Combine(folder, "manifest.json");
        if (RagLexiconPackage.Hash(manifestPath, token) != pointer.RootElement.GetProperty("manifest_sha256").GetString())
            throw new InvalidDataException("RagBooks.LexiconChecksum");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)); var manifest = doc.RootElement;
        if (manifest.GetProperty("profile").GetString() != RagLexiconPackage.Profile || manifest.GetProperty("version").GetString() != version)
            throw new InvalidDataException("RagBooks.InvalidLexicon");
        var database = Path.Combine(folder, "lexicon.sqlite");
        if (verifyDatabase)
        {
            var info = new FileInfo(database); var expected = manifest.GetProperty("database_sha256").GetString()!;
            var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{expected}";
            if (!Verified.TryGetValue(database, out var saved) || saved != stamp)
            {
                if (RagLexiconPackage.Hash(database, token) != expected) throw new InvalidDataException("RagBooks.LexiconChecksum");
                Verified[database] = stamp;
            }
        }
        return (database, manifest.Clone());
    }
    public static IReadOnlyList<RagLexiconHit> Search(string library, string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new InvalidDataException("RagBooks.InvalidQuery");
        var (database, _) = Open(library, token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); token.ThrowIfCancellationRequested();
        const string columns = "SELECT e.word,e.kind,e.data,e.requires_context,s.id,s.metadata,e.locator FROM entries e JOIN sources s ON s.id=e.source_id ";
        List<RagLexiconHit> Read(string sql, string argument)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 15;
            command.Parameters.AddWithValue("$query", argument); using var reader = command.ExecuteReader(); var results = new List<RagLexiconHit>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); using var data = JsonDocument.Parse(reader.GetString(2)); using var metadata = JsonDocument.Parse(reader.GetString(5));
                results.Add(new(reader.GetString(0), reader.GetString(1), data.RootElement.Clone(), reader.GetBoolean(3), reader.GetString(4),
                    metadata.RootElement.GetProperty("license").GetString()!, metadata.RootElement.GetProperty("attribution").GetString()!, reader.GetString(6)));
            }
            return results;
        }
        var hits = Read(columns + "JOIN lookup l ON l.entry_id=e.id WHERE l.key=$query ORDER BY CASE s.adapter WHEN 'ruaccent.omographs' THEN 0 WHEN 'ruaccent.yo_homographs' THEN 1 WHEN 'ruaccent.accents' THEN 2 WHEN 'cmudict' THEN 3 WHEN 'openrussian' THEN 4 ELSE 5 END,e.id LIMIT 80", Normalize(query));
        // A context-sensitive stress table must not be obscured by an apparently unambiguous base table.
        if (hits.Any(h => h.Kind == "stress" && h.RequiresContext))
            hits.RemoveAll(h => h.Kind == "stress" && !h.RequiresContext);
        if (hits.Any(h => h.Kind == "yo" && h.RequiresContext))
            hits.RemoveAll(h => h.Kind == "yo" && !h.RequiresContext);
        // Folding е/ё is for retrieval only: e.g. все/всё still have different stress readings.
        if (hits.Where(h => h.Kind == "stress").SelectMany(h => h.Data.GetProperty("variants").EnumerateArray())
            .Select(v => v.GetString()!.Normalize(NormalizationForm.FormC).ToLowerInvariant()).Distinct().Take(2).Count() > 1)
            hits = hits.Select(h => h.Kind == "stress" ? h with { RequiresContext = true } : h).ToList();
        if (hits.Count == 0)
        {
            var words = Regex.Matches(query, @"[\p{L}\p{N}]+") .Select(m => m.Value).Take(12);
            var fts = string.Join(" OR ", words.Select(w => "\"" + w + "\""));
            if (fts.Length > 0) hits = Read(columns + "JOIN search ON search.entry_id=e.id WHERE search MATCH $query ORDER BY search.rank LIMIT 20", fts);
        }
        if (hits.Count(h => h.Kind == "pronunciation") > 1)
            hits = hits.Select(h => h.Kind == "pronunciation" ? h with { RequiresContext = true } : h).ToList();
        return hits;
    }
}
