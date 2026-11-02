using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services.SongClip;

/// <summary>Legacy worker requests omit the new plan entirely; explicit malformed plans remain errors.</summary>
internal static class SongClipWorkerRequest
{
    internal static void Write(string path, object payload, SongClipVisualPlan? plan)
    {
        var request = JsonSerializer.SerializeToNode(payload) as JsonObject
            ?? throw new InvalidDataException("Invalid song-clip worker request.");
        if (plan is null) request.Remove("visualPlan");
        else request["visualPlan"] = JsonSerializer.SerializeToNode(plan);
        // Null means a historical single-reference contract. Do not add keys
        // that would change its durable request identity merely by upgrading.
        foreach (var key in new[] { "references", "referencePrompts" })
            if (request.TryGetPropertyValue(key, out var value) && value is null) request.Remove(key);
        SongClipStore.Atomic(path, request);
    }
}
