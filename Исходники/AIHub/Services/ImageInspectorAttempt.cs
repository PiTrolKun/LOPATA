using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using AIHub.Models;
using Lopata.Inspector;

namespace AIHub.Services;

internal sealed partial class ImageInspectorAttempt
{
    private readonly object _sync = new();
    private readonly bool _retry;
    private InspectorEvent _context;
    private string? _stage;
    private string? _detail;
    private bool _finished;
    private bool _decoding;

    private ImageInspectorAttempt(InspectorEvent context, bool retry)
    {
        _context = context;
        _retry = retry;
    }

    internal static ImageInspectorAttempt? Create(ImageGenerationRequest request, SdRuntimeSelection selection,
        IReadOnlyList<ManagedModelArtifactCard> cards, int index, bool retry = false, bool warmup = false)
    {
        if (ApplicationInspector.DescribeImage(request, index * 2 + (retry ? 2 : 1)) is not { } run) return null;
        var modelBytes = cards.Where(card => card.ModelArtifactId != "generation-runtime")
            .SelectMany(card => card.Files).Where(file => file.SizeBytes > 0)
            .Aggregate(0L, (bytes, file) => bytes + Math.Min(long.MaxValue - bytes, file.SizeBytes));
        return new(run with
        {
            Engine = "sd.cpp/" + selection.Revision,
            Backend = selection.Backend,
            Device = selection.UsesGpu ? null : "cpu",
            ExpectedVideoBytes = selection.UsesGpu && modelBytes > 0 ? modelBytes : null,
            ExpectedRamBytes = !selection.UsesGpu && modelBytes is > 0 and <= long.MaxValue / 2 ? modelBytes * 2 : null,
            Stage = warmup ? "model_loading" : "preparing"
        }, retry);
    }

    internal async Task BeginAsync(CancellationToken token)
    {
        await ApplicationInspector.PreflightAsync(_context, token);
        lock (_sync)
            Publish(InspectorEventKind.AttemptStarted, _stage,
                outcome: _retry ? InspectorOutcome.CpuRetry : InspectorOutcome.Unknown);
    }

    internal void SetWarm(bool warm)
    {
        lock (_sync) _context = _context with { Warm = warm };
    }

    internal void Attach(Process process, bool announce = true)
    {
        DateTimeOffset? started = null;
        try { started = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        lock (_sync)
            _context = _context with { ProcessId = process.Id, ProcessStartedUtc = started };
        if (announce) Emit(InspectorEventKind.ProcessStarted);
    }

    internal void Progress(string stage, string? detail = null)
    {
        lock (_sync)
        {
            if (_finished || (_stage, _detail) == (stage, detail)) return;
            _stage = stage;
            _detail = detail;
            Publish(InspectorEventKind.Progress, stage, detail);
        }
    }

    internal void Complete(InspectorOutcome outcome)
    {
        lock (_sync)
        {
            if (_finished) return;
            _finished = true;
            Publish(InspectorEventKind.AttemptFinished, _stage, outcome: outcome);
        }
    }

    internal async Task FailAsync(Exception error, InspectorOutcome outcome, CancellationToken token)
    {
        InspectorEvent item;
        lock (_sync)
        {
            if (_finished) return;
            _finished = true;
            item = _context with { Stage = _stage ?? _context.Stage };
        }
        await ApplicationInspector.ReportFailureAsync(error, item, token, outcome);
    }

    internal void Emit(InspectorEventKind kind)
    {
        lock (_sync) Publish(kind, _stage);
    }

    private void Publish(InspectorEventKind kind, string? stage, string? detail = null,
        InspectorOutcome outcome = InspectorOutcome.Unknown) =>
        ApplicationInspector.Publish(_context with
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Stage = stage ?? _context.Stage,
            Detail = detail,
            Outcome = outcome,
            TimestampUtc = DateTimeOffset.UtcNow
        });

    internal void ObserveNativeProgress(string line)
    {
        lock (_sync)
        {
            if (line.Contains("generating image:", StringComparison.OrdinalIgnoreCase)) _decoding = false;
            if (ReadNativeProgress(line, _decoding) is not { } progress) return;
            if (progress.Stage == "image_decoding") _decoding = true;
            Progress(progress.Stage, progress.Detail);
        }
    }

    internal static (string Stage, string? Detail)? ReadNativeProgress(string line, bool decoding = false)
    {
        if (line.Contains("sampling completed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("decoding ", StringComparison.OrdinalIgnoreCase) && line.Contains(" latents", StringComparison.OrdinalIgnoreCase))
            return ("image_decoding", null);
        if (line.Contains("load tensors", StringComparison.OrdinalIgnoreCase)
            || line.Contains("loading tensors", StringComparison.OrdinalIgnoreCase)
            || line.Contains("load model", StringComparison.OrdinalIgnoreCase))
            return (decoding ? "image_decoding" : "model_loading", null);
        if (NativeProgress().Match(line) is { Success: true } match)
        {
            if (decoding) return ("image_decoding", null);
            if (match.Groups["unit"].Value.EndsWith("B/s", StringComparison.Ordinal)) return ("model_loading", null);
            return ("generating", $"step {match.Groups[1].Value}/{match.Groups[2].Value}");
        }
        return null;
    }

    internal static InspectorOutcome Evaluate(int exit, string log, ImageGenerationGpuPlacement placement,
        bool usesGpu)
    {
        if (exit == 0) return InspectorOutcome.Success;
        var native = GenerationFailureClassifier.FromWorker(exit, log, usesGpu ? null : "cpu");
        if (native != InspectorOutcome.Failure) return native;
        if (usesGpu && placement == ImageGenerationGpuPlacement.MemoryUnavailable) return InspectorOutcome.VideoMemoryAdmissionDenied;
        if (usesGpu && log.Contains("video memory", StringComparison.OrdinalIgnoreCase)
            && log.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
            return InspectorOutcome.OutOfVideoMemory;
        if (!usesGpu && (log.Contains("std::bad_alloc", StringComparison.OrdinalIgnoreCase)
            || log.Contains("out of memory", StringComparison.OrdinalIgnoreCase))) return InspectorOutcome.OutOfMemory;
        return InspectorOutcome.Failure;
    }

    internal static InspectorOutcome Evaluate(Exception error, CancellationToken token) =>
        error is OperationCanceledException && (token.IsCancellationRequested || error.Message == ApplicationInspector.Deferred)
            ? InspectorOutcome.Cancelled
            : error is BackgroundOperationWaitingException or HttpRequestException or OperationCanceledException
                ? InspectorOutcome.Disconnected
                : GenerationFailureClassifier.FromException(error, token);

    [GeneratedRegex(@"\|\s*(\d{1,4})\s*/\s*(\d{1,4})\s*-\s*[\d.]+\s*(?<unit>[KMGT]?B/s|it/s|s/it)",
        RegexOptions.CultureInvariant)]
    private static partial Regex NativeProgress();
}
