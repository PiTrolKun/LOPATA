using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Studio 3.6 keeps native float audio. Its own normalizer preserves our former peak_clip contract.</summary>
internal static class MusicStudioOutput
{
    internal static string? SemanticStopReason(string log)
    {
        const string prefix = "[AR] Semantic song 0: ";
        const string tokens = " tokens";
        const string truncated = " (truncated)";
        if (!log.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var counter = log.AsSpan(prefix.Length);
        var limitReached = counter.EndsWith(truncated, StringComparison.Ordinal);
        if (limitReached) counter = counter[..^truncated.Length];
        if (!counter.EndsWith(tokens, StringComparison.Ordinal)
            || !int.TryParse(counter[..^tokens.Length], NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || frames <= 0) return null;
        return limitReached ? MusicDurationResult.LimitReached : MusicDurationResult.NaturalEnd;
    }

    internal static object DurationPolicy(JsonElement effective, string? stopReason)
    {
        double? limit = null;
        if (effective.ValueKind == JsonValueKind.Object) {
            if (effective.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                && duration.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0)
                limit = seconds;
            if (effective.TryGetProperty("semantic_sampling", out var sampling) && sampling.ValueKind == JsonValueKind.Object
                && sampling.TryGetProperty("max_tokens", out var maximum) && maximum.ValueKind == JsonValueKind.Number
                && maximum.TryGetInt32(out var frames) && frames > 0)
                limit = Math.Min(limit ?? double.PositiveInfinity, frames / 25d);
        }
        return new { effective_limit_seconds = limit, stop_reason = stopReason };
    }

    public static async Task<JsonElement> NormalizeAndDecodeAsync(MusicStudioLease lease, JsonElement song,
        int peakClip, string destination, Action<string> log, CancellationToken token)
    {
        var id = song.GetProperty("id").GetString();
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("Invalid Studio song identifier.");
        // Do not encode the original float to integer first: fitting_gain is different from percentile normalization.
        log("[Studio] normalize; peak_clip=" + peakClip.ToString(CultureInfo.InvariantCulture));
        await lease.SendAsync("v1/library/songs/" + id + "/process", new { normalize = new { peak_clip = peakClip } }, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        JsonElement completed;
        try { while (true) {
            var status = await lease.SendAsync("v1/processing", null, deadline.Token);
            var run = status.GetProperty("run");
            if (run.ValueKind != JsonValueKind.Object || run.GetProperty("song_id").GetString() != id)
                throw new InvalidDataException("Studio normalization belongs to a different song.");
            if (run.GetProperty("done").GetBoolean()) {
                if (run.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    throw new InvalidDataException("Studio normalization failed: " + error.GetString());
                if (!run.GetProperty("preview_ready").GetBoolean()
                    || run.GetProperty("request").GetProperty("normalize").GetProperty("peak_clip").GetInt32() != peakClip
                    || !run.GetProperty("stages").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["normalize"]))
                    throw new InvalidDataException("Studio normalization receipt mismatch.");
                completed = run.Clone(); break;
            }
            await Task.Delay(200, deadline.Token);
        } }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) {
            throw new TimeoutException("Studio normalization exceeded two minutes.");
        }
        var flac = destination + ".normalized.flac";
        try {
            await lease.CopyAudioAsync("/v1/processing/preview", flac, token);
            var runtime = MusicAudioRuntime.Default;
            await runtime.PrepareAsync(token);
            using var probe = JsonDocument.Parse(await runtime.RunAsync(true, ["-v", "error", "-show_entries",
                "stream=codec_name,channels,sample_rate,duration_ts,time_base", "-of", "json", flac], token));
            var audio = probe.RootElement.GetProperty("streams").EnumerateArray().Single();
            if (audio.GetProperty("codec_name").GetString() != "flac" || audio.GetProperty("channels").GetInt32() != 2
                || audio.GetProperty("sample_rate").GetString() != "48000" || audio.GetProperty("time_base").GetString() != "1/48000")
                throw new InvalidDataException("Studio normalized audio is not stereo FLAC at 48000 Hz.");
            var frames = audio.GetProperty("duration_ts").GetInt64();
            if (frames <= 0) throw new InvalidDataException("Studio normalized audio is empty.");
            await MusicAudioRuntime.Default.DecodeAsync(flac, destination, token);
            if (Math.Abs(MusicWaveFile.ReadDuration(destination).TotalSeconds - frames / 48000d) > 1 / 48000d)
                throw new InvalidDataException("Studio audio conversion changed its duration.");
        }
        finally { if (File.Exists(flac)) File.Delete(flac); }
        return completed;
    }
}
