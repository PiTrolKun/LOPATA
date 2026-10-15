using System.IO;
using System.Text.Json;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

/// <summary>Restartable CPU suffix index. Source records remain in the licensed, immutable library.</summary>
public static class MusicRhymeIndex
{
    public static string Ensure(MusicRhymeLexicon lexicon, IProgress<RagBookProgress> progress, CancellationToken token,
        string? cacheRoot = null)
    {
        var root = cacheRoot ?? Path.Combine(AppDataPaths.BaseDirectory, "Music", "RhymeIndexes"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, lexicon.Hash + "-phonetic-v1.sqlite");
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); target.Open();
        using (var schema = target.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS endings(language TEXT,key TEXT,word TEXT,display TEXT,syllables INTEGER,source TEXT,
                    PRIMARY KEY(language,key,word,display)) WITHOUT ROWID;
                """; schema.ExecuteNonQuery();
        }
        long last = 0, processed = 0;
        using (var metadata = target.CreateCommand())
        {
            metadata.CommandText = "SELECT key,value FROM meta"; using var reader = metadata.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(0) == "ready") return path;
                if (reader.GetString(0) == "last") last = long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
                if (reader.GetString(0) == "processed") processed = long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = lexicon.Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); source.Open();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var command = source.CreateCommand(); command.CommandText = "SELECT id,word,kind,data,source_id FROM entries WHERE id>$last ORDER BY id LIMIT 5000";
            command.Parameters.AddWithValue("$last", last); using var reader = command.ExecuteReader(); var count = 0;
            using var transaction = target.BeginTransaction();
            using var insert = target.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO endings VALUES($language,$key,$word,$display,$syllables,$source)";
            foreach (var name in new[] { "$language", "$key", "$word", "$display", "$syllables", "$source" }) insert.Parameters.AddWithValue(name, "");
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); last = reader.GetInt64(0); count++;
                var kind = reader.GetString(2); if (kind is not ("stress" or "pronunciation")) continue;
                var word = reader.GetString(1); using var data = JsonDocument.Parse(reader.GetString(3));
                var readings = kind == "stress" ? data.RootElement.GetProperty("variants").EnumerateArray()
                    .Select(v => MusicRhymePhonetics.Russian(v.GetString()!)).Where(r => r is not null).Select(r => r!).ToArray()
                    : MusicRhymePhonetics.English(word, data.RootElement.GetProperty("phonemes").EnumerateArray().Select(p => p.GetString()!)) is { } en ? [en] : Array.Empty<MusicRhymeReading>();
                foreach (var reading in readings)
                {
                    insert.Parameters["$language"].Value = reading.Language; insert.Parameters["$key"].Value = reading.Key;
                    insert.Parameters["$word"].Value = word; insert.Parameters["$display"].Value = reading.Display;
                    insert.Parameters["$syllables"].Value = reading.Syllables; insert.Parameters["$source"].Value = reader.GetString(4);
                    insert.ExecuteNonQuery();
                }
            }
            using var save = target.CreateCommand(); save.Transaction = transaction;
            processed += count; save.CommandText = "INSERT OR REPLACE INTO meta VALUES('last',$last),('processed',$processed)";
            save.Parameters.AddWithValue("$last", last.ToString(System.Globalization.CultureInfo.InvariantCulture));
            save.Parameters.AddWithValue("$processed", processed.ToString(System.Globalization.CultureInfo.InvariantCulture)); save.ExecuteNonQuery();
            if (count == 0) { save.CommandText = "INSERT OR REPLACE INTO meta VALUES('ready','1')"; save.ExecuteNonQuery(); }
            transaction.Commit(); progress.Report(new("Index", (int)processed, lexicon.Records));
            if (count == 0) return path;
        }
    }
    public static MusicRhymeSuggestion[] Find(string index, MusicRhymeLine line, MusicRhymeLine partner,
        IReadOnlyList<string> synonyms, CancellationToken token, Func<string, bool>? knownWord = null)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        var results = new List<MusicRhymeSuggestion>();
        foreach (var reading in partner.Readings)
        {
            token.ThrowIfCancellationRequested(); using var command = db.CreateCommand();
            command.CommandText = "SELECT word,display,source FROM endings WHERE language=$language AND key=$key AND word<>$word AND abs(syllables-$count)<=1 ORDER BY abs(syllables-$count),abs(length(word)-$length),word LIMIT 160";
            command.Parameters.AddWithValue("$language", reading.Language); command.Parameters.AddWithValue("$key", reading.Key);
            command.Parameters.AddWithValue("$word", MusicRhymePhonetics.Clean(line.Ending));
            command.Parameters.AddWithValue("$count", line.Readings.FirstOrDefault()?.Syllables ?? reading.Syllables);
            command.Parameters.AddWithValue("$length", MusicRhymePhonetics.Clean(line.Ending).Length);
            using var reader = command.ExecuteReader();
            while (reader.Read()) results.Add(new(line.Number, reader.GetString(0), reader.GetString(1), reader.GetString(2), synonyms.Contains(reader.GetString(0), StringComparer.OrdinalIgnoreCase)));
        }
        return results.DistinctBy(r => r.Word).Where(r => r.Synonym || knownWord is null || knownWord(r.Word))
            .OrderByDescending(r => r.Synonym).Take(8).ToArray();
    }
}
