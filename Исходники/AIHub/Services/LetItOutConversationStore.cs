using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

/// <summary>Scenario-local history. Body writes never overwrite title/favorite metadata.</summary>
public sealed partial class LetItOutConversationStore
{
    public string DatabasePath { get; }
    public LetItOutConversationStore(string? path = null)
    {
        DatabasePath = Path.GetFullPath(path ?? Path.Combine(AppDataPaths.BaseDirectory, "LetItOut", "conversations.db"));
        Lopata.Updates.SafeUpdatePath.RejectLinks(DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open(true);
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        var schema = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schema > 1) throw new InvalidDataException("Unsupported conversation history schema.");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation (
              id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', manual_title INTEGER NOT NULL DEFAULT 0,
              favorite INTEGER NOT NULL DEFAULT 0, title_attempted INTEGER NOT NULL DEFAULT 0,
              created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, messages TEXT NOT NULL DEFAULT '[]',
              draft TEXT NOT NULL DEFAULT '', complete_answers INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS deleted_conversation (id TEXT PRIMARY KEY, deleted_utc TEXT NOT NULL);
            INSERT OR IGNORE INTO metadata VALUES ('product','LOPATA'),('scenario','let_it_out'),('schema','1');
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT OR IGNORE INTO metadata VALUES ('created_version',$version)";
        command.Parameters.AddWithValue("$version", typeof(LetItOutConversationStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        command.ExecuteNonQuery(); transaction.Commit();
        using var identity = connection.CreateCommand();
        identity.CommandText = "INSERT OR IGNORE INTO metadata VALUES ('model_id',$id),('model_file',$file)";
        identity.Parameters.AddWithValue("$id", ManagedModelCatalog.LetItOutArtifactId);
        identity.Parameters.AddWithValue("$file", LetItOutModel.FileName); identity.ExecuteNonQuery();
        InitializeMemory(connection);
        InitializeTree(connection);
        InitializeAttachments(connection);
        InitializeRecall(connection);
    }

    private SqliteConnection Open(bool create = false)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(DatabasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false,
            ForeignKeys = true }.ToString());
        connection.Open(); return connection;
    }
    private static void Id(string id)
    { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid conversation id."); }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static LetItOutConversationSummary Summary(SqliteDataReader row) => new(row.GetString(0), row.GetString(1),
        row.GetBoolean(2), row.GetBoolean(3), row.GetBoolean(4), DateTimeOffset.Parse(row.GetString(5), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(row.GetString(6), CultureInfo.InvariantCulture));
    private const string Columns = "id,title,manual_title,favorite,title_attempted,created_utc,updated_utc";

    public IReadOnlyList<LetItOutConversationSummary> List()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM conversation ORDER BY updated_utc DESC,id";
        using var reader = command.ExecuteReader(); var result = new List<LetItOutConversationSummary>();
        while (reader.Read()) result.Add(Summary(reader)); return result;
    }
    public LetItOutConversation? Load(string id)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + ",messages,draft FROM conversation WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var messages = JsonSerializer.Deserialize<LetItOutSavedMessage[]>(row.GetString(7))
            ?? throw new InvalidDataException("Invalid conversation body.");
        if (messages.Any(message => message is null || message.Role is not ("user" or "assistant") || message.Content is null))
            throw new InvalidDataException("Invalid conversation message.");
        return new(Summary(row), messages, row.GetString(8), DraftAttachmentIds(id));
    }
    public void Create(string id)
    {
        Id(id);
        if (Execute("INSERT INTO conversation (id,created_utc,updated_utc) SELECT $id,$now,$now WHERE NOT EXISTS (SELECT 1 FROM deleted_conversation WHERE id=$id)", id) != 1)
            throw new InvalidDataException("Conversation was removed.");
    }
    public void SaveContent(string id, IReadOnlyList<LetItOutSavedMessage> messages, string draft)
    {
        Id(id);
        if (messages.Any(message => message.Role is not ("user" or "assistant") || message.Content is null))
            throw new InvalidDataException("Invalid conversation message.");
        foreach (var message in messages) ValidateAttachments(id, message.Attachments);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        messages = PreserveMessageClocks(connection, transaction, id, messages);
        CapturePairs(connection, transaction, id, messages);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE conversation SET messages=$messages,draft=$draft,complete_answers=$answers,updated_utc=$now WHERE id=$id";
        command.Parameters.AddWithValue("$messages", JsonSerializer.Serialize(messages));
        command.Parameters.AddWithValue("$draft", draft);
        command.Parameters.AddWithValue("$answers", messages.Count(message => message.Role == "assistant" && message.Complete));
        command.Parameters.AddWithValue("$now", Now()); command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Conversation was removed.");
        transaction.Commit();
        WriteMemoryIndex(id);
    }
    public bool SetManualTitle(string id, string title) => Execute(
        "UPDATE conversation SET title=$value,manual_title=1 WHERE id=$id", id, Title(title)) == 1;
    public bool SetFavorite(string id, bool favorite) => Execute(
        "UPDATE conversation SET favorite=$value WHERE id=$id", id, favorite ? 1 : 0) == 1;
    public bool IsDeleted(string id) => ExecuteScalar(id) != 0;
    private long ExecuteScalar(string id)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM deleted_conversation WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); return (long)command.ExecuteScalar()!;
    }
    public bool Delete(string id)
    {
        Id(id); using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", Now());
        command.CommandText = "INSERT OR IGNORE INTO deleted_conversation SELECT id,$now FROM conversation WHERE id=$id";
        command.ExecuteNonQuery(); command.CommandText = "DELETE FROM conversation WHERE id=$id";
        var removed = command.ExecuteNonQuery() == 1; transaction.Commit();
        if (removed) DeleteMemoryIndex(id);
        if (removed) DeleteAttachmentCopies(id);
        return removed;
    }
    public bool TryBeginAutoTitle(string id) => Execute("""
        UPDATE conversation SET title_attempted=1 WHERE id=$id AND manual_title=0
        AND title_attempted=0 AND complete_answers>=2
        """, id) == 1;
    public bool TryApplyAutoTitle(string id, string title) => Execute("""
        UPDATE conversation SET title=$value WHERE id=$id AND manual_title=0 AND title_attempted=1
        """, id, Title(title)) == 1;

    private int Execute(string sql, string id, object? value = null)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        if (sql.Contains("$now", StringComparison.Ordinal)) command.Parameters.AddWithValue("$now", Now());
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        return command.ExecuteNonQuery();
    }
    public static string Title(string value)
    {
        var title = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('"', '«', '»');
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("A title is required.");
        var elements = StringInfo.ParseCombiningCharacters(title);
        return elements.Length <= 80 ? title : title[..elements[80]].TrimEnd();
    }
}
