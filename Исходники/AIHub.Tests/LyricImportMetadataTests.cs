using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Tests;

[TestClass]
public sealed class LyricImportMetadataTests
{
    [TestMethod]
    public void EditorDraftUsesTheCurrentProjectWishesWithoutInventingAuthorship()
    {
        var preferences = new MusicPreferences(); var genre = MusicWishCatalog.Genres[0];
        preferences.Select("genres", [genre.Id, "indie rock"]); preferences.Select("mood", ["calm"]);
        preferences.Performers.Add(new("lead", "Lead", "", "", "russian", [], [], "Soft delivery"));
        var snapshot = new MusicProjectSnapshot { Title = "Ночная дорога", Artist = "Test artist", Comment = "A private song note",
            Lyrics = "Exact editor text", Wishes = MusicWishSnapshot.Capture(preferences) };
        var draft = LyricLibraryDraft.FromEditor(snapshot, snapshot.Lyrics, key => key);
        Assert.AreEqual(snapshot.Title, draft.Title); Assert.AreEqual(snapshot.Artist, draft.Artist);
        Assert.AreEqual(genre.Name + "; indie rock", draft.Genre); StringAssert.Contains(draft.Style, "calm");
        StringAssert.Contains(draft.Style, "Soft delivery"); Assert.IsFalse(draft.Style.Contains(genre.Id, StringComparison.Ordinal));
        Assert.AreEqual("ru", draft.Language); Assert.AreEqual(snapshot.Comment, draft.Notes);
        Assert.AreEqual("", draft.Author); Assert.AreEqual(0, draft.Models.Length); Assert.AreEqual(snapshot.Lyrics, draft.Text);
        draft.Validate(); Assert.AreEqual(genre.Id, snapshot.Wishes.Selections["genres"][0]);
    }
    [TestMethod]
    public void MissingAndMixedLanguagesStayUnknownRatherThanUsingTheInterfaceLanguage()
    {
        var empty = LyricLibraryDraft.FromEditor(new() { Lyrics = "text" }, "text", key => "Untitled");
        Assert.AreEqual("und", empty.Language); Assert.AreEqual("Untitled", empty.Title);
        var p = new MusicPreferences();
        p.Performers.Add(new("one", "One", "", "", "english", [], [], ""));
        p.Performers.Add(new("two", "Two", "", "", "russian", [], [], ""));
        var mixed = LyricLibraryDraft.FromEditor(new() { Wishes = MusicWishSnapshot.Capture(p) }, "text", key => "Untitled");
        Assert.AreEqual("und", mixed.Language);
        Assert.AreEqual("en", LyricLibraryDraft.Language("English"));
        Assert.AreEqual("ru", LyricLibraryDraft.Language("русский"));
        Assert.AreEqual("pt-BR", LyricLibraryDraft.Language("pt-BR"));
    }
    [TestMethod]
    public void FillingSharedDetailsNeverReplacesOtherSongsTitlesTextOrExistingMetadata()
    {
        var source = new LyricEntry { Title = "Source", Text = "Source text", Artist = "Shared artist", Author = "Shared author",
            Genre = "rock", Style = "spoken delivery", Language = "ru", Tags = "night", Notes = "Only for source" };
        var target = new LyricEntry { Title = "Other song", Text = "Original other text", Artist = "Original artist",
            Language = "fr", Notes = "Original note" };
        var merged = LyricLibraryDraft.FillEmptyMetadata(target, source);
        Assert.AreEqual(target.Title, merged.Title); Assert.AreEqual(target.Text, merged.Text); Assert.AreEqual(target.Id, merged.Id);
        Assert.AreEqual(target.Artist, merged.Artist); Assert.AreEqual("fr", merged.Language); Assert.AreEqual(target.Notes, merged.Notes);
        Assert.AreEqual(source.Genre, merged.Genre); Assert.AreEqual(source.Style, merged.Style); Assert.AreEqual(source.Author, merged.Author);
    }
    [TestMethod]
    public void StyleSurvivesExternalImportSearchPortableExportAndArtistExamples()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-import-meta-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "songs.jsonl");
            File.WriteAllText(source, JsonSerializer.Serialize(new { title = "New song", lyrics = "[Verse]\nA new story",
                artist = "Unit 83", genre = "folk", styles = new[] { "staccato", "whispered" }, notes = "Original notes", tags = "night" }));
            var original = File.ReadAllBytes(source); var parsed = LyricLibraryImport.Read(source, CancellationToken.None).Single();
            Assert.AreEqual("staccato; whispered", parsed.Style); Assert.AreEqual("folk", parsed.Genre); Assert.AreEqual("Original notes", parsed.Notes);
            var store = new LyricLibraryStore(Path.Combine(root, "library")); var saved = store.Save(parsed).Entry;
            Assert.AreEqual(saved.Id, store.Search("staccato").Single().Id);
            Assert.AreEqual(saved.Style, LyricSections.Split(saved).Single().Style);
            Assert.AreEqual(saved.Style, LyricArtistSelection.Resolve([saved], "Like Unit 83")!.Examples("English").Single().Style);
            var exported = Path.Combine(root, "export.json"); LyricLibraryImport.Export(store, exported);
            Assert.AreEqual(saved.Style, LyricLibraryImport.Read(exported, CancellationToken.None).Single().Style);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            var old = JsonSerializer.Deserialize<LyricEntry>("{\"Title\":\"Old song\",\"Text\":\"Original lines\"}")!;
            Assert.AreEqual("", old.Style); old.Validate();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
