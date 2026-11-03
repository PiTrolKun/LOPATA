using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

internal sealed record LetItOutRecallHit(string ConversationId, string Title, int Number,
    string Fingerprint, string Excerpt);
internal sealed record LetItOutRecallSource(LetItOutRecallHit Reference, LetItOutMemoryPair Original,
    LetItOutMemorySnapshot? Summary, IReadOnlyList<LetItOutMemoryPair> Later, IReadOnlyList<LetItOutMemoryPair> Nearby);

public sealed partial class LetItOutConversationStore
{
    private static void InitializeRecall(SqliteConnection db)
    {
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS conversation_search USING fts5(
              heading,user_text,answer_text,content='memory_pair',content_rowid='rowid',
              tokenize='unicode61 remove_diacritics 2',prefix='3 4');
            CREATE TRIGGER IF NOT EXISTS recall_insert AFTER INSERT ON memory_pair BEGIN
              INSERT INTO conversation_search(rowid,heading,user_text,answer_text)
              VALUES(new.rowid,new.heading,new.user_text,new.answer_text); END;
            CREATE TRIGGER IF NOT EXISTS recall_delete AFTER DELETE ON memory_pair BEGIN
              INSERT INTO conversation_search(conversation_search,rowid,heading,user_text,answer_text)
              VALUES('delete',old.rowid,old.heading,old.user_text,old.answer_text); END;
            CREATE TRIGGER IF NOT EXISTS recall_heading AFTER UPDATE OF heading ON memory_pair BEGIN
              INSERT INTO conversation_search(conversation_search,rowid,heading,user_text,answer_text)
              VALUES('delete',old.rowid,old.heading,old.user_text,old.answer_text);
              INSERT INTO conversation_search(rowid,heading,user_text,answer_text)
              VALUES(new.rowid,new.heading,new.user_text,new.answer_text); END;
            CREATE TABLE IF NOT EXISTS conversation_recall(
              conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              answer_ordinal INTEGER NOT NULL,source_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              source_number INTEGER NOT NULL,fingerprint TEXT NOT NULL,
              PRIMARY KEY(conversation_id,answer_ordinal,source_id,source_number));
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT value FROM metadata WHERE key='recall_schema'";
        var version = cmd.ExecuteScalar() as string;
        if (version is null)
        {
            cmd.CommandText = "INSERT INTO conversation_search(conversation_search) VALUES('rebuild'); INSERT INTO metadata VALUES('recall_schema','1')";
            cmd.ExecuteNonQuery();
        }
        else if (version != "1") throw new InvalidDataException("Unsupported recall schema.");
        tx.Commit();
    }

    internal bool HasOtherMemories(string current)
    {
        Id(current); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM memory_pair p JOIN conversation c ON c.id=p.conversation_id WHERE c.id<>$id)";
        cmd.Parameters.AddWithValue("$id", current); return (long)cmd.ExecuteScalar()! != 0;
    }

    internal IReadOnlyList<LetItOutRecallHit> SearchMemories(string current, string query, CancellationToken token, string? only = null)
    {
        Id(current); if (only is not null) Id(only);
        if (query.Length > 240) throw new ArgumentOutOfRangeException(nameof(query));
        var terms = Regex.Matches(query.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}]+")
            .Select(m => m.Value).Where(t => t.Length >= 2 && !RecallStopWords.Contains(t)).Take(16).Distinct()
            .Select(t => Regex.IsMatch(t, @"^[а-я]+$") ? (t.Length > 6 ? t[..^2] : t.TrimEnd('ь')) : t).ToArray();
        if (terms.Length == 0) return [];
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.id,c.title,p.number,p.fingerprint,substr(p.user_text,1,550),substr(COALESCE(p.heading,''),1,200)
            FROM conversation_search JOIN memory_pair p ON p.rowid=conversation_search.rowid
            JOIN conversation c ON c.id=p.conversation_id
            WHERE conversation_search MATCH $q AND c.id<>$id AND ($only IS NULL OR c.id=$only)
            ORDER BY bm25(conversation_search,4.0,3.0,0.2),p.number DESC LIMIT 16
            """;
        cmd.Parameters.AddWithValue("$q", string.Join(" OR ", terms.Select(t => "\"" + t + "\"*")));
        cmd.Parameters.AddWithValue("$id", current); cmd.Parameters.AddWithValue("$only", (object?)only ?? DBNull.Value);
        using var cancel = token.Register(cmd.Cancel); token.ThrowIfCancellationRequested();
        using var row = cmd.ExecuteReader(); var hits = new List<LetItOutRecallHit>();
        while (row.Read()) { token.ThrowIfCancellationRequested(); hits.Add(new(row.GetString(0), row.GetString(1), row.GetInt32(2),
            row.GetString(3), row.GetString(5) + "\nUSER: " + row.GetString(4))); }
        return hits;
    }

    private static readonly HashSet<string> RecallStopWords = new(("это как что мне меня тебя тебе тебе уже ещё или они она оно мой моя мои был была были будет если чтобы потому просто "
        + "этот этой того так про при для без вот все всё нет есть the this that with from about have has had what when where your you and are was were not").Split(' '));

    internal LetItOutRecallSource? RecallSource(string current, LetItOutRecallHit hit, string query, CancellationToken token)
    {
        if (current == hit.ConversationId || Load(current) is null || Load(hit.ConversationId) is null) return null;
        var pairs = MemoryPairs(hit.ConversationId);
        var original = pairs.SingleOrDefault(p => p.Number == hit.Number && p.Fingerprint == hit.Fingerprint);
        if (original is null) return null;
        var laterNumbers = SearchMemories(current, query, token, hit.ConversationId)
            .Where(p => p.Number > hit.Number).Select(p => p.Number).ToHashSet();
        var later = pairs.Where(p => laterNumbers.Contains(p.Number)).OrderByDescending(p => p.Number).Take(4)
            .Concat(pairs.Where(p => p.Number > hit.Number).TakeLast(2)).DistinctBy(p => p.Number).OrderBy(p => p.Number).ToArray();
        var nearby = pairs.Where(p => p.Number == hit.Number - 1 || p.Number == hit.Number + 1).ToArray();
        return new(hit, original, MemorySnapshot(hit.ConversationId), later, nearby);
    }

    internal void SaveRecallLinks(string current, int answerOrdinal, IReadOnlyList<LetItOutRecallHit> sources)
    {
        Id(current); using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        foreach (var source in sources.DistinctBy(s => (s.ConversationId, s.Number)))
        {
            Id(source.ConversationId);
            cmd.CommandText = """
                INSERT OR IGNORE INTO conversation_recall SELECT $id,$a,$s,$n,$f
                WHERE EXISTS(SELECT 1 FROM conversation WHERE id=$id)
                AND EXISTS(SELECT 1 FROM memory_pair WHERE conversation_id=$s AND number=$n AND fingerprint=$f)
                """;
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", current); cmd.Parameters.AddWithValue("$a", answerOrdinal);
            cmd.Parameters.AddWithValue("$s", source.ConversationId); cmd.Parameters.AddWithValue("$n", source.Number);
            cmd.Parameters.AddWithValue("$f", source.Fingerprint); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal IReadOnlyList<LetItOutRecallHit> RecallLinks(string current, int answerOrdinal)
    {
        Id(current); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.id,c.title,p.number,p.fingerprint,substr(p.user_text,1,550)
            FROM conversation_recall r JOIN conversation c ON c.id=r.source_id
            JOIN memory_pair p ON p.conversation_id=r.source_id AND p.number=r.source_number AND p.fingerprint=r.fingerprint
            WHERE r.conversation_id=$id AND r.answer_ordinal=$a
            """;
        cmd.Parameters.AddWithValue("$id", current); cmd.Parameters.AddWithValue("$a", answerOrdinal);
        using var row = cmd.ExecuteReader(); var result = new List<LetItOutRecallHit>();
        while (row.Read()) result.Add(new(row.GetString(0),row.GetString(1),row.GetInt32(2),row.GetString(3),row.GetString(4)));
        return result;
    }
}
