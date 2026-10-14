using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Release only legacy DiffRhythm requests that failed section validation before any worker stage.</summary>
public static class MusicDiffRhythmFailureRecovery
{
    public static bool Recover(BackgroundOperationController controller, MusicGenerationJobs jobs)
    {
        if (controller.IsRunning || controller.State is not { Kind: MusicGenerationRunner.BackgroundKind,
            Phase: BackgroundOperationPhase.Waiting, RequiresDecision: true, UserPaused: false,
            Detail: "InvalidDataException", Project: not null } state) return false;
        try {
            if (!state.Input.TryGetProperty("JobId", out var input) || input.GetString() is not { } id || id != state.Project) return false;
            if (state.Checkpoint is { } checkpoint && (!checkpoint.TryGetProperty("JobId", out var saved) || saved.GetString() != id
                || !checkpoint.TryGetProperty("Completed", out var completed) || !completed.TryGetInt32(out var count) || count != 0)) return false;
            var folder = jobs.Folder(id);
            Lopata.Updates.SafeUpdatePath.RejectLinks(folder);
            Lopata.Updates.SafeUpdatePath.RejectLinks(Path.Combine(folder, "job.json"));
            var job = jobs.Load(id);
            if (!MusicDiffRhythmCatalog.IsDiff(job.Variation) || !MusicDiffRhythmRequest.HasUnsupportedLegacySection(job.Lyrics)) return false;
            if (job.Variants.Any(v => v.Completed || v.AudioFile is not null || v.AudioHash is not null || v.PlanFile is not null
                || v.ResultHash is not null || v.ExecutionReceipt is not null || File.Exists(v.ResultPath)
                || v.AdditionalPath is not null && File.Exists(v.AdditionalPath))) return false;
            // Build used to run before writing a request or starting Python; other stage evidence is protected.
            if (Directory.EnumerateDirectories(folder).Any()
                || Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path) is not ("job.json" or "job.json.schema1.bak"))) return false;
            if (!controller.FailWaiting(state.Id, "Music.Generation.Failed")) return false;
            OwnedProcessRegistry.Log("background_input_failure_recovered", "Music.DiffRhythm2", detail: $"operation={state.Id}; job={id}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException) {
            OwnedProcessRegistry.Log("background_failure_recovery_skipped", "Music.DiffRhythm2", detail: error.GetType().Name);
        }
        return false;
    }
}
