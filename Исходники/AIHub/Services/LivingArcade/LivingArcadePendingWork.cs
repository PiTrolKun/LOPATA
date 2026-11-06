using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

internal sealed record LivingArcadePendingOperation(BackgroundOperationState State,
    LivingArcadeModelPaths Paths, string? RunId);

/// <summary>Find durable arcade work without preparing models or overriding a user's pause.</summary>
internal static class LivingArcadePendingWork
{
    internal static LivingArcadePendingOperation? Find(BackgroundOperationController controller, string? runId = null,
        string? shownOperationId = null)
    {
        var selectedId = controller.State?.Id;
        var pending = controller.PendingOperations.Select(Read).OfType<LivingArcadePendingOperation>().ToArray();
        // A reopened page may show another run's parked work while retaining the active game's run ID.
        if (shownOperationId is not null && pending.FirstOrDefault(work => work.State.Id == shownOperationId) is { } shown)
            return shown;
        return pending
            .Where(work => runId is null || work.RunId is null || work.RunId == runId)
            .OrderByDescending(work => work.State.Id == selectedId)
            .ThenByDescending(work => work.State.UpdatedUtc)
            .ThenBy(work => work.State.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static int DiscardDeleted(BackgroundOperationController controller, LivingArcadeRunStore store)
    {
        var discarded = 0;
        foreach (var work in controller.PendingOperations.Select(Read).OfType<LivingArcadePendingOperation>())
        {
            // A missing or damaged manifest is not proof of intentional deletion. Never load it here.
            if (work.RunId is not { } id || !store.IsDeleted(id) || controller.HasLiveOperation(work.State.Id)) continue;
            try { controller.DiscardPending(work.State.Id); }
            catch (InvalidOperationException) when (controller.HasLiveOperation(work.State.Id))
            { continue; } // A concurrent explicit resume acquired the worker; its own cancellation must retire it.
            if (controller.PendingOperations.All(state => state.Id != work.State.Id)) discarded++;
        }
        return discarded;
    }

    private static LivingArcadePendingOperation? Read(BackgroundOperationState state)
    {
        try
        {
            LivingArcadeModelPaths? paths;
            string? runId;
            switch (state.Kind)
            {
                case LivingArcadeGeneration.WarmupKind:
                    var warmup = state.Input.Deserialize<LivingArcadeWarmupRequest>();
                    paths = warmup?.Paths; runId = null;
                    break;
                case LivingArcadeGeneration.FirstEventsKind:
                    var first = state.Input.Deserialize<LivingArcadeFirstEventsRequest>();
                    paths = first?.Paths; runId = first?.RunId;
                    break;
                case LivingArcadeGeneration.Kind:
                    var generation = state.Input.Deserialize<LivingArcadeGenerationRequest>();
                    paths = generation?.Paths; runId = generation?.RunId;
                    break;
                default: return null;
            }
            if (paths is null || string.IsNullOrWhiteSpace(paths.Main) || string.IsNullOrWhiteSpace(paths.Artist)
                || state.Kind != LivingArcadeGeneration.WarmupKind && !Guid.TryParseExact(runId, "N", out _)) return null;
            return new(state, paths, runId);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
