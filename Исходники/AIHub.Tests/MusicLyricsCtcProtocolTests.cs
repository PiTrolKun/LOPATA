using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicLyricsCtcProtocolTests
{
    private const string Source = "[Verse 1]\r\n  Кот зовёт солнце.  \r\n\r\n[Chorus]\r\nКот зовёт солнце.\r\n[Outro]";

    private static string Result(string lyrics = Source, int[]? indices = null, string? provider = null,
        string? sourceHash = null, double duration = 20, string? state = null, double firstStart = 2)
    {
        indices ??= [0, 1];
        return JsonSerializer.Serialize(new {
            schema = 1, provider = provider ?? MusicLyricsAlignmentService.Provider,
            version = MusicLyricsAlignmentService.ProviderVersion,
            source_hash = sourceHash ?? MusicTimedLyrics.HashLyrics(lyrics), duration_seconds = duration,
            state = state ?? (indices.Length == 0 ? "unavailable" : indices.Length == MusicLyricsCtcProtocol.SourceRows(lyrics).Length ? "complete" : "partial"),
            lines = indices.Select((index, n) => new { index, start = n == 0 ? firstStart : 12, end = n == 0 ? 5d : 16d })
        });
    }

    [TestMethod]
    public void WholeLinesRetainUtf16OffsetsAndRepeatedOccurrences()
    {
        var rows = MusicLyricsCtcProtocol.SourceRows(Source);
        Assert.HasCount(2, rows);
        Assert.AreEqual(rows[0].Text, rows[1].Text);
        Assert.IsGreaterThan(rows[0].SourceStart, rows[1].SourceStart);
        var result = MusicLyricsCtcProtocol.Read(Result(), Source, 20);
        Assert.AreEqual("complete", result.State);
        Assert.HasCount(2, result.Lines);
        foreach (var line in result.Lines)
            Assert.AreEqual("Кот зовёт солнце.", MusicTimedLyrics.Normalize(Source).Substring(line.SourceStart, line.SourceLength));
    }

    [TestMethod]
    public void MissingAcousticRowIsNotFilledOrReportedComplete()
    {
        var result = MusicLyricsCtcProtocol.Read(Result(indices:[1]), Source, 20);
        Assert.AreEqual("partial", result.State);
        Assert.HasCount(1, result.Lines);
        Assert.AreEqual(MusicLyricsCtcProtocol.SourceRows(Source)[1].SourceStart, result.Lines[0].SourceStart);
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(indices:[1], state:"complete"), Source, 20));
    }

    [TestMethod]
    public void ForeignTextModelAndTimebaseAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(sourceHash:new string('0', 64)), Source, 20));
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(provider:"Whisper"), Source, 20));
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(duration:21), Source, 20));
    }

    [TestMethod]
    public void DuplicateSourceOccurrenceAndOutOfRangeTimeAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(indices:[0, 0]), Source, 20));
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(indices:[2]), Source, 20));
        Assert.Throws<InvalidDataException>(() => MusicLyricsCtcProtocol.Read(Result(firstStart:-1), Source, 20));
    }

    [TestMethod]
    public void SourceRowsKeepNonBmpCharactersAndAccentMarksWithoutWorkerOffsets()
    {
        const string source = "[Verse]\n🎵 Кот пое́т.\nКот поёт.";
        var rows = MusicLyricsCtcProtocol.SourceRows(source);
        Assert.HasCount(2, rows);
        foreach (var row in rows) Assert.AreEqual(row.Text, source.Substring(row.SourceStart, row.SourceLength));
    }

    [TestMethod]
    public void KnownWhisperCachesUpgradeButForeignAndCurrentProviderCachesStayUsable()
    {
        MusicTimedLyrics Timing(string provider, string version) => new() {
            JobId = Guid.NewGuid().ToString("N"), Variant = 0, SourceLyrics = Source,
            LyricsHash = MusicTimedLyrics.HashLyrics(Source), AudioPayloadHash = new string('0',64), DurationSeconds = 20,
            Provider = provider, ProviderVersion = version
        };
        Assert.IsTrue(MusicLyricsSynchronization.NeedsUpgrade(Timing(MusicLyricsAlignmentService.LegacyProvider,"1.9.1/small/matcher-2")));
        Assert.IsTrue(MusicLyricsSynchronization.NeedsUpgrade(Timing(MusicLyricsAlignmentService.LegacyProvider,"1.9.1/small/matcher-1")));
        Assert.IsFalse(MusicLyricsSynchronization.NeedsUpgrade(Timing(MusicLyricsAlignmentService.Provider,MusicLyricsAlignmentService.ProviderVersion)));
        Assert.IsFalse(MusicLyricsSynchronization.NeedsUpgrade(Timing("native-generated-timings","1")));
    }

    [TestMethod]
    public void CpuFallbackRequiresGpuHardwareFailureAndDoesNotMaskInputErrorsOrCancellation()
    {
        Assert.IsTrue(MusicLyricsDevicePolicy.CanRetryOnCpu("cuda:0",42,"CUDA out of memory",false));
        Assert.IsTrue(MusicLyricsDevicePolicy.CanRetryOnCpu("xpu:0",42,"ZE_RESULT_ERROR_UNSUPPORTED_FEATURE",false));
        Assert.IsFalse(MusicLyricsDevicePolicy.CanRetryOnCpu("cpu",42,"out of memory",false));
        Assert.IsFalse(MusicLyricsDevicePolicy.CanRetryOnCpu("cuda:0",42,"CUDA out of memory",true));
        Assert.IsFalse(MusicLyricsDevicePolicy.CanRetryOnCpu("cuda:0",1,"CUDA out of memory",false));
        Assert.IsFalse(MusicLyricsDevicePolicy.CanRetryOnCpu("cuda:0",42,"FileNotFoundError: out of memory",false));
        Assert.IsFalse(MusicLyricsDevicePolicy.CanRetryOnCpu("cuda:0",42,"DefaultCPUAllocator: out of memory",false));
        Assert.IsFalse(MusicLyricsDevicePolicy.CanRetryOnCpu("cuda:0",42,"JSONDecodeError",false));
    }
}
