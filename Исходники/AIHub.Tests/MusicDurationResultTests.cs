using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicDurationResultTests
{
    [TestMethod]
    [DataRow("[AR] Semantic song 0: 15000 tokens (truncated)", "duration_limit")]
    [DataRow("[AR] Semantic song 0: 8999 tokens", "natural_end")]
    [DataRow("[AR] Score song 0: 4096 tokens (truncated)", null)]
    [DataRow("[AR] Semantic 9000/9000", null)]
    [DataRow("[AR] Semantic song 1: 15000 tokens (truncated)", null)]
    [DataRow("[AR] Semantic song 0: 7200 tokens (other)", null)]
    public void OnlyTheWorkerSemanticSummaryConfirmsHowTheSongEnded(string log, string? reason)
    {
        Assert.AreEqual(reason, MusicStudioOutput.SemanticStopReason(log));
    }

    [TestMethod]
    public void StudioReceiptUsesTheSmallerNativeLimitAndKeepsTheConfirmedReason()
    {
        using var request = JsonDocument.Parse("""{"duration":600,"semantic_sampling":{"max_tokens":12000}}""");
        var receipt = JsonSerializer.Serialize(new {
            duration_policy = MusicStudioOutput.DurationPolicy(request.RootElement, MusicDurationResult.LimitReached)
        });

        var duration = MusicDurationResult.FromReceipt(receipt);

        Assert.AreEqual<double?>(480, duration.EffectiveLimitSeconds);
        Assert.AreEqual("duration_limit", duration.StopReason);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"duration_seconds\":360}")]
    [DataRow("{\"duration_policy\":{\"effective_limit_seconds\":-1,\"stop_reason\":\"unknown\"}}")]
    [DataRow("not a receipt")]
    public void ActualDurationAndLegacyReceiptsCannotImplyTruncation(string receipt)
    {
        Assert.AreEqual(default, MusicDurationResult.FromReceipt(receipt));
    }

    [TestMethod]
    public void CompletedTrackAndExportKeepDistinctRequestedEffectiveAndActualValues()
    {
        var result = new MusicGenerationVariant(1, 2, "song.flac") {
            DurationSeconds = 480,
            EffectiveDurationLimitSeconds = 480,
            DurationStopReason = MusicDurationResult.LimitReached
        };
        var profile = MusicStudioRuntime.Current;
        var job = new MusicGenerationJob("job", "models", "output", "song", "rock", "lyrics", 600, DateTime.Now, [result]) {
            DurationAutomatic = true,
            Variation = MusicStudioRuntime.Variation,
            Expert = MusicModelVariants.Defaults(MusicStudioRuntime.Variation),
            RuntimeRevision = profile.Revision,
            RuntimePack = profile.Pack
        };

        var tags = MusicSongMetadata.Create(job, result, 0);

        Assert.AreEqual("auto", tags["LOPATA_DURATION_REQUEST_SECONDS"]);
        Assert.AreEqual("480", tags["LOPATA_DURATION_EFFECTIVE_LIMIT_SECONDS"]);
        Assert.AreEqual("480", tags["LOPATA_DURATION_ACTUAL_SECONDS"]);
        Assert.AreEqual("duration_limit", tags["LOPATA_DURATION_STOP_REASON"]);
        StringAssert.Contains(tags["LOPATA_PARAMETERS"], "semantic_output_limit=12000");
        Assert.AreEqual("duration_limit", MusicGenerationJobs.Track(job, result, 0).DurationStopReason);

        var unknownEnd = result with { DurationStopReason = null };
        Assert.IsFalse(MusicSongMetadata.Create(job, unknownEnd, 0).ContainsKey("LOPATA_DURATION_STOP_REASON"));
    }
}
