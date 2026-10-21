using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AIHub.Services.MusicLibrary;

/// <summary>Canonical personal works. Derived vectors never own the user's text.</summary>
public sealed class LyricLibraryStore
{
    public static LyricLibraryStore Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "LyricsLibrary"));
    public string DirectoryPath { get; }
    public LyricLibraryStore(string directory) => DirectoryPath = Path.GetFullPath(directory);
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(DirectoryPath);
        Lopata.Updates.SafeUpdatePath.RejectLinks(DirectoryPath);
        Lopata.Updates.SafeUpdatePath.RejectLinks(Path.Combine(DirectoryPath, "works.sqlite"));
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(DirectoryPath, "works.sqlite"), Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 30 }.ToString());
        try
        {
            db.Open();
            using var version = db.CreateCommand(); version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema > 1) throw new InvalidDataException("Music.Library.NewerSchema");
            if (schema == 1) return db;
            using var create = db.CreateCommand();
            create.CommandText = """
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS state(id INTEGER PRIMARY KEY CHECK(id=1), generation INTEGER NOT NULL);
                INSERT OR IGNORE INTO state VALUES(1,0);
                CREATE TABLE IF NOT EXISTS works(id TEXT PRIMARY KEY, revision INTEGER NOT NULL, hash TEXT NOT NULL,
                    data TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS works_hash ON works(hash,deleted);
                CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(id UNINDEXED,terms,tokenize='unicode61');
                PRAGMA user_version=1;
                """;
            create.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    public bool ContainsText(string text)
    {
        var path = Path.Combine(DirectoryPath, "works.sqlite");
        if (!File.Exists(path)) return false;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT 1 FROM works WHERE hash=$hash AND deleted=0 LIMIT 1";
        command.Parameters.AddWithValue("$hash", new LyricEntry { Text = text }.TextHash);
        return command.ExecuteScalar() is not null;
    }
    public LyricLibrarySnapshot Snapshot()
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT generation FROM state WHERE id=1";
        var generation = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT data FROM works WHERE deleted=0 ORDER BY rowid DESC";
        var entries = new List<LyricEntry>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) entries.Add(Decode(reader.GetString(0)));
        transaction.Commit(); return new(generation, entries.ToArray());
    }
    public LyricEntry? Find(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT data FROM works WHERE id=$id AND deleted=0"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? Decode(json) : null;
    }
    public IReadOnlyList<LyricEntry> Search(string query, int limit = 200)
    {
        if (query.Length > 2000 || limit is < 1 or > 1000) throw new ArgumentException("Music.Library.InvalidQuery");
        var terms = Regex.Matches(query, @"[\p{L}\p{N}]+").Select(m => m.Value).Take(12).ToArray();
        if (terms.Length == 0) return Snapshot().Entries.Take(limit).ToArray();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT w.data FROM search s JOIN works w ON w.id=s.id WHERE search MATCH $q AND w.deleted=0 ORDER BY bm25(search),w.rowid DESC LIMIT $limit";
        command.Parameters.AddWithValue("$q", string.Join(" OR ", terms.Select(t => "\"" + t + "\"*")));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<LyricEntry>();
        using var reader = command.ExecuteReader(); while (reader.Read()) result.Add(Decode(reader.GetString(0)));
        return result;
    }
    public LyricSaveResult Save(LyricEntry entry)
    {
        entry.Validate(); using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT data FROM works WHERE hash=$hash AND deleted=0 AND id<>$id LIMIT 1";
        command.Parameters.AddWithValue("$hash", entry.TextHash); command.Parameters.AddWithValue("$id", entry.Id);
        if (command.ExecuteScalar() is string duplicate) return new(Decode(duplicate), true);
        command.CommandText = "SELECT revision,deleted FROM works WHERE id=$id";
        using (var reader = command.ExecuteReader())
        {
            var exists = reader.Read();
            if (exists ? reader.GetInt32(0) != entry.Revision || reader.GetInt32(1) != 0 : entry.Revision != 0)
                throw new InvalidDataException("Music.Library.Changed");
        }
        var saved = entry with { Revision = checked(entry.Revision + 1), UpdatedUtc = DateTimeOffset.UtcNow };
        command.CommandText = "INSERT INTO works VALUES($id,$revision,$hash,$data,0) ON CONFLICT(id) DO UPDATE SET revision=$revision,hash=$hash,data=$data";
        command.Parameters.AddWithValue("$revision", saved.Revision); command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(saved));
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM search WHERE id=$id"; command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO search(id,terms) VALUES($id,$terms)";
        command.Parameters.AddWithValue("$terms", string.Join("\n", saved.Title, saved.Author, saved.Artist, saved.Genre, saved.Style, saved.Tags, saved.Notes, saved.Text));
        command.ExecuteNonQuery(); Bump(db, transaction); transaction.Commit(); return new(saved, false);
    }
    public void SetDeleted(string id, int revision, bool deleted)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE works SET deleted=$deleted,revision=revision+1 WHERE id=$id AND revision=$revision AND deleted<>$deleted";
        command.Parameters.AddWithValue("$deleted", deleted ? 1 : 0); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$revision", revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Music.Library.Changed");
        command.CommandText = "SELECT data,revision FROM works WHERE id=$id";
        LyricEntry entry;
        using (var reader = command.ExecuteReader()) { reader.Read(); entry = Decode(reader.GetString(0)) with { Revision = reader.GetInt32(1) }; }
        command.CommandText = "UPDATE works SET data=$data WHERE id=$id";
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(entry)); command.ExecuteNonQuery();
        Bump(db, transaction); transaction.Commit();
    }
    private static void Bump(SqliteConnection db, SqliteTransaction transaction)
    { using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE state SET generation=generation+1 WHERE id=1"; command.ExecuteNonQuery(); }
    private static LyricEntry Decode(string json)
    { var entry = JsonSerializer.Deserialize<LyricEntry>(json) ?? throw new InvalidDataException("Music.Library.InvalidRecord"); entry.Validate(); return entry; }
}
