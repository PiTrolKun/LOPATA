using System.Text.Json;

namespace AIHub.Services;

internal readonly record struct MusicDurationResult(double? EffectiveLimitSeconds, string? StopReason)
{
    internal const string LimitReached = "duration_limit";
    internal const string NaturalEnd = "natural_end";

    internal static MusicDurationResult FromReceipt(string? receipt)
    {
        if (string.IsNullOrEmpty(receipt)) return default;
        try {
            using var json = JsonDocument.Parse(receipt);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("duration_policy", out var result)
                || result.ValueKind != JsonValueKind.Object) return default;
            double? limit = result.TryGetProperty("effective_limit_seconds", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
                && double.IsFinite(seconds) && seconds > 0 ? seconds : null;
            var reason = result.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String
                ? stop.GetString() : null;
            return new(limit, reason is LimitReached or NaturalEnd ? reason : null);
        }
        catch (JsonException) { return default; }
    }
}
