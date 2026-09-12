using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

/// <summary>Project-owned transactional memory. Approval never edits manuscript files.</summary>
public sealed class LiteraryJellyStore(LiteraryProjectLayout layout)
{
    public string FilePath => Path.Combine(layout.Root, "Jelly", "память.lopata");
    private SqliteConnection Open()
    {
        layout.EnsureFolder("Jelly");
        if (File.Exists(FilePath) && (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked memory file is not supported.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false, ForeignKeys = true }.ToString());
        try
        {
            db.Open(); Execute(db, null, "PRAGMA synchronous=FULL; PRAGMA journal_mode=DELETE;");
            var version = Convert.ToInt32(Scalar(db, null, "PRAGMA user_version"));
            if (version == 0)
            {
                if (Convert.ToInt32(Scalar(db, null, "SELECT count(*) FROM sqlite_master WHERE type='table'")) != 0)
                    throw new InvalidDataException("Unrecognized memory database.");
                using var tx = db.BeginTransaction();
                Execute(db, tx, """
                    CREATE TABLE meta(project_id TEXT NOT NULL,format TEXT NOT NULL);
                    CREATE TABLE batch(id TEXT PRIMARY KEY,part_id TEXT,revision TEXT,payload TEXT,status TEXT,UNIQUE(part_id,revision));
                    CREATE TABLE fact(id TEXT PRIMARY KEY,batch_id TEXT REFERENCES batch(id),version INTEGER,payload TEXT,active INTEGER);
                    CREATE TABLE change(seq INTEGER PRIMARY KEY,fact_id TEXT,operation TEXT,previous TEXT,current TEXT,at TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """);
                Execute(db, tx, "INSERT INTO meta VALUES($id,'AIHub.Jelly')", ("$id", layout.ProjectId)); tx.Commit();
            }
            else if (version != 1) throw new InvalidDataException("Unsupported memory version.");
            if ((string?)Scalar(db, null, "SELECT project_id FROM meta WHERE format='AIHub.Jelly'") != layout.ProjectId)
                throw new InvalidDataException("Memory belongs to another project.");
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = tx;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var command = Command(db, tx, sql, args); command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var command = Command(db, tx, sql, args); return command.ExecuteScalar(); }
    public LiteraryJellyBatch? Find(string partId, string revision)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT payload,status FROM batch WHERE part_id=$p AND revision=$r", ("$p", partId), ("$r", revision));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? JsonSerializer.Deserialize<LiteraryJellyBatch>(reader.GetString(0))! with { Status = reader.GetString(1) } : null;
    }
    public void Stage(LiteraryJellyBatch batch)
    {
        VerifySource(batch); using var db = Open();
        Execute(db, null, "INSERT OR IGNORE INTO batch VALUES($id,$p,$r,$data,'pending')", ("$id", batch.Id), ("$p", batch.PartId), ("$r", batch.Revision), ("$data", JsonSerializer.Serialize(batch)));
    }
    public void VerifySource(LiteraryJellyBatch batch)
    {
        layout.EnsurePresent(); var chapters = new LiteraryChapterStore(layout.Root); chapters.Open();
        var part = chapters.Index.Parts.SingleOrDefault(p => p.Id == batch.PartId);
        if (part is null || part.Id == chapters.Index.ActiveId
            || LiteraryWorkIndex.Revision(LiteraryChapterFiles.Read(Path.Combine(layout.Root, "chapters", part.FileName))) != batch.Revision
            || LiteraryWorkIndex.Revision(batch.SourceText) != batch.Revision)
            throw new IOException("The source changed or is still the active draft. Reopen memory preparation.");
    }
    public void Confirm(LiteraryJellyBatch original, IReadOnlyList<LiteraryJellyFact> decisions)
    {
        VerifySource(original);
        if (decisions.Count != original.Facts.Length || !decisions.Select(f => f.Id).Order().SequenceEqual(original.Facts.Select(f => f.Id).Order()))
            throw new InvalidDataException("Every proposed fact must have one decision.");
        foreach (var fact in decisions) LiteraryJellyContract.Validate(fact, original.SourceText);
        using var db = Open(); using var tx = db.BeginTransaction();
        var stored = Scalar(db, tx, "SELECT payload FROM batch WHERE id=$id AND status='pending'", ("$id", original.Id)) as string;
        if (stored != JsonSerializer.Serialize(original with { Status = "pending" })) throw new IOException("The proposal changed or was already confirmed.");
        Execute(db, tx, "UPDATE fact SET active=0 WHERE batch_id IN (SELECT id FROM batch WHERE part_id=$p)", ("$p", original.PartId));
        foreach (var decision in decisions)
        {
            var proposed = original.Facts.Single(f => f.Id == decision.Id);
            var fact = decision with { Edited = JsonSerializer.Serialize(proposed) != JsonSerializer.Serialize(decision) };
            var data = JsonSerializer.Serialize(fact);
            Execute(db, tx, "INSERT INTO fact VALUES($id,$b,1,$data,$active)", ("$id", fact.Id), ("$b", original.Id), ("$data", data), ("$active", fact.Accepted ? 1 : 0));
            LogChange(db, tx, fact.Id, fact.Accepted ? "user_approved" : "user_excluded", JsonSerializer.Serialize(proposed), data);
        }
        Execute(db, tx, "UPDATE batch SET status='confirmed' WHERE id=$id", ("$id", original.Id));
        VerifySource(original); tx.Commit();
    }
    private static void LogChange(SqliteConnection db, SqliteTransaction tx, string id, string operation, string previous, string current) =>
        Execute(db, tx, "INSERT INTO change(fact_id,operation,previous,current,at) VALUES($id,$op,$old,$new,$at)",
            ("$id", id), ("$op", operation), ("$old", previous), ("$new", current), ("$at", DateTime.UtcNow.ToString("O")));
    public IReadOnlyList<LiteraryJellyEntry> Read()
    {
        if (!File.Exists(FilePath)) return [];
        using var db = Open(); using var cmd = Command(db, null, "SELECT f.id,b.part_id,b.revision,f.version,f.payload,b.payload FROM fact f JOIN batch b ON b.id=f.batch_id WHERE f.active=1 ORDER BY f.rowid DESC");
        using var reader = cmd.ExecuteReader(); var rows = new List<LiteraryJellyEntry>();
        while (reader.Read())
        {
            var batch = JsonSerializer.Deserialize<LiteraryJellyBatch>(reader.GetString(5))!;
            rows.Add(new(reader.GetString(0), reader.GetString(1), batch.Number, reader.GetString(2), reader.GetInt32(3), JsonSerializer.Deserialize<LiteraryJellyFact>(reader.GetString(4))!));
        }
        return rows;
    }
    public void Edit(IReadOnlyList<LiteraryJellyEntry> originals, IReadOnlyList<LiteraryJellyFact> changed)
    {
        layout.EnsurePresent();
        if (originals.Count != changed.Count || !originals.Select(e => e.Id).Order().SequenceEqual(changed.Select(f => f.Id).Order())) throw new InvalidDataException("Invalid edit set.");
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var entry in originals)
        {
            var batchJson = (string?)Scalar(db, tx, "SELECT payload FROM batch WHERE part_id=$p AND revision=$r", ("$p", entry.PartId), ("$r", entry.Revision));
            if (batchJson is null) throw new IOException("Source unavailable.");
            var batch = JsonSerializer.Deserialize<LiteraryJellyBatch>(batchJson)!; VerifySource(batch);
            var fact = changed.Single(f => f.Id == entry.Id); LiteraryJellyContract.Validate(fact, batch.SourceText);
            if (JsonSerializer.Serialize(fact) == JsonSerializer.Serialize(entry.Fact)) continue;
            fact = fact with { Edited = true }; var data = JsonSerializer.Serialize(fact);
            using var update = Command(db, tx, "UPDATE fact SET payload=$data,active=$active,version=version+1 WHERE id=$id AND version=$v AND active=1",
                ("$data", data), ("$active", fact.Accepted ? 1 : 0), ("$id", entry.Id), ("$v", entry.Version));
            if (update.ExecuteNonQuery() != 1) throw new IOException("The fact changed in another editor.");
            LogChange(db, tx, entry.Id, fact.Accepted ? "user_edited" : "user_excluded", JsonSerializer.Serialize(entry.Fact), data);
        }
        tx.Commit();
    }
}
