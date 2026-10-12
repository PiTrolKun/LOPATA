using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicPoetryFlexibleTests
{
    [TestMethod]
    public void FieldsDetermineEffectsWithMissingOrIncompleteLegacyDeclarations()
    {
        var before = new MusicPoetrySession { Lyrics = "Old poem", Parameters = new(MusicStudioRuntime.Variation, "Pop") };
        foreach (var raw in new[] {
            """{"chat":"Идея!","song":"[Verse]\nНовая песня","parameters":"[Music]\nБлюз"}""",
            """{"chat":"Идея!","song":"[Verse]\nНовая песня","parameters":"[Music]\nБлюз","variation":null,"actions":["song"]}""" })
        {
            var checkedReply = MusicPoetryReplyGuard.Check(before, MusicPoetryProtocol.Parse(raw));
            Assert.IsNull(checkedReply.Error);
            CollectionAssert.AreEqual(new[] { "song", "parameters" }, checkedReply.Changes);
            CollectionAssert.AreEqual(checkedReply.Changes, checkedReply.Reply.Actions);
            before.Apply(checkedReply.Reply);
            Assert.AreEqual("[Verse]\nНовая песня", before.Lyrics);
            Assert.AreEqual(MusicStudioRuntime.Variation, before.Parameters.Variation);
            before.Lyrics = "Old poem"; before.Parameters = new(MusicStudioRuntime.Variation, "Pop");
        }
        var discussion = MusicPoetryReplyGuard.Check(before, MusicPoetryProtocol.Parse("""{"chat":"Давай обсудим настроение"}"""));
        Assert.IsNull(discussion.Error); Assert.AreEqual(0, discussion.Changes.Length);
    }

    [TestMethod]
    public void FlexibilityCannotHideAnEngineChangeUnknownFieldOrBrokenJson()
    {
        foreach (var raw in new[] {
            """{"chat":"","song":"New","parameters":null,"variation":"ace-step-1.5-xl-turbo"}""",
            """{"chat":"","song":"New","actions":["variation"]}""",
            """{"chat":"","song":"New","shell":"delete"}""",
            """{"chat":"","song":"New","song":"Duplicate"}""",
            """{"chat":"","song":42}""" })
            Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryProtocol.Parse(raw));
        Assert.Throws<JsonException>(() => MusicPoetryProtocol.Parse("""{"chat":"","song":"Incomplete"""));
        var session = new MusicPoetrySession { Lyrics = "Saved" };
        Assert.AreEqual("Music.Poetry.Missing.song", MusicPoetryReplyGuard.Check(session, new("", "  ", null, null)).Error);
        Assert.AreEqual("Saved", session.Lyrics);
    }

    [TestMethod]
    public void ExplicitMusicAndStructureLabelsOverrideAnIncorrectIdeaHeading()
    {
        const string raw = "[Idea]\nТема: Кот учится говорить человеческий язык.\nСтруктура: Классическая форма.\nМузыка: Блюз (Blues). Стиль: Глубокий. Вокал: Мужской.\nГитара и перкуссия";
        var brief = MusicPoetryBrief.Parse(raw);
        Assert.AreEqual("Тема: Кот учится говорить человеческий язык.", brief.Idea);
        Assert.AreEqual("Структура: Классическая форма.", brief.Structure);
        StringAssert.Contains(brief.Music, "Музыка: Блюз");
        StringAssert.Contains(brief.Music, "Гитара и перкуссия");
        Assert.AreEqual(brief, MusicPoetryBrief.Parse(brief.ToText()));
    }

    [TestMethod]
    public void InlineHeadingsAndBothLanguagesRemainReadableAndRoundTrip()
    {
        var brief = MusicPoetryBrief.Parse("[Idea] Кот учится языку\n[Structure] Два куплета\n[Music] Инди-фолк");
        Assert.AreEqual("Кот учится языку", brief.Idea);
        Assert.AreEqual("Два куплета", brief.Structure); Assert.AreEqual("Инди-фолк", brief.Music);
        var english = MusicPoetryBrief.Parse("Song idea: Cat\nStructure: Two verses\nGenre: Blues\nVocals: Soft");
        Assert.AreEqual("Cat", english.Idea); Assert.AreEqual("Structure: Two verses", english.Structure);
        Assert.AreEqual("Genre: Blues\nVocals: Soft", english.Music);
        Assert.AreEqual(english, MusicPoetryBrief.Parse(english.ToText()));
        var paragraphs = MusicPoetryBrief.Parse("[Idea]\nПервая мысль\n\nВторая мысль\n[Music]\nBlues");
        Assert.AreEqual("Первая мысль\n\nВторая мысль", paragraphs.Idea);
    }

    [TestMethod]
    public void ExactSongCopiesAreRemovedFromBriefWithoutInventingALyricsRevision()
    {
        const string poem = "[Verse 1]\nКот сидит на диване\n\n[Chorus]\nМяу";
        const string brief = "[Idea]\nТема: Кот\nМузыка: Блюз";
        var before = new MusicPoetrySession { Lyrics = poem, Parameters = new(MusicStudioRuntime.Variation, "") };
        var check = MusicPoetryReplyGuard.Check(before, new("", null, brief + "\n\n" + poem, null, ["parameters"]));
        Assert.IsNull(check.Error); CollectionAssert.AreEqual(new[] { "parameters" }, check.Changes);
        Assert.AreEqual(brief, check.Reply.Parameters); Assert.IsNull(check.Reply.Song);
        before.Apply(check.Reply); Assert.AreEqual(poem, before.Lyrics);

        before.Parameters = new(before.Parameters.Variation, brief + "\n\n" + poem);
        var original = before.Parameters.Text;
        var prompt = MusicPoetryProtocol.System(before, "Guidance");
        using var workspace = JsonDocument.Parse(prompt[(MusicPoetryProtocol.Instructions.Length + 1)..]);
        Assert.AreEqual(brief, workspace.RootElement.GetProperty("parameters").GetString());
        Assert.AreEqual(original, before.Parameters.Text);
        var unknownVerse = "[Verse 1]\nИной текст";
        check = MusicPoetryReplyGuard.Check(before, new("", null, brief + "\n" + unknownVerse, null));
        StringAssert.Contains(check.Reply.Parameters!, unknownVerse);
        Assert.IsNull(check.Reply.Song);
    }
}
