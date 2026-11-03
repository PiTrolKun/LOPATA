using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    private static void InitializeMemory(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_pair(
              conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              number INTEGER NOT NULL,user_ordinal INTEGER NOT NULL,answer_ordinal INTEGER NOT NULL,
              user_text TEXT NOT NULL,answer_text TEXT NOT NULL,user_utc TEXT,answer_utc TEXT,
              fingerprint TEXT NOT NULL,heading TEXT,attempted INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(conversation_id,number),UNIQUE(conversation_id,answer_ordinal));
            CREATE TABLE IF NOT EXISTS memory_run(
              id TEXT PRIMARY KEY,conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              first_pair INTEGER NOT NULL,last_pair INTEGER,closed_utc TEXT,attempted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS memory_snapshot(
              conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              through_pair INTEGER NOT NULL,short_text TEXT NOT NULL,expanded_text TEXT NOT NULL,
              model TEXT NOT NULL,created_utc TEXT NOT NULL,PRIMARY KEY(conversation_id,through_pair));
            CREATE TABLE IF NOT EXISTS context_cursor(
              conversation_id TEXT PRIMARY KEY REFERENCES conversation(id) ON DELETE CASCADE,
              through_pair INTEGER NOT NULL);
            CREATE TRIGGER IF NOT EXISTS immutable_memory_source BEFORE UPDATE OF
              conversation_id,number,user_ordinal,answer_ordinal,user_text,answer_text,user_utc,answer_utc,fingerprint
              ON memory_pair BEGIN SELECT RAISE(ABORT,'Original chat pairs are immutable'); END;
            INSERT OR IGNORE INTO metadata VALUES('memory_schema','1');
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM metadata WHERE key='memory_schema'";
        if ((string?)command.ExecuteScalar() != "1") throw new InvalidDataException("Unsupported chat memory schema.");
    }

    private static string Fingerprint(string conversation, int ordinal, string user, string answer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { conversation, ordinal, user, answer }))));

    private static void CapturePairs(SqliteConnection connection, SqliteTransaction transaction,
        string id, IReadOnlyList<LetItOutSavedMessage> messages)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM conversation WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if ((long)command.ExecuteScalar()! != 1) throw new InvalidDataException("Conversation was removed.");
        command.CommandText = "SELECT answer_ordinal,fingerprint FROM memory_pair WHERE conversation_id=$id";
        var known = new Dictionary<int, string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) known.Add(reader.GetInt32(0), reader.GetString(1));
        command.CommandText = "SELECT COALESCE(MAX(number),0) FROM memory_pair WHERE conversation_id=$id";
        var number = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        for (var i = 1; i < messages.Count; i++)
        {
            if (messages[i].Role != "assistant" || !messages[i].Complete || messages[i - 1].Role != "user") continue;
            var source = messages[i - 1];
            var fingerprintText = source.Content + (source.Attachments is { Count: > 0 } ? "\nATTACHMENTS:" + JsonSerializer.Serialize(source.Attachments) : "");
            var hash = Fingerprint(id, i, fingerprintText, messages[i].Content);
            if (known.TryGetValue(i, out var previous))
            {
                if (hash != previous) throw new InvalidDataException("A recorded chat pair changed.");
                continue;
            }
            // Do not allow an earlier snapshot to renumber or insert into recorded history.
            if (known.Keys.Any(k => k > i)) throw new InvalidDataException("Recorded chat order changed.");
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO memory_pair(conversation_id,number,user_ordinal,answer_ordinal,user_text,answer_text,user_utc,answer_utc,fingerprint) VALUES($id,$n,$u,$a,$ut,$at,$uc,$ac,$hash)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$n", ++number);
            command.Parameters.AddWithValue("$u", i - 1); command.Parameters.AddWithValue("$a", i);
            command.Parameters.AddWithValue("$ut", messages[i - 1].Content); command.Parameters.AddWithValue("$at", messages[i].Content);
            command.Parameters.AddWithValue("$uc", (object?)messages[i - 1].RecordedUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$ac", (object?)messages[i].RecordedUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", hash); command.ExecuteNonQuery();
            known[i] = hash;
        }
        if (known.Keys.Any(i => i >= messages.Count || messages[i].Role != "assistant" || !messages[i].Complete
            || i == 0 || messages[i - 1].Role != "user"))
            throw new InvalidDataException("A recorded chat pair disappeared.");
    }

    private static IReadOnlyList<LetItOutSavedMessage> PreserveMessageClocks(SqliteConnection connection,
        SqliteTransaction transaction, string id, IReadOnlyList<LetItOutSavedMessage> messages)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT messages FROM conversation WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        var json = command.ExecuteScalar() as string;
        var previous = json is null ? [] : JsonSerializer.Deserialize<LetItOutSavedMessage[]>(json) ?? [];
        return messages.Select((m, i) => m.RecordedUtc is null && i < previous.Length && previous[i].Role == m.Role
            && (previous[i].Content == m.Content || m.Role == "assistant" && !previous[i].Complete)
            ? m with { RecordedUtc = previous[i].RecordedUtc } : m).ToArray();
    }

    public IReadOnlyList<LetItOutMemoryPair> MemoryPairs(string id)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT number,user_ordinal,answer_ordinal,user_text,answer_text,user_utc,answer_utc,fingerprint,heading,attempted FROM memory_pair WHERE conversation_id=$id ORDER BY number";
        command.Parameters.AddWithValue("$id", id); using var row = command.ExecuteReader(); var result = new List<LetItOutMemoryPair>();
        while (row.Read()) result.Add(new(row.GetInt32(0), row.GetInt32(1), row.GetInt32(2), row.GetString(3), row.GetString(4),
            row.IsDBNull(5) ? null : DateTimeOffset.Parse(row.GetString(5), CultureInfo.InvariantCulture),
            row.IsDBNull(6) ? null : DateTimeOffset.Parse(row.GetString(6), CultureInfo.InvariantCulture),
            row.GetString(7), row.IsDBNull(8) ? null : row.GetString(8), row.GetBoolean(9)));
        var saved = Load(id);
        return result.Select(pair => pair with { Attachments = saved is null ? []
            : (saved.Messages[pair.UserOrdinal].Attachments ?? []).Select(a => Attachment(id, a)).ToArray() }).ToArray();
    }

    public bool TryClaimPair(string id, int number) => MemoryUpdate(
        "UPDATE memory_pair SET attempted=1 WHERE conversation_id=$id AND number=$n AND attempted=0", id, number) == 1;

    public bool SetPairHeading(string id, LetItOutMemoryPair pair, string heading)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_pair SET heading=$text WHERE conversation_id=$id AND number=$n AND fingerprint=$hash AND attempted=1";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$n", pair.Number);
        command.Parameters.AddWithValue("$hash", pair.Fingerprint); command.Parameters.AddWithValue("$text", heading);
        return command.ExecuteNonQuery() == 1;
    }

    private int MemoryUpdate(string sql, string id, int number)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = sql; command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$n", number);
        return command.ExecuteNonQuery();
    }
}
