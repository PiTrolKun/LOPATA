using AIHub.Models;

namespace AIHub.Services.VoiceNotifications;

internal sealed record VoiceEventRequest(string Value, string Scope, string[] Contexts, string Family = "", bool Terminal = false, string Occurrence = "", int? Group = null);

/// <summary>Non-blocking adapters publish semantic events; a missing host never affects the operation.</summary>
internal static class VoiceNotificationEvents
{
    private sealed record WorkScope(string Id, string[] Contexts);
    private static readonly AsyncLocal<WorkScope?> Work = new();
    private static readonly AsyncLocal<int> Suppressed = new();
    internal static Func<VoiceEventRequest, bool>? Deliver { get; set; }
    internal static Func<string, string[], bool>? CanSpeak { get; set; }
    internal static Func<string, bool>? KnowsKey { get; set; }
    internal static bool HasOperation => Work.Value is not null;
    private sealed class Exit(Action action) : IDisposable { public void Dispose() => action(); }
    internal static IDisposable Suppress()
    { var before = Suppressed.Value; Suppressed.Value++; return new Exit(() => Suppressed.Value = before); }
    internal static IDisposable Operation(string id, string? project)
    { var before = Work.Value; Work.Value = new(id, VoiceNotificationContext.Project(project)); return new Exit(() => Work.Value = before); }
    internal static bool Publish(string value, string? scope = null, string[]? contexts = null, string family = "", bool terminal = false, string occurrence = "", int? group = null)
    {
        if (Suppressed.Value > 0 || Deliver is not { } deliver) return false;
        var request = new VoiceEventRequest(value, scope ?? Work.Value?.Id ?? "application", contexts ?? Work.Value?.Contexts ?? [], family, terminal, occurrence, group);
        try { return deliver(request); } catch { return false; }
    }
    internal static void Background(BackgroundOperationState before, BackgroundOperationState state)
    {
        if (before.Phase == state.Phase && before.Detail == state.Detail) return;
        var key = state.Phase switch
        {
            BackgroundOperationPhase.Completed => "Tray.ResultReady",
            BackgroundOperationPhase.Failed => "RagBooks.State.Failed",
            BackgroundOperationPhase.Canceled => "RagBooks.State.Canceled",
            BackgroundOperationPhase.Paused => "RagBooks.State.Paused",
            BackgroundOperationPhase.Waiting when state.RequiresDecision => state.Detail is { } detail && KnowsKey?.Invoke(detail) == true
                ? detail : state.Detail is not null ? "RagBooks.State.Failed" : "RagBooks.State.Waiting",
            _ => null
        };
        if (key is null) return;
        Publish(key, state.Id, VoiceNotificationContext.Project(state.Project), "operation",
            state.Phase is BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled,
            group: state.Phase == BackgroundOperationPhase.Failed || state.RequiresDecision && state.Detail is not null ? 1 : 2);
    }
    internal static void ModelLog(string kind, string component)
    {
        if (component.StartsWith("VoiceNotifications", StringComparison.Ordinal) || component == "Music.Audio") return;
        var key = "model-log:" + kind;
        if (KnowsKey?.Invoke(key) == true) Publish(key);
    }
}
