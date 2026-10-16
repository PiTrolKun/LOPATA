using System.IO;
using AIHub.Models;
using AIHub.Services;
using Microsoft.Data.Sqlite;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymeSoundTests
{
    [TestMethod]
    public void RawCollectionBudgetStopsBeforeLaterFamiliesAndCapsActualCandidateValidation()
    {
        using var index = new Fixture(fourFamilies: true);
        var checkedWords = new List<string>();
        var partner = Line("факт", "акт") with { Readings = [Reading("ru", "акт"), Reading("ru", "ист")] };
        var tuning = new MusicRhymeTuning { Values = new() { ["search.raw_rows"] = 3 } };
        var result = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), partner, [], CancellationToken.None,
            word => { checkedWords.Add(word); return true; }, tuning);
        Assert.AreEqual(3, result.Length); Assert.HasCount(3, checkedWords);
        Assert.IsTrue(result.All(r => r.Word.StartsWith('e') && r.SoundScore == 1));
        Assert.IsFalse(result.Any(r => r.Word == "near" || r.Word.StartsWith('f')));
    }

    [TestMethod]
    public void IncreasingSearchDepthCanRecoverAWordBeyondTheDefaultFamilyPage()
    {
        using var index = new Fixture();
        var shallow = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт"), [], CancellationToken.None,
            word => word == "e0600");
        Assert.IsEmpty(shallow);
        var tuning = new MusicRhymeTuning { Values = new() {
            ["search.family_rows"] = 800, ["search.exact_rows"] = 800, ["search.candidate_pool"] = 900 } };
        var deeper = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт"), [], CancellationToken.None,
            word => word == "e0600", tuning);
        Assert.AreEqual("e0600", deeper.Single().Word);
    }

    [TestMethod]
    public void NearThresholdAndResultBudgetActuallyChangeTheSearchOutput()
    {
        using var index = new Fixture();
        var tuning = new MusicRhymeTuning { Values = new() {
            ["quality.near_similarity"] = 1, ["search.returned_candidates"] = 3 } };
        var result = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт"), [], CancellationToken.None, tuning: tuning);
        Assert.AreEqual(3, result.Length);
        Assert.IsTrue(result.All(r => r.SoundScore == 1));
        Assert.IsFalse(result.Any(r => r.Word == "near"));
    }

    [TestMethod]
    public void PhoneticTailBudgetIsAppliedDuringSimilarityComparison()
    {
        var tail = Reading("ru", "а" + new string('н', 41));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(tail, tail));
        var tuning = new MusicRhymeTuning { Values = new() { ["quality.phonetic_units"] = 64 } };
        Assert.AreEqual(1d, MusicRhymeSound.Similarity(tail, tail, tuning));
    }

    [TestMethod]
    public void ExactAndSlantRhymesUsePhoneticTailsInBothLanguages()
    {
        var ru = Reading("ru", "акт"); var ruNear = Reading("ru", "ак");
        Assert.AreEqual(1d, MusicRhymeSound.Similarity(ru, ru));
        Assert.IsTrue(MusicRhymeSound.Similarity(ru, ruNear) >= MusicRhymeSound.MinimumNearSimilarity);
        Assert.IsTrue(MusicRhymeSound.Similarity(ru, ruNear) < 1);
        var en = Reading("en", "AY T"); var enNear = Reading("en", "AY D");
        Assert.AreEqual(1d, MusicRhymeSound.Similarity(en, en));
        Assert.IsTrue(MusicRhymeSound.Similarity(en, enNear) >= MusicRhymeSound.MinimumNearSimilarity);
        Assert.AreEqual(MusicRhymeSound.Similarity(en, enNear), MusicRhymeSound.Similarity(enNear, en));
        Assert.AreEqual(1d, MusicRhymeSound.Similarity(MusicRhymePhonetics.Russian("пло́д"), MusicRhymePhonetics.Russian("ко́т")));
    }

    [TestMethod]
    public void StressedVowelCannotBeOutweighedByLongIdenticalSuffix()
    {
        foreach (var language in new[] { "ru", "en" })
        {
            var a = Reading(language, language == "ru" ? "андамант" : "AY N D AH M AH N T");
            var b = Reading(language, language == "ru" ? "эндамант" : "IY N D AH M AH N T");
            Assert.IsTrue(MusicRhymeSound.Similarity(a, b) < MusicRhymeSound.MinimumNearSimilarity);
        }
        Assert.IsTrue(MusicRhymeSound.Similarity(Reading("ru", "от"), Reading("ru", "ок")) < MusicRhymeSound.MinimumNearSimilarity);
        Assert.IsTrue(MusicRhymeSound.Similarity(Reading("ru", "ак"), Reading("ru", "ах")) > MusicRhymeSound.Similarity(Reading("ru", "ак"), Reading("ru", "ат")));
    }

    [TestMethod]
    public void UnknownAndIncompatibleReadingsHaveNoSoundEvidence()
    {
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(null, Reading("ru", "от")));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(Reading("ru", "от"), null));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(Reading("ru", "от"), Reading("en", "AO T")));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(Reading("ru", ""), Reading("ru", "")));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(Reading("ru", "кот"), Reading("ru", "кот")));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(Reading("fr", "a"), Reading("fr", "a")));
        Assert.AreEqual(0d, MusicRhymeSound.Similarity(Reading("ru", new string('а', 200)), Reading("ru", "а")));
    }

    [TestMethod]
    public void CandidatePoolIncludesLateSynonymsAndSlantRhymesWithoutGrowingUnbounded()
    {
        using var index = new Fixture();
        var result = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт"), ["zebra"], CancellationToken.None);
        Assert.AreEqual(96, result.Length); Assert.AreEqual(result.Length, result.Select(r => r.Word).Distinct().Count());
        Assert.IsTrue(result.Any(r => r.Word == "zebra" && r.Synonym && r.SoundScore == 1));
        Assert.IsTrue(result.Any(r => r.Word == "near" && r.SoundScore is >= MusicRhymeSound.MinimumNearSimilarity and < 1));
        Assert.IsFalse(result.Any(r => r.Word is "bad-vowel" or "bad-size"));
        Assert.IsTrue(result.Any(r => r.Word.StartsWith('e') && r.SoundScore == 1));
    }

    [TestMethod]
    public void KnownWordsAreFilteredBeforePoolLimitAndSynonymsHaveTheirOwnIndexedLookup()
    {
        using var index = new Fixture(); var checkedWords = new HashSet<string>();
        var result = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт"), ["zebra"], CancellationToken.None,
            word => { checkedWords.Add(word); return word is "near" or "e0600"; });
        Assert.IsTrue(result.Any(r => r.Word == "zebra")); Assert.IsTrue(result.Any(r => r.Word == "near"));
        Assert.IsFalse(checkedWords.Contains("zebra"));
        // The general rhyme-family page visits at most 512 rows; the independently addressed synonym is still found.
        Assert.IsFalse(result.Any(r => r.Word == "e0600")); Assert.IsTrue(checkedWords.Count <= 768);
        Assert.IsEmpty(MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт") with { Readings = [] }, [], CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), Line("факт", "акт"), [], cancelled.Token));
    }

    [TestMethod]
    public void FourExactPronunciationFamiliesCannotConsumeThePreliminarySlantRhymeQuota()
    {
        using var index = new Fixture(fourFamilies: true); var checkedWords = new HashSet<string>();
        var partner = Line("факт", "акт") with { Readings = [Reading("ru", "акт"), Reading("ru", "ист"), Reading("ru", "ол"), Reading("ru", "эм")] };
        var result = MusicRhymeIndex.Find(index.Path, Line("оригинал", "ал"), partner, [], CancellationToken.None,
            word => { checkedWords.Add(word); return true; });
        Assert.AreEqual(96, result.Length);
        Assert.IsTrue(result.Any(r => r.Word == "near" && r.SoundScore < 1));
        Assert.IsTrue(result.Any(r => r.SoundScore == 1));
        Assert.IsTrue(checkedWords.Count <= 768);
    }

    private static MusicRhymeReading Reading(string language, string key) => new("fixture", language, key, 1, "fixture");
    private static MusicRhymeLine Line(string word, string key) => new(1, 0, word.Length, 0, word, word, 0, "ru", 1, [Reading("ru", key)]);
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lopata-rhyme-sound-" + Guid.NewGuid().ToString("N") + ".sqlite");
        public Fixture(bool fourFamilies = false)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); db.Open();
            using var schema = db.CreateCommand();
            schema.CommandText = "CREATE TABLE endings(language TEXT,key TEXT,word TEXT,display TEXT,syllables INTEGER,source TEXT,PRIMARY KEY(language,key,word,display)) WITHOUT ROWID";
            schema.ExecuteNonQuery(); using var transaction = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO endings VALUES('ru',$key,$word,$word,$syllables,'fixture')";
            insert.Parameters.AddWithValue("$key", "акт"); insert.Parameters.AddWithValue("$word", ""); insert.Parameters.AddWithValue("$syllables", 1);
            for (var i = 0; i < 700; i++) { insert.Parameters["$word"].Value = "e" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture); insert.ExecuteNonQuery(); }
            Add("zebra", "акт", 1); Add("near", "ак", 1); Add("bad-vowel", "экт", 1); Add("bad-size", "акт", 5);
            if (fourFamilies)
                foreach (var (prefix, key) in new[] { ("f", "ист"), ("g", "ол"), ("h", "эм") })
                    for (var i = 0; i < 192; i++) Add(prefix + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), key, 1);
            transaction.Commit();
            void Add(string word, string key, int syllables)
            { insert.Parameters["$word"].Value = word; insert.Parameters["$key"].Value = key; insert.Parameters["$syllables"].Value = syllables; insert.ExecuteNonQuery(); }
        }
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            if (!full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(full).StartsWith("lopata-rhyme-sound-", StringComparison.Ordinal)) throw new InvalidOperationException();
            File.Delete(full);
        }
    }
}
