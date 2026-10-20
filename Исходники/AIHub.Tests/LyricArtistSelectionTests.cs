using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricArtistSelectionTests
{
    private static LyricEntry Song(string artist, string title = "Work", string text = "[Verse]\nQuiet streets hold a story\n[Chorus]\nA door remembers home") =>
        new() { Artist = artist, Title = title, Genre = "folk", Language = "en", Text = text };

    [TestMethod, DataRow("Напиши как у «Линия 47»."), DataRow("Новая песня в стиле ЛИНИЯ 47."),
        DataRow("Write like the performer Линия 47."), DataRow("Линия 47")]
    public void ExactNameSelectsOnlyThatArtistDespiteUnrelatedTopic(string query)
    {
        var target = Song("Линия 47"); var unrelated = Song("Линия 470", "Tram song");
        var choice = LyricArtistSelection.Resolve([target, unrelated], query + (query == "Линия 47" ? "" : " About trams."));
        Assert.IsNotNull(choice); Assert.AreEqual(0, choice.Status.Missing.Length);
        Assert.AreEqual(target.Id, choice.Entries.Single().Id);
        var examples = choice.Examples(query);
        Assert.IsTrue(examples.All(e => e.Artist == "Линия 47" && e.MatchKind == "artist" && e.Genre == "folk" && e.Language == "en"));
    }
    [TestMethod, DataRow("Песня о море в стиле рок"), DataRow("Не как «Море», придумай другое"),
        DataRow("Not like Море. New song."), DataRow("Напиши как Мореход"),
        DataRow("Not like the performer Море. New song."), DataRow("Не как исполнитель Море, придумай другое")]
    public void TopicPrefixAndNegatedArtistDoNotBecomeAnArtistFilter(string query) =>
        Assert.IsNull(LyricArtistSelection.Resolve([Song("Море")], query));

    [TestMethod]
    public void UnknownAndMixedArtistsAreExplicitAndNeverFallBackToOtherPerformers()
    {
        var known = Song("Unit 83");
        var missing = LyricArtistSelection.Resolve([known], "Write like the performer «Absent Unit».");
        Assert.IsNotNull(missing); Assert.AreEqual("Absent Unit", missing.Status.Missing.Single());
        Assert.AreEqual(0, missing.Examples("English").Length);
        var mixed = LyricArtistSelection.Resolve([known], "Как «Unit 83», а также как «Absent Unit».");
        Assert.IsNotNull(mixed); Assert.AreEqual("Absent Unit", mixed.Status.Missing.Single());
        Assert.IsTrue(mixed.Examples("English").All(e => e.Id == known.Id));
    }
    [TestMethod]
    public void ArtistPunctuationAndWhitespaceAreLiteralMetadata()
    {
        var quoted = LyricArtistSelection.Resolve([Song("N' Roses + (83)")], "In the style of «N' Roses + (83)». New song.");
        Assert.IsNotNull(quoted); Assert.AreEqual(0, quoted.Status.Missing.Length);
        Assert.AreEqual("N' Roses + (83)", quoted.Entries.Single().Artist);
        var normalized = LyricArtistSelection.Resolve([Song("Ёж 47")], "Как у «еж    47».");
        Assert.IsNotNull(normalized); Assert.AreEqual(1, normalized.Entries.Length);
    }
    [TestMethod]
    public void SeveralWorksAreInterleavedAndLargeSectionsCannotConsumeTheWholeQuota()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Song("Unit 83", "Work " + i,
            "[Verse]\n" + new string('я', 3000) + "😀\n[Chorus]\nA different refrain " + i)).ToArray();
        var selection = LyricArtistSelection.Resolve(entries, "Like Unit 83.")!;
        var examples = selection.Examples("English");
        Assert.AreEqual(8, examples.Length); Assert.AreEqual(5, examples.Select(e => e.Id).Distinct().Count());
        Assert.IsTrue(examples.All(e => e.Text.Length <= 1200 && !char.IsHighSurrogate(e.Text[^1])));
        CollectionAssert.AreEqual(entries.Select(e => e.Id).ToArray(), examples.Take(5).Select(e => e.Id).ToArray());
    }
    [TestMethod]
    public async Task ArtistMetadataSearchUsesNoEmbeddingAndDeletedOrRenamedArtistIsNotReused()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-artist-" + Guid.NewGuid().ToString("N"));
        var store = new LyricLibraryStore(root); var old = LyricLibraryPreparation.RequestPreparation;
        LyricLibraryPreparation.RequestPreparation = (_, _) => throw new InvalidOperationException("Embedding must not run for exact metadata.");
        try
        {
            var entry = store.Save(Song("Unit 83")).Entry;
            var search = new LyricLibrarySearch(store, "unused");
            var examples = await search.SearchAsync("Write like Unit 83.", new Progress<RagBookProgress>(), CancellationToken.None);
            Assert.IsGreaterThan(0, examples.Length);
            var longRequest = new string('x', 1700) + " Write like Unit 83.";
            var selectedBeforeEmbeddingQueryIsShortened = LyricArtistSelection.Resolve(store.Snapshot().Entries, longRequest);
            var longHits = await search.SearchAsync(LyricExampleContext.Query(longRequest), new Progress<RagBookProgress>(),
                CancellationToken.None, selectedBeforeEmbeddingQueryIsShortened);
            Assert.AreEqual(entry.Id, longHits[0].Id);
            var changed = store.Save(entry with { Artist = "Unit 84" }).Entry;
            Assert.ThrowsExactly<InvalidDataException>(() => LyricExampleContext.ValidateCurrent(store, examples));
            Assert.AreEqual(0, (await search.SearchAsync("Write like the performer «Unit 83».", new Progress<RagBookProgress>(), CancellationToken.None)).Length);
            store.SetDeleted(changed.Id, changed.Revision, true);
            Assert.AreEqual(0, (await search.SearchAsync("Write like the performer «Unit 84».", new Progress<RagBookProgress>(), CancellationToken.None)).Length);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => search.SearchAsync("Unit 84", new Progress<RagBookProgress>(), cancel.Token));
        }
        finally { LyricLibraryPreparation.RequestPreparation = old; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task MissingArtistStatusReachesModelOnlyWhenEnabledAndFitsTheCountedContext()
    {
        var session = MusicPoetryProtocol.Create(new(), key => key);
        session.Messages.Add(new("user", "Write like Unit 83", "turn") { InContext = true });
        session.PendingLibraryArtist = new(["Unit 83"], ["Unit 83"]);
        static Task<int> Count(string prompt, IReadOnlyList<MusicPoetryMessage> messages) => Task.FromResult(prompt.Length / 4 + messages.Sum(m => m.Text.Length / 4));
        var disabled = await MusicPoetryContextWindow.BuildAsync(session, "guide", 8192, 1500, Count, false);
        Assert.IsFalse(disabled.System.Contains("lookup status", StringComparison.Ordinal));
        session.UsePersonalLyricsLibrary = true;
        var enabled = await MusicPoetryContextWindow.BuildAsync(session, "guide", 8192, 1500, Count, false);
        StringAssert.Contains(enabled.System, "Unit 83"); StringAssert.Contains(enabled.System, "no usable personal examples");
        Assert.IsLessThan(8192 - 1500, enabled.Tokens);
        var json = JsonSerializer.Serialize(session);
        Assert.IsFalse(json.Contains("PendingLibraryArtist", StringComparison.Ordinal));
        var oldHistory = JsonSerializer.Deserialize<LyricExample>("{\"Id\":\"id\",\"Revision\":1,\"Title\":\"title\",\"Section\":\"Verse\",\"Text\":\"old\",\"Score\":0.5}");
        Assert.IsNotNull(oldHistory); Assert.AreEqual("", oldHistory.Artist);
    }
}
