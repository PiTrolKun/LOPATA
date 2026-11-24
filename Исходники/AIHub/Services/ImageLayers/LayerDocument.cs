using System.Drawing;
using System.IO;
using System.Text.Json.Serialization;

namespace AIHub.Services.ImageLayers;

internal sealed record ImageLayer(Guid Id, Guid? Parent, string Name, string? Mask,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayerPointLayout? Points = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayerMatteReference? Matte = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayerRecognizedText? Text = null);
internal sealed record LayerChange(Guid Id, ImageLayer? Before, ImageLayer After);
internal sealed record LayerOperation(Guid Id, DateTimeOffset AtUtc, string Kind, Guid? Target,
    Rectangle? Area, string? Depth, OutputModelUse[] Models, LayerChange[] Changes, Guid? RestoredVersion = null);
internal sealed record LayerCandidate(string Id, string? Parent, string Name, LayerMask Mask, int OwnershipPriority = 0,
    LayerRecognizedText? Text = null);
internal sealed record LayerProposal(LayerCandidate[] Layers, OutputModelUse[] Models, int Skipped = 0);

internal sealed partial class LayerDocument
{
    internal bool ExternalAgentProject { get; init; }
    internal const string ScenarioId = "image_layers";
    internal const string AnalyzeKind = "analysis";
    internal const string RefineKind = "cross-check";
    internal const string PointsKind = "move-point";
    internal const string AddPointKind = "add-point";
    internal const string RemovePointsKind = "remove-points";
    internal const string PencilKind = "pencil";
    internal const string IncludeRegionKind = "include-region";
    internal const string ExcludeRegionKind = "exclude-region";
    internal const string RemoveRegionContoursKind = "remove-region-contours";
    internal const string ManualLayerKind = "manual-layer";
    internal const string LassoMergeKind = "lasso-merge";
    internal const string RestoreKind = "restore";

    private Dictionary<Guid, ImageLayer> _layers = [];
    private readonly Dictionary<string, LayerMask> _masks = new(StringComparer.Ordinal);
    private readonly List<LayerOperation> _history = [];

    internal LayerDocument(LayerSource source, Guid? id = null, DateTimeOffset? created = null)
    {
        Source = source;
        Id = id ?? Guid.NewGuid();
        CreatedUtc = created ?? DateTimeOffset.UtcNow;
        Empty = new(source.Width * source.Height);
    }

    internal Guid Id { get; }
    internal DateTimeOffset CreatedUtc { get; }
    internal LayerSource Source { get; }
    internal LayerMask Empty { get; }
    internal IReadOnlyDictionary<Guid, ImageLayer> Layers => _layers;
    internal IReadOnlyList<LayerOperation> History => _history;
    internal IReadOnlyDictionary<string, LayerMask> Masks => _masks;
    internal bool HasManualEdits => _history.Any(step => step.Kind == RestoreKind || IsManualBoundaryEdit(step.Kind));
    internal LayerOperation? RefinableVersion(Guid id) =>
        !HasManualEdits && _layers.GetValueOrDefault(id)?.Text is null
            && LayerHistory(id).LastOrDefault() is { Kind: AnalyzeKind } version ? version : null;

    private static bool IsManualBoundaryEdit(string kind) => kind is PointsKind or AddPointKind or RemovePointsKind or PencilKind
        or IncludeRegionKind or ExcludeRegionKind or RemoveRegionContoursKind or ManualLayerKind or LassoMergeKind;

    internal LayerDocument Fork()
    {
        var copy = new LayerDocument(Source, Id, CreatedUtc) { _layers = new(_layers) };
        foreach (var (key, mask) in _masks) copy._masks.Add(key, mask);
        foreach (var (key, matte) in _mattes) copy._mattes.Add(key, matte);
        copy._history.AddRange(_history);
        return copy;
    }

    internal LayerMask Mask(string? key) => key is null ? Empty : _masks[key];
    internal LayerMask Direct(ImageLayer layer) => Mask(layer.Mask);
    internal LayerMask Aggregate(Guid id, IReadOnlyDictionary<Guid, ImageLayer>? state = null)
    {
        state ??= _layers;
        return Merge(Branch(id, state).Select(key => Direct(state[key])));
    }

    internal HashSet<Guid> LayersWithCoverage(IReadOnlyDictionary<Guid, ImageLayer>? state = null)
    {
        state ??= _layers;
        var ids = new HashSet<Guid>();
        foreach (var layer in state.Values.Where(layer => layer.Mask is not null))
            for (ImageLayer? node = layer; node is not null && ids.Add(node.Id);)
                node = node.Parent is Guid parent ? state[parent] : null;
        return ids;
    }

    internal LayerMask Merge(IEnumerable<LayerMask> masks) => masks.Aggregate(Empty, (result, mask) => result.Union(mask));
    internal string? Remember(LayerMask mask)
    {
        if (mask.Length != Empty.Length) throw new InvalidDataException("ImageLayers.InvalidProject");
        if (!mask.Any) return null;
        _masks.TryAdd(mask.Key, mask);
        return mask.Key;
    }

    internal IEnumerable<Guid> Branch(Guid id, IReadOnlyDictionary<Guid, ImageLayer> state)
    {
        if (!state.ContainsKey(id)) yield break;
        var visible = new Queue<Guid>();
        visible.Enqueue(id);
        while (visible.TryDequeue(out var item))
        {
            yield return item;
            foreach (var child in state.Values.Where(layer => layer.Parent == item)) visible.Enqueue(child.Id);
        }
    }

    internal IEnumerable<LayerOperation> LayerHistory(Guid id)
    {
        var ids = Branch(id, Layers).ToHashSet();
        return _history.Where(step => step.Changes.Any(change => ids.Contains(change.Id)));
    }

    internal bool Commit(IReadOnlyDictionary<Guid, ImageLayer> plan, string kind, Guid? target = null,
        Rectangle? area = null, string? depth = null, OutputModelUse[]? models = null, Guid? restored = null)
    {
        var changes = plan.Values.Where(next => !_layers.TryGetValue(next.Id, out var current) || current != next)
            .Select(next => new LayerChange(next.Id, _layers.GetValueOrDefault(next.Id), next)).ToArray();
        if (changes.Length == 0) return false;
        Apply(new(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, target, area, depth, models ?? [], changes, restored));
        return true;
    }

    internal void Apply(LayerOperation step)
    {
        var next = new Dictionary<Guid, ImageLayer>(_layers);
        var changed = new HashSet<Guid>();
        if (step.Id == Guid.Empty || _history.Any(operation => operation.Id == step.Id)
            || step.Changes is null or { Length: 0 } || step.Models is null
            || step.Kind is not (AnalyzeKind or RefineKind or RestoreKind)
                && !IsManualBoundaryEdit(step.Kind) && !LayerAdditionalMethods.IsOperation(step.Kind)
            || step.Area is { } area && (area.IsEmpty || !Source.Frame.Contains(area)))
            throw new InvalidDataException("ImageLayers.InvalidProject");
        foreach (var change in step.Changes)
        {
            if (!changed.Add(change.Id) || change.Id == Guid.Empty || change.After is null || change.After.Id != change.Id
                || next.GetValueOrDefault(change.Id) != change.Before
                || string.IsNullOrWhiteSpace(change.After.Name) || change.After.Name.Length > 160
                || change.After.Name.Any(char.IsControl) || change.After.Mask is { } key && !_masks.ContainsKey(key)
                || change.After.Points is { } points && !points.IsValid() || !ValidMatte(change.After.Matte)
                || change.After.Text is { } text && !text.IsValid())
                throw new InvalidDataException("ImageLayers.InvalidProject");
            next[change.Id] = change.After;
        }
        foreach (var layer in next.Values.Where(layer => changed.Contains(layer.Id)))
        {
            var ancestors = new HashSet<Guid> { layer.Id };
            for (var parent = layer.Parent; parent is Guid id; parent = next[id].Parent)
                if (!ancestors.Add(id) || !next.ContainsKey(id)) throw new InvalidDataException("ImageLayers.InvalidProject");
        }
        foreach (var layer in next.Values.Where(layer => layer.Text?.Kind is LayerTextKind.Word or LayerTextKind.Glyph))
        {
            var kind = layer.Text!.Kind == LayerTextKind.Word ? LayerTextKind.Area : LayerTextKind.Word;
            if (layer.Parent is not Guid parent || next[parent].Text?.Kind != kind)
                throw new InvalidDataException("ImageLayers.InvalidProject");
        }
        var used = Merge(_layers.Values.Where(layer => !changed.Contains(layer.Id)).Select(Direct));
        foreach (var change in step.Changes)
        {
            var mask = Direct(change.After);
            if (used.Meets(mask)) throw new InvalidDataException("ImageLayers.InvalidProject");
            used = used.Union(mask);
        }
        _layers = next;
        _history.Add(step);
    }

    private Dictionary<Guid, ImageLayer> At(int index)
    {
        var state = new Dictionary<Guid, ImageLayer>();
        for (var i = 0; i <= index; i++)
            foreach (var change in _history[i].Changes) state[change.Id] = change.After;
        return state;
    }
}
