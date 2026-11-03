using System.IO;
using System.Text.Json;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    private static void InitializeTree(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS topic_tree(
              conversation_id TEXT PRIMARY KEY REFERENCES conversation(id) ON DELETE CASCADE,
              enabled INTEGER NOT NULL DEFAULT 0,revision INTEGER NOT NULL DEFAULT 0,document TEXT);
            CREATE TABLE IF NOT EXISTS topic_tree_version(
              conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              revision INTEGER NOT NULL,document TEXT NOT NULL,PRIMARY KEY(conversation_id,revision));
            INSERT OR IGNORE INTO metadata VALUES('tree_schema','1');
            """;
        command.ExecuteNonQuery();
        command.CommandText="SELECT value FROM metadata WHERE key='tree_schema'";
        if((string?)command.ExecuteScalar()!="1")throw new InvalidDataException("Unsupported topic map schema.");
    }
    public LetItOutTreeState Tree(string id)
    {
        Id(id);using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT enabled,document FROM topic_tree WHERE conversation_id=$id";
        command.Parameters.AddWithValue("$id",id);using var row=command.ExecuteReader();
        return !row.Read()?new(false,null):new(row.GetBoolean(0),row.IsDBNull(1)?null:
            JsonSerializer.Deserialize<LetItOutTreeDocument>(row.GetString(1))??throw new InvalidDataException("Invalid tree document."));
    }
    public void SetTreeEnabled(string id,bool enabled)
    {
        Id(id);using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO topic_tree(conversation_id,enabled) VALUES($id,$enabled)
            ON CONFLICT(conversation_id) DO UPDATE SET enabled=excluded.enabled;
            """;
        command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$enabled",enabled);command.ExecuteNonQuery();
    }
    internal bool PublishTree(LetItOutTreeDocument document,int expectedRevision,IReadOnlyList<LetItOutMemoryPair> sources)
    {
        Id(document.ConversationId);
        if(document.Revision!=expectedRevision+1||document.Product!="LOPATA")throw new InvalidDataException("Invalid tree revision.");
        LetItOutTreePolicy.Validate(document.Nodes,document.Through);
        // Immutable source fingerprints are checked independently from the model's citations.
        var actual=MemoryPairs(document.ConversationId).Where(p=>p.Number<=document.Through).ToArray();
        if(actual.Length!=sources.Count||!actual.Select(p=>p.Fingerprint).SequenceEqual(sources.Select(p=>p.Fingerprint)))return false;
        var json=JsonSerializer.Serialize(document);using var connection=Open();using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="INSERT OR IGNORE INTO topic_tree(conversation_id) SELECT id FROM conversation WHERE id=$id";
        command.Parameters.AddWithValue("$id",document.ConversationId);command.ExecuteNonQuery();
        command.CommandText="UPDATE topic_tree SET revision=$next,document=$document WHERE conversation_id=$id AND enabled=1 AND revision=$previous";
        command.Parameters.AddWithValue("$next",document.Revision);command.Parameters.AddWithValue("$document",json);
        command.Parameters.AddWithValue("$previous",expectedRevision);
        if(command.ExecuteNonQuery()!=1)return false;
        command.CommandText="INSERT INTO topic_tree_version(conversation_id,revision,document) VALUES($id,$next,$document)";
        command.ExecuteNonQuery();transaction.Commit();return true;
    }
}
