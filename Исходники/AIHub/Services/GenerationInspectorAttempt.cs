using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Lopata.Inspector;

namespace AIHub.Services;

internal sealed class InspectorDeferredException(string warning, CancellationToken token)
    : OperationCanceledException(ApplicationInspector.Deferred, token)
{
    internal string Warning { get; } = warning;
}

internal sealed class GenerationInspectorAttempt(InspectorEvent? context)
{
    private static readonly object EvidenceKey = new();
    private InspectorEvent? _context = context;
    private int _finished;

    internal string? Device => _context?.Device;

    internal async Task CheckAsync(string stage, long? needed, long? free, CancellationToken token)
    {
        if (_context is not { } item) return;
        _context = item with { Stage = stage, ExpectedVideoBytes = needed, AvailableVideoBytes = free };
        await ApplicationInspector.PreflightAsync(_context, token);
    }

    internal void Stage(string stage)
    {
        if (_context is not { } item || item.Stage == stage) return;
        _context = item with { Stage = stage };
        Emit(InspectorEventKind.Progress);
    }

    internal Process Attach(Process process)
    {
        if (_context is not { } item) return process;
        DateTimeOffset? started = null;
        try { started = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
        _context = item with { ProcessId = process.Id, ProcessStartedUtc = started };
        Emit(InspectorEventKind.ProcessStarted);
        return process;
    }

    internal void LinkRequest(Guid requestId)
        => LinkRequest(requestId.ToString("N"));

    internal void LinkRequest(string requestId)
    {
        if (_context is { } item && requestId.Length is > 0 and <= 128
            && requestId.All(value => char.IsAsciiLetterOrDigit(value) || value == '-'))
            _context = item with { RequestId = requestId };
    }

    internal void Emit(InspectorEventKind kind, InspectorOutcome outcome = InspectorOutcome.Unknown)
    {
        if (_context is { } item)
            ApplicationInspector.Publish(item with { Id = Guid.NewGuid(), TimestampUtc = DateTimeOffset.UtcNow, Kind = kind, Outcome = outcome });
    }

    internal void Succeed()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
            Emit(InspectorEventKind.AttemptFinished, InspectorOutcome.Success);
    }

    internal async Task FailAsync(Exception error, CancellationToken token, InspectorOutcome? outcome = null)
    {
        if (_context is not { } item || Interlocked.Exchange(ref _finished, 1) != 0) return;
        if (Evidence(error) is not null)
        {
            Emit(InspectorEventKind.AttemptFinished);
            return;
        }
        await ApplicationInspector.ReportFailureAsync(error, item, token, outcome);
    }

    internal static InspectorEvent? Evidence(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current.Data[EvidenceKey] is InspectorEvent item) return item;
        return null;
    }

    internal static InspectorEvent RecordFailure(Exception error, InspectorEvent item, InspectorOutcome outcome, bool foreground)
    {
        var result = item with { Id = Guid.NewGuid(), TimestampUtc = DateTimeOffset.UtcNow,
            Kind = InspectorEventKind.AttemptFinished, Outcome = outcome, UiErrorShown = foreground };
        error.Data[EvidenceKey] = result;
        ApplicationInspector.Publish(result);
        return result;
    }
}

internal static class GenerationFailureClassifier
{
    internal static InspectorOutcome FromException(Exception error, CancellationToken token)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var outcome = current switch
            {
                InspectorDeferredException => InspectorOutcome.Cancelled,
                OperationCanceledException when token.IsCancellationRequested => InspectorOutcome.Cancelled,
                OutOfMemoryException or Win32Exception { NativeErrorCode: 8 or 14 or 1455 } => InspectorOutcome.OutOfMemory,
                HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.NameResolutionError
                    or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.SecureConnectionError }
                    => InspectorOutcome.Disconnected,
                System.Net.Sockets.SocketException => InspectorOutcome.Disconnected,
                _ => InspectorOutcome.Unknown
            };
            if (outcome != InspectorOutcome.Unknown) return outcome;
        }
        return InspectorOutcome.Failure;
    }

    internal static InspectorOutcome FromWorker(int exitCode, string? error, string? device)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        bool Contains(string code) => error?.Contains(code, comparison) == true;
        if (device != "cpu")
        {
            if (Contains("Insufficient free VRAM:")) return InspectorOutcome.VideoMemoryAdmissionDenied;
            if (Contains("torch.OutOfMemoryError:") || Contains("CUDA out of memory.") || Contains("CUDA error: out of memory")
                || Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY") || Contains("ZE_RESULT_ERROR_OUT_OF_DEVICE_MEMORY")
                || Contains("DXGI_ERROR_OUT_OF_MEMORY")) return InspectorOutcome.OutOfVideoMemory;
            if (Contains("CUDA_ERROR_DEVICE_LOST") || Contains("VK_ERROR_DEVICE_LOST") || Contains("DXGI_ERROR_DEVICE_REMOVED")
                || Contains("DXGI_ERROR_DEVICE_RESET") || Contains("DXGI_ERROR_DEVICE_HUNG")) return InspectorOutcome.DriverFailure;
        }
        if (exitCode == unchecked((int)0xc0000017) || Contains("std::bad_alloc") || Contains("[WinError 1455]")
            || Contains("VK_ERROR_OUT_OF_HOST_MEMORY") || error?.Split('\n').Any(line =>
                line.TrimStart().StartsWith("MemoryError:", StringComparison.Ordinal) || line.Trim() == "MemoryError") == true)
            return InspectorOutcome.OutOfMemory;
        return exitCode is unchecked((int)0xc0000005) or unchecked((int)0xc0000409) or unchecked((int)0xc000001d)
            ? InspectorOutcome.WorkerFailure : InspectorOutcome.Failure;
    }
}
