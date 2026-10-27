using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Suggest diagnostics for unknown/component failures without replacing their original error.</summary>
public static class ComponentAuditFailures
{
    private static readonly ConditionalWeakTable<Exception, object> Seen = new();
    private static readonly object Gate = new();
    private static DateTime _lastNotice;
    public static event Action<Exception>? Failure;
    public static bool IsEligible(Exception error)
    {
        IEnumerable<Exception> errors = error is AggregateException group ? group.Flatten().InnerExceptions : [error];
        foreach (var current in errors)
        {
            // A rejected literal-preservation response is a known model-output error, not an installation failure.
            if (current.Message is "VideoPrep.TranslationLiteralLost" or "VideoPrep.AiContextLimit" or "VideoPrep.ShotsInvalidReply") return false;
            if (current is OperationCanceledException or OutOfMemoryException or HttpRequestException or SocketException or ArgumentException
                or BackgroundOperationWaitingException or ImageAnalysisContextExhaustedException or LiteraryRamReserveException) return false;
            var text = current.Message.ToLowerInvariant();
            if (new[] { "out of memory", "cuda out", "insufficient memory", "not enough memory", "нехват", "недостаточно памяти",
                "недостаточно видеопамяти", "no internet", "network is unreachable", "name resolution", "connection refused",
                "нет интернета", "invalid parameter", "invalid argument", "неверные параметры", "некорректные параметры" }.Any(text.Contains)) return false;
            if (current.InnerException is { } inner && !IsEligible(inner)) return false;
        }
        return true;
    }
    public static void Report(Exception error)
    {
        if (ComponentAudit.Enabled || !IsEligible(error)) return;
        lock (Gate)
        {
            if (Seen.TryGetValue(error, out _)) return;
            Seen.Add(error, new());
            if (DateTime.UtcNow - _lastNotice < TimeSpan.FromSeconds(2)) return;
            _lastNotice = DateTime.UtcNow;
        }
        foreach (Action<Exception> listener in Failure?.GetInvocationList() ?? [])
        {
            try { listener(error); }
            catch (Exception observerError)
            { OwnedProcessRegistry.Log("component_audit_notice_failed", "ComponentAudit", detail: observerError.GetType().Name); }
        }
    }
}
