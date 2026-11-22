using System.Globalization;
using Lopata.Inspector;

namespace Lopata.PcInspector;

internal sealed class ObservedAttempt
{
    private DateTimeOffset? _nativeStarted;
    private InspectorGpuSample? _gpu;
    private long? _peakDedicated;
    private long? _peakShared;
    private bool _pressure;
    private bool _sampledVideoMemory;
    private int _pressureSamples;

    internal ObservedAttempt(InspectorEvent item)
    {
        Item = item;
        StageStartedUtc = item.TimestampUtc;
        LastEventUtc = item.TimestampUtc;
        Update(item);
    }

    internal InspectorEvent Item { get; private set; }
    internal DateTimeOffset LastEventUtc { get; private set; }
    internal DateTimeOffset StageStartedUtc { get; private set; }
    internal DateTimeOffset ReadTypicalAt { get; set; }
    internal double? TypicalSeconds { get; set; }
    internal bool ProgressObserved { get; private set; }
    internal bool SustainedVideoPressure => _pressureSamples >= 3;
    internal bool Active => Item.Stage is not ("paused" or "waiting") && Item.Kind != InspectorEventKind.Queued;
    internal bool KnownStage => Item.Stage is "generating" or "image_decoding" or "model_loading" or "output_write" or "worker_connect";
    internal string? Context => FindContext(Item, _gpu);

    internal void Update(InspectorEvent item)
    {
        if (item.TimestampUtc < LastEventUtc) return;
        var previous = Item;
        var stage = item.Model is null && KnownStage && item.Stage is not ("paused" or "waiting")
            ? previous.Stage : item.Stage ?? previous.Stage;
        if (previous.Stage != stage)
        {
            ProgressObserved = false;
            StageStartedUtc = item.TimestampUtc;
        }
        if (item.Kind is InspectorEventKind.AttemptStarted or InspectorEventKind.ProcessStarted &&
            !string.IsNullOrEmpty(item.Model))
            _nativeStarted ??= item.TimestampUtc;
        if (item.Kind == InspectorEventKind.Progress && item.Stage is "generating" &&
            item.Detail is { } detail && detail.StartsWith("step", StringComparison.Ordinal))
            ProgressObserved = true;
        LastEventUtc = item.TimestampUtc;
        Item = item with
        {
            Scenario = item.Scenario ?? previous.Scenario,
            Engine = item.Engine ?? previous.Engine,
            Model = item.Model ?? previous.Model,
            Width = item.Width > 0 ? item.Width : previous.Width,
            Height = item.Height > 0 ? item.Height : previous.Height,
            Backend = item.Backend ?? previous.Backend,
            Warm = item.Warm ?? previous.Warm,
            ProcessId = item.ProcessId > 0 ? item.ProcessId : previous.ProcessId,
            ProcessStartedUtc = item.ProcessStartedUtc ?? previous.ProcessStartedUtc,
            Adapter = item.Adapter ?? previous.Adapter,
            Stage = stage
        };
    }

    internal void Observe(GpuConsumer? worker, InspectorGpuSample? gpu, bool pressure)
    {
        if (gpu is not null) _gpu = gpu;
        if (worker?.DedicatedBytes is { } dedicated) _peakDedicated = Math.Max(_peakDedicated ?? 0, dedicated);
        if (worker?.SharedBytes is { } shared) _peakShared = Math.Max(_peakShared ?? 0, shared);
        if (worker?.UtilizationPercent > 0) ProgressObserved = true;
        _pressureSamples = pressure ? Math.Min(3, _pressureSamples + 1) : 0;
        _pressure |= SustainedVideoPressure;
        _sampledVideoMemory |= gpu?.DedicatedBytes is not null && worker?.DedicatedBytes is not null;
    }

    internal InspectorExperience? Complete(InspectorEvent item)
    {
        if (_nativeStarted is not { } started || item.TimestampUtc <= started || Context is not { } context ||
            item.Outcome is InspectorOutcome.Unknown or InspectorOutcome.Cancelled)
            return null;
        var pressure = Item.Backend?.Equals("cpu", StringComparison.OrdinalIgnoreCase) == true
            ? "normal" : _pressure ? "high" : _sampledVideoMemory ? "normal" : "unknown";
        return new InspectorExperience(InspectorStore.Hash(item.OperationId + "\n" + item.Attempt),
            context, item.TimestampUtc, item.OperationId, item.Attempt, Item.Model!, Item.Engine!, Item.Backend!,
            Item.Width, Item.Height, _gpu?.Name, _gpu?.CapacityBytes, Item.Warm,
            (item.TimestampUtc - started).TotalSeconds, pressure, item.Outcome, _peakDedicated, _peakShared);
    }

    internal static string? FindContext(InspectorEvent item, InspectorGpuSample? gpu)
    {
        if (string.IsNullOrWhiteSpace(item.Model) || string.IsNullOrWhiteSpace(item.Engine) ||
            string.IsNullOrWhiteSpace(item.Backend) || item.Width <= 0 || item.Height <= 0 || item.Warm is null)
            return null;
        var cpu = item.Backend.Equals("cpu", StringComparison.OrdinalIgnoreCase);
        if (!cpu && gpu?.CapacityBytes is not > 0) return null;
        return string.Join("\n", item.Model, item.Engine, item.Backend.ToLowerInvariant(),
            item.Width.ToString(CultureInfo.InvariantCulture), item.Height.ToString(CultureInfo.InvariantCulture),
            item.Warm.Value ? "warm" : "cold", cpu ? "cpu" : gpu!.Name,
            cpu ? "" : gpu!.CapacityBytes!.Value.ToString(CultureInfo.InvariantCulture));
    }
}
