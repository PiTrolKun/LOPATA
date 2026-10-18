using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Repair legacy Waiting entries only when the matching native log proves a completed failed process.</summary>
public static class ImageGenerationFailureRecovery
{
    private static readonly JsonSerializerOptions InputOptions = new()
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static bool Recover(BackgroundOperationController controller)
    {
        if (controller.IsRunning || controller.State is not { Kind: ImageGenerationCatalog.BackgroundKind,
            Phase: BackgroundOperationPhase.Waiting, RequiresDecision: true, UserPaused: false,
            Detail: "InvalidOperationException", Project: not null } state) return false;
        try {
            var request = state.Input.Deserialize<ImageGenerationRequest>(InputOptions);
            if (request is null) return false;
            ImageGenerationCatalog.Validate(request);
            if (!Path.GetFullPath(request.SessionDirectory).Equals(Path.GetFullPath(state.Project), StringComparison.OrdinalIgnoreCase)) return false;
            Lopata.Updates.SafeUpdatePath.RejectLinks(request.SessionDirectory);
            var sessionPath = Path.Combine(request.SessionDirectory, "session.json");
            Lopata.Updates.SafeUpdatePath.RejectLinks(sessionPath);
            var turn = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.SingleOrDefault(t => t.Request.Id == request.Id);
            // Optional fields added since an older checkpoint (for example
            // EngineSetId=null for legacy001) must not change semantic identity.
            // Reject unknown parameters before normalization rather than discard them.
            if (turn is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(turn.Request),
                JsonSerializer.SerializeToElement(request))) return false;
            for (var index = 0; index < request.Seeds.Length; index++) {
                if (ImageGenerationRuntime.IsValidImage(ImageGenerationSessionStore.ResultPath(request, index), request)) continue;
                var logPath = Path.Combine(request.SessionDirectory, request.Id + "_" + index + ".runtime.log");
                Lopata.Updates.SafeUpdatePath.RejectLinks(logPath);
                if (!File.Exists(logPath) || new FileInfo(logPath).Length > 262144) continue;
                var log = File.ReadAllText(logPath);
                // Every managed attempt records its final native exit. The last exit accounts for a CPU retry.
                var exit = log.Split('\n').LastOrDefault(line => line.StartsWith("Exit: 0x", StringComparison.Ordinal))?.Trim();
                if (exit is null || !uint.TryParse(exit.AsSpan(8), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var code) || code == 0) continue;
                if (controller.FailWaiting(state.Id, "Generation.RuntimeError")) {
                    OwnedProcessRegistry.Log("background_native_failure_recovered", "ImageGeneration", detail: $"operation={state.Id}; exit=0x{code:X8}");
                    return true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException) {
            OwnedProcessRegistry.Log("background_failure_recovery_skipped", "ImageGeneration", detail: error.GetType().Name);
        }
        return false;
    }
}
