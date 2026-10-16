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
        IReadOnlyList<string> synonyms, CancellationToken token, Func<string, bool>? knownWord = null, MusicRhymeTuning? tuning = null)
    {
        token.ThrowIfCancellationRequested();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        tuning ??= new();
        var results = new List<MusicRhymeSuggestion>();
        var original = MusicRhymePhonetics.Clean(line.Ending);
        var synonymSet = synonyms.Select(MusicRhymePhonetics.Clean).Where(s => s.Length > 0 && s.Length <= tuning.Integer("search.synonym_length"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(tuning.Integer("search.synonyms")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in partner.Readings.Take(tuning.Integer("search.partner_readings")))
        {
            if (results.Count >= tuning.Integer("search.raw_rows")) break;
            var keys = MusicRhymeSound.NeighbourKeys(reading, tuning).Take(tuning.Integer("search.neighbour_keys")).ToArray(); if (keys.Length == 0) continue;
            var syllables = line.Readings.FirstOrDefault(r => r.Language == reading.Language)?.Syllables ?? reading.Syllables;
            // Direct indexed synonym lookup comes first: a useful synonym may be far beyond the first alphabetical page.
            if (synonymSet.Count > 0)
            {
                using var command = db.CreateCommand(); token.ThrowIfCancellationRequested();
                var keyParameters = AddList(command, "$k", keys); var wordParameters = AddList(command, "$s", synonymSet);
                command.CommandText = $"SELECT word,display,source,key,syllables FROM endings WHERE language=$language AND key IN ({keyParameters}) AND word IN ({wordParameters}) AND abs(syllables-$count)<=$delta LIMIT $limit";
                command.Parameters.AddWithValue("$language", reading.Language); command.Parameters.AddWithValue("$count", syllables);
                command.Parameters.AddWithValue("$delta", tuning.Integer("search.word_syllable_difference"));
                command.Parameters.AddWithValue("$limit", tuning.Integer("search.synonym_rows"));
                Read(command, reading, line.Number, original, synonymSet, results, token, tuning);
            }
            foreach (var key in keys)
            {
                if (results.Count >= tuning.Integer("search.raw_rows")) break;
                token.ThrowIfCancellationRequested(); using var command = db.CreateCommand();
                // The inner LIMIT bounds rows visited even for vowel-only rhyme keys with very large families.
                command.CommandText = """
                    SELECT word,display,source,key,syllables FROM
                      (SELECT word,display,source,key,syllables FROM endings
                       WHERE language=$language AND key=$key LIMIT $family)
                    WHERE word<>$word AND abs(syllables-$count)<=$delta
                    ORDER BY abs(syllables-$count),abs(length(word)-$length),word LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$language", reading.Language); command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$word", original); command.Parameters.AddWithValue("$count", syllables);
                command.Parameters.AddWithValue("$family", tuning.Integer("search.family_rows"));
                command.Parameters.AddWithValue("$delta", tuning.Integer("search.word_syllable_difference"));
                command.Parameters.AddWithValue("$length", original.Length); command.Parameters.AddWithValue("$limit", key == reading.Key ? tuning.Integer("search.exact_rows") : tuning.Integer("search.near_rows"));
                Read(command, reading, line.Number, original, synonymSet, results, token, tuning);
            }
        }
        var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ranked = results.OrderByDescending(r => r.Synonym).ThenByDescending(r => r.SoundScore)
            .ThenBy(r => Math.Abs(r.Word.Length - original.Length)).ThenBy(r => r.Word, StringComparer.Ordinal)
            .DistinctBy(r => r.Word, StringComparer.OrdinalIgnoreCase).ToArray();
        // Reserve a bounded near page before the callback cap too: four pronunciation families can supply 768 exact words.
        var candidates = ranked.Where(r => r.Synonym).Concat(ranked.Where(r => !r.Synonym && r.SoundScore < 1).Take(tuning.Integer("search.near_pool")))
            .Concat(ranked).DistinctBy(r => r.Word, StringComparer.OrdinalIgnoreCase).Take(tuning.Integer("search.candidate_pool"))
            .Where(r => r.Synonym || knownWord is null || IsKnown(r.Word)).ToArray();
        // Leave room for slant rhymes even when a large exact family fills the ordinary page.
        return candidates.Where(r => r.Synonym).Concat(candidates.Where(r => !r.Synonym && r.SoundScore < 1).Take(tuning.Integer("search.near_reserved")))
            .Concat(candidates).DistinctBy(r => r.Word, StringComparer.OrdinalIgnoreCase).Take(tuning.Integer("search.returned_candidates"))
            .OrderByDescending(r => r.Synonym).ThenByDescending(r => r.SoundScore).ToArray();

        bool IsKnown(string word)
        {
            token.ThrowIfCancellationRequested();
            if (!known.TryGetValue(word, out var result)) known[word] = result = knownWord!(word);
            return result;
        }
    }
    private static string AddList(SqliteCommand command, string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        foreach (var value in values) { var name = prefix + names.Count; command.Parameters.AddWithValue(name, value); names.Add(name); }
        return string.Join(",", names);
    }
    private static void Read(SqliteCommand command, MusicRhymeReading target, int line, string original,
        HashSet<string> synonyms, List<MusicRhymeSuggestion> results, CancellationToken token, MusicRhymeTuning tuning)
    {
        if (results.Count >= tuning.Integer("search.raw_rows")) return;
        using var reader = command.ExecuteReader();
        while (results.Count < tuning.Integer("search.raw_rows") && reader.Read())
        {
            token.ThrowIfCancellationRequested(); var word = reader.GetString(0); if (word == original) continue;
            var candidate = new MusicRhymeReading(word, target.Language, reader.GetString(3), reader.GetInt32(4), reader.GetString(1));
            var sound = MusicRhymeSound.Similarity(target, candidate, tuning); if (sound < tuning.Number("quality.near_similarity")) continue;
            results.Add(new(line, word, candidate.Display, reader.GetString(2), synonyms.Contains(word)) { SoundScore = sound });
        }
    }
}
