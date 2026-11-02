using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipSourceMetadataTests
{
    [TestMethod]
    [DataRow("lyrics")]
    [DataRow("lyrics-xxx")]
    [DataRow("lyrics-rus")]
    [DataRow("LYRICS-Description-eng")]
    [DataRow("UNSYNCEDLYRICS")]
    public void ReadsNativeLyricsIncludingTheLanguageQualifiedId3Name(string key)
    {
        const string text="[Verse]\nПервый куплет\n\n[Chorus]\nПовтор припева";
        const string lrc="[00:02.10]Первый куплет\n[00:10.00]Повтор припева";
        using var probe=JsonDocument.Parse(JsonSerializer.Serialize(new {format=new {tags=new Dictionary<string,string>{[key]=text,[MusicLyricsTimingMetadata.LrcTag]=lrc}}}));
        var result=SongClipSourceMetadata.Parse(probe.RootElement);
        Assert.AreEqual(text,result.Lyrics);Assert.AreEqual(lrc,result.Timings);
    }

    [TestMethod]
    public void ReadsAudioStreamTagsAndIgnoresImageStreamLyrics()
    {
        using var probe=JsonDocument.Parse("""
            {"streams":[
              {"codec_type":"video","tags":{"lyrics":"Unrelated cover image text"}},
              {"codec_type":"audio","tags":{"LYRICS":"Song text from Opus","LOPATA_LRC":"[00:01.00]Song text from Opus"}}
            ]}
            """);
        var result=SongClipSourceMetadata.Parse(probe.RootElement);
        Assert.AreEqual("Song text from Opus",result.Lyrics);Assert.AreEqual("[00:01.00]Song text from Opus",result.Timings);
    }

    [TestMethod]
    public void EmptyOrdinaryLyricsDoNotMaskALanguageQualifiedValue()
    {
        using var probe=JsonDocument.Parse("""{"format":{"tags":{"lyrics":"  ","lyrics-xxx":"Stored source text","LOPATA_LRC":"[00:01.00]Stored source text"}}} """);
        Assert.AreEqual("Stored source text",SongClipSourceMetadata.Parse(probe.RootElement).Lyrics);
    }

    [TestMethod]
    public void PrefersTheOrdinaryLyricsTagAndPreservesExistingLrc()
    {
        using var probe=JsonDocument.Parse("""{"format":{"tags":{"lyrics-eng":"Other translation","lyrics":"Original source","LOPATA_LRC":"[00:01.00]Reviewed timing"}}} """);
        var result=SongClipSourceMetadata.Parse(probe.RootElement);
        Assert.AreEqual("Original source",result.Lyrics);Assert.AreEqual("[00:01.00]Reviewed timing",result.Timings);
    }

    [TestMethod]
    public void FallsBackToValidatedLopataTimingMetadataWithoutLosingSongSections()
    {
        const string source="[Verse]\nСтрока\n[Chorus]\nПрипев";
        var timed=new MusicTimedLyrics{JobId=Guid.NewGuid().ToString("N"),Variant=0,SourceLyrics=source,
            LyricsHash=MusicTimedLyrics.HashLyrics(source),AudioPayloadHash=new string('A',64),DurationSeconds=20,
            Provider="test",ProviderVersion="1",Lines=[new(source.IndexOf("Строка",StringComparison.Ordinal),6,2,4),new(source.IndexOf("Припев",StringComparison.Ordinal),6,10,12)]};
        timed.Validate();
        using var probe=JsonDocument.Parse(JsonSerializer.Serialize(new {format=new {tags=new Dictionary<string,string>{[MusicLyricsTimingMetadata.JsonTag]=JsonSerializer.Serialize(timed)}}}));
        var result=SongClipSourceMetadata.Parse(probe.RootElement);
        Assert.AreEqual(source,result.Lyrics);Assert.AreEqual(MusicLyricsTimingMetadata.Lrc(timed),result.Timings);
    }

    [TestMethod]
    public void InvalidOptionalTimingJsonDoesNotDiscardUsableLyrics()
    {
        using var probe=JsonDocument.Parse("""{"format":{"tags":{"lyrics-xxx":"Keep source","LOPATA_TIMED_LYRICS":"{broken"}}} """);
        var result=SongClipSourceMetadata.Parse(probe.RootElement);
        Assert.AreEqual("Keep source",result.Lyrics);Assert.AreEqual("",result.Timings);
    }
}
