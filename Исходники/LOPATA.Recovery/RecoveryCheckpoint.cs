using System.Security.Cryptography;
using System.Text.Json;

namespace Lopata.Recovery;

internal sealed record RecoveryCheckpoint(string OperationId, string? Checkpoint, string? ProjectReceipt)
{
    internal static RecoveryCheckpoint? Capture(string stateRoot)
    {
        var directory = Path.Combine(stateRoot, "Background");
        var selected = Path.Combine(directory, "operation.json");
        try
        {
            using var primary = Read(selected);
            if (primary is null || !primary.RootElement.TryGetProperty("id", out var key)
                || key.ValueKind != JsonValueKind.String || !Guid.TryParseExact(key.GetString(), "N", out var id))
                return null;

            using var actual = Read(Path.Combine(directory, "Jobs", id.ToString("N") + ".json"));
            var state = (actual ?? primary).RootElement;
            if (state.TryGetProperty("phase", out var phase)
                && phase.GetString() is "Completed" or "Failed" or "Canceled")
                return null;
            var checkpoint = state.TryGetProperty("checkpoint", out var stored) ? stored : default;
            return new RecoveryCheckpoint(id.ToString("N"),
                checkpoint.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : checkpoint.GetRawText(),
                FingerprintReceipt(checkpoint));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return null; }
    }

    private static JsonDocument? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return stream.Length <= 2 * 1024 * 1024 ? JsonDocument.Parse(stream) : null;
    }

    private static string? FingerprintReceipt(JsonElement checkpoint)
    {
        if (checkpoint.ValueKind != JsonValueKind.Object || !checkpoint.TryGetProperty("JobFile", out var file)
            || file.ValueKind != JsonValueKind.String || file.GetString() is not { } path
            || !Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "job.json")
            return null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return stream.Length <= 2 * 1024 * 1024 ? Convert.ToHexString(SHA256.HashData(stream)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
