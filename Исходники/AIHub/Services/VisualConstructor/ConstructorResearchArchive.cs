using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AIHub.Services.VisualConstructor;

internal sealed record ResearchFile(string Path, string Text);
internal sealed record ResearchSkip(string Path, string Reason);
internal sealed record ResearchMaterial(Guid Id, string Title, string Url, DateTimeOffset Retrieved, string Version,
    string License, string Summary, ResearchFile[] Files, ResearchSkip[] Skipped, bool Incomplete = false)
{
    internal ConstructorReference Reference(string? path = null)
    {
        var text = path is null ? Summary : Files.FirstOrDefault(file => file.Path == path)?.Text ?? Summary;
        return new(path is null ? Title : Title + " — " + path, Url, Retrieved, Version, License, text[..Math.Min(2400, text.Length)]);
    }
}

internal sealed record ConstructorReference(string Title, string Url, DateTimeOffset Retrieved, string Version, string License, string Text);

internal sealed class ConstructorResearchArchive(string directory, bool managedStorage = false)
{
    internal const long MiB = 1024 * 1024;
    internal const int DefaultQuotaMiB = 64;
    private static readonly Regex Words = new(@"[\p{L}\p{N}_]{2,}", RegexOptions.Compiled);

    internal static ConstructorResearchArchive OpenDefault() =>
        new(ManagedWorkStorage.Current.Results(Path.Combine("VisualConstructor", "Research")), managedStorage: true);

    internal IReadOnlyList<ResearchMaterial> Search(string query, int maximum = 20)
    {
        using var connection = Open();
        var terms = Words.Matches(query).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24).Select(term => $"\"{term}\"").ToArray();
        if (terms.Length == 0 && !string.IsNullOrWhiteSpace(query)) return [];
        using var command = connection.CreateCommand();
        command.CommandText = terms.Length == 0
            ? "SELECT data FROM material ORDER BY created DESC LIMIT $max"
            : """
              SELECT material.data FROM material_search JOIN material ON material.id=material_search.id
              WHERE material_search MATCH $words ORDER BY bm25(material_search), material.created DESC LIMIT $max
              """;
        command.Parameters.AddWithValue("$max", maximum);
        if (terms.Length > 0) command.Parameters.AddWithValue("$words", string.Join(" OR ", terms));
        using var reader = command.ExecuteReader();
        var results = new List<ResearchMaterial>();
        while (reader.Read()) results.Add(Parse(reader.GetString(0)));
        return results;
    }

    internal int ReadQuotaMiB()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM research_settings WHERE name='quota_mib'";
        return command.ExecuteScalar() is long value ? (int)value : DefaultQuotaMiB;
    }

    internal void SetQuotaMiB(int value)
    {
        if (value is < 1 or > 1024) throw new InvalidDataException("VisualConstructor.ResearchQuotaInvalid");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (ArchiveBytes(connection, transaction) > value * MiB) throw new IOException("VisualConstructor.ResearchQuotaFull");
        using var command = Command(connection, transaction,
            "INSERT OR REPLACE INTO research_settings VALUES ('quota_mib', $value)");
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal void Save(ResearchMaterial material)
    {
        var json = JsonSerializer.Serialize(material, BackgroundOperationStore.Options);
        var size = System.Text.Encoding.UTF8.GetByteCount(json);
        var quota = ReadQuotaMiB();
        using var space = managedStorage ? ManagedWorkStorage.Current.Reserve(directory, size * 3L) : null;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var storedQuota = Command(connection, transaction, "SELECT value FROM research_settings WHERE name='quota_mib'");
        if (storedQuota.ExecuteScalar() is long latest) quota = (int)latest;
        Remove(connection, transaction, material.Id);
        if (MaterialBytes(connection, transaction) + size > quota * MiB)
            throw new IOException("VisualConstructor.ResearchQuotaFull");
        using var command = Command(connection, transaction, "INSERT INTO material VALUES ($id, $date, $data)");
        command.Parameters.AddWithValue("$id", material.Id.ToString("N"));
        command.Parameters.AddWithValue("$date", material.Retrieved.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$data", json);
        command.ExecuteNonQuery();
        Index(connection, transaction, material);
        if (ArchiveBytes(connection, transaction) > quota * MiB) throw new IOException("VisualConstructor.ResearchQuotaFull");
        transaction.Commit();
    }

    internal void Remove(Guid id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Remove(connection, transaction, id);
        transaction.Commit();
    }

    internal void Rebuild()
    {
        var quota = ReadQuotaMiB();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var clear = Command(connection, transaction, "DELETE FROM material_search");
        clear.ExecuteNonQuery();
        using var source = Command(connection, transaction, "SELECT data FROM material");
        using (var reader = source.ExecuteReader())
            while (reader.Read()) Index(connection, transaction, Parse(reader.GetString(0)));
        if (ArchiveBytes(connection, transaction) > quota * MiB) throw new IOException("VisualConstructor.ResearchQuotaFull");
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "technical-sources.sqlite");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA auto_vacuum=FULL;
                CREATE TABLE IF NOT EXISTS material(id TEXT PRIMARY KEY, created INTEGER NOT NULL, data TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS research_settings(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
                CREATE VIRTUAL TABLE IF NOT EXISTS material_search USING fts5(id UNINDEXED, title, content, tokenize='unicode61');
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void Remove(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, "DELETE FROM material_search WHERE id=$id; DELETE FROM material WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static void Index(SqliteConnection connection, SqliteTransaction transaction, ResearchMaterial material)
    {
        using var command = Command(connection, transaction, "INSERT INTO material_search VALUES ($id, $title, $body)");
        command.Parameters.AddWithValue("$id", material.Id.ToString("N"));
        command.Parameters.AddWithValue("$title", material.Title);
        command.Parameters.AddWithValue("$body", string.Join("\n", new[] { material.Url, material.Version, material.License, material.Summary }
            .Concat(material.Files.Select(file => file.Path + "\n" + file.Text))));
        command.ExecuteNonQuery();
    }

    private static long MaterialBytes(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT COALESCE(SUM(length(CAST(data AS BLOB))), 0) FROM material");
        return (long)command.ExecuteScalar()!;
    }

    private static long ArchiveBytes(SqliteConnection connection, SqliteTransaction transaction)
    {
        long Pragma(string name)
        {
            using var command = Command(connection, transaction, "PRAGMA " + name);
            return (long)command.ExecuteScalar()!;
        }
        return (Pragma("page_count") - Pragma("freelist_count")) * Pragma("page_size");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static ResearchMaterial Parse(string json) =>
        JsonSerializer.Deserialize<ResearchMaterial>(json, BackgroundOperationStore.Options)
        ?? throw new InvalidDataException("VisualConstructor.ResearchArchiveInvalid");
}
