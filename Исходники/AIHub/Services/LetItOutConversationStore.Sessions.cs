using System.Globalization;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    public string BeginMemoryRun(string id)
    {
        Id(id); using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", Now());
        command.CommandText = "UPDATE memory_run SET closed_utc=$now,last_pair=(SELECT COALESCE(MAX(number),0) FROM memory_pair WHERE conversation_id=$id) WHERE conversation_id=$id AND closed_utc IS NULL";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE memory_run SET attempted=0 WHERE conversation_id=$id AND closed_utc IS NOT NULL AND attempted=1 AND last_pair>COALESCE((SELECT MAX(through_pair) FROM memory_snapshot WHERE conversation_id=$id),0)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COALESCE(MAX(number),0)+1 FROM memory_pair WHERE conversation_id=$id";
        var first = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        var run = Guid.NewGuid().ToString("N"); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$first", first);
        command.CommandText = "INSERT INTO memory_run(id,conversation_id,first_pair) VALUES($run,$id,$first)";
        command.ExecuteNonQuery(); transaction.Commit(); return run;
    }

    public void EndMemoryRun(string id, string run)
    {
        Id(id); Id(run); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_run SET closed_utc=$now,last_pair=(SELECT COALESCE(MAX(number),0) FROM memory_pair WHERE conversation_id=$id) WHERE id=$run AND conversation_id=$id AND closed_utc IS NULL";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$now", Now()); command.ExecuteNonQuery();
    }

    public LetItOutMemoryRun? PendingMemoryRun()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.conversation_id,r.first_pair,r.last_pair FROM memory_run r
            WHERE r.closed_utc IS NOT NULL AND r.last_pair>0 AND r.attempted=0
            AND r.last_pair>COALESCE((SELECT MAX(through_pair) FROM memory_snapshot s WHERE s.conversation_id=r.conversation_id),0)
            ORDER BY r.closed_utc,r.last_pair LIMIT 1
            """;
        using var row = command.ExecuteReader(); return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetInt32(2), row.GetInt32(3)) : null;
    }

    public LetItOutMemoryRun? MemoryRun(string id, string run)
    {
        Id(id); Id(run); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_pair,last_pair FROM memory_run WHERE id=$run AND conversation_id=$id AND closed_utc IS NOT NULL";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$run", run);
        using var row = command.ExecuteReader(); return row.Read() ? new(run, id, row.GetInt32(0), row.GetInt32(1)) : null;
    }

    public void ClaimMemoryRun(string id, string run)
    {
        Id(id); Id(run); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_run SET attempted=1 WHERE id=$run AND conversation_id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$run", run); command.ExecuteNonQuery();
    }

    public LetItOutMemorySnapshot? MemorySnapshot(string id)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT through_pair,short_text,expanded_text FROM memory_snapshot WHERE conversation_id=$id ORDER BY through_pair DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", id); using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt32(0), row.GetString(1), row.GetString(2)) : null;
    }

    public bool PublishMemorySnapshot(string id, int through, string shortText, string expanded, IReadOnlyList<LetItOutMemoryPair> sources)
    {
        Id(id);
        if (through <= 0 || shortText.Length is < 1 or > 600 || expanded.Length is < 1 or > 6000) throw new InvalidDataException("Invalid memory summary.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = "SELECT number,fingerprint FROM memory_pair WHERE conversation_id=$id AND number<=$n ORDER BY number";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$n", through);
        var actual = new List<(int, string)>(); using (var row = command.ExecuteReader()) while (row.Read()) actual.Add((row.GetInt32(0), row.GetString(1)));
        if (actual.Count != through || !actual.SequenceEqual(sources.Select(p => (p.Number, p.Fingerprint)))) return false;
        command.CommandText = "INSERT OR IGNORE INTO memory_snapshot VALUES($id,$n,$short,$long,$model,$now)";
        command.Parameters.AddWithValue("$short", shortText); command.Parameters.AddWithValue("$long", expanded);
        command.Parameters.AddWithValue("$model", ManagedModelCatalog.LetItOutArtifactId); command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery(); transaction.Commit(); WriteMemoryIndex(id); return true;
    }
}
