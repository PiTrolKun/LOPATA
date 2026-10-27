using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>The application host installs the durable controller; isolated runtime tests remain independent.</summary>
public static class ApplicationBackgroundOperations
{
    public static BackgroundOperationController? Current { get; internal set; }
    public static bool PreserveHiddenWork { get; internal set; }
    public static CancellationToken ExitToken { get; internal set; }
    internal static bool IsSuspending(CancellationToken token) => token.IsCancellationRequested
        && (ExitToken.IsCancellationRequested || Current?.State?.Phase is BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Waiting);
    private static readonly object Gate = new();
    private static readonly List<(WeakReference<object> Owner, Func<object, Task> Retire)> Models = [];

    public static void RegisterModel<T>(T owner, Func<T, Task> retire) where T : class
    {
        lock (Gate)
        {
            Models.RemoveAll(model => !model.Owner.TryGetTarget(out _));
            Models.Add((new(owner), value => retire((T)value)));
        }
    }

    public static Task RetireModelsAsync() => RetireModelsExceptAsync(null);
    internal static async Task RetireModelsExceptAsync(object? keep)
    {
        (WeakReference<object> Owner, Func<object, Task> Retire)[] models;
        lock (Gate) { Models.RemoveAll(model => !model.Owner.TryGetTarget(out _)); models = Models.ToArray(); }
        List<Exception> errors = [];
        foreach (var model in models)
            if (model.Owner.TryGetTarget(out var owner) && !ReferenceEquals(owner, keep))
                try { await model.Retire(owner); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Model retirement was not confirmed for every backend.", errors);
    }

    public static async Task<T> RunAsync<T>(string kind, string title, string? project, object input,
        Func<CancellationToken, Task<T>> execute, CancellationToken token, BackgroundOperationState? restored = null)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        if (Current is not { } controller)
        {
            try { return await execute(token); }
            catch (Exception error) { ComponentAuditFailures.Report(error); throw; }
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ExitToken);
        var operation = restored ?? new BackgroundOperationState
            { Kind = kind, Title = title, Project = project, Input = JsonSerializer.SerializeToElement(input) };
        try { return await controller.RunAsync(operation, execute, RetireModelsAsync, lifetime.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !ExitToken.IsCancellationRequested)
        { controller.DiscardPending(operation.Id); throw; }
        catch (Exception error) { ComponentAuditFailures.Report(error); throw; }
    }
}
