using System.Drawing;
using System.IO;

namespace AIHub.Services.ImageLayers;

internal sealed partial class LayerDocument
{
    internal Dictionary<Guid, ImageLayer> PlanAnalysis(LayerProposal proposal, Rectangle area, Guid? target)
    {
        var result = new Dictionary<Guid, ImageLayer>(_layers);
        var scoped = target is Guid selected ? Branch(selected, _layers).ToHashSet() : _layers.Keys.ToHashSet();
        var areaMask = LayerMask.Area(Source.Width, Source.Height, area);
        var reserved = Merge(_layers.Values.Where(layer => !scoped.Contains(layer.Id)).Select(Direct));
        var editable = areaMask.Difference(reserved);
        var oldText = _layers.Values.Where(layer => layer.Text?.Kind == LayerTextKind.Area)
            .Select(layer => (Layer: layer, Shape: Aggregate(layer.Id))).Where(entry => entry.Shape.Any).ToArray();
        var shapes = oldText.ToDictionary(entry => entry.Layer.Id, entry => entry.Shape);
        var newTextZones = oldText.Select(entry => (Bounds: entry.Shape.Bounds(Source.Width), Ink: entry.Shape)).ToList();

        var candidates = proposal.Layers.ToDictionary(candidate => candidate.Id);
        var translated = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var claimed = new HashSet<Guid>();

        Guid Map(LayerCandidate candidate)
        {
            if (translated.TryGetValue(candidate.Id, out var existing)) return existing;
            var parent = candidate.Parent is { } key ? Map(candidates[key]) : target;
            var current = candidate.Id == "$target" && target is Guid anchor ? _layers[anchor]
                : Match(candidate, parent);
            var id = current?.Id ?? Guid.NewGuid();
            translated.Add(candidate.Id, id);
            depth.Add(candidate.Id, candidate.Parent is { } key2 ? depth[key2] + 1 : 0);
            claimed.Add(id);
            result[id] = result.GetValueOrDefault(id) ?? new(id, parent, candidate.Name, null, Text: candidate.Text);
            return id;
        }

        ImageLayer? Match(LayerCandidate candidate, Guid? parent)
        {
            var siblings = _layers.Values.Where(layer => !claimed.Contains(layer.Id) && layer.Parent == parent
                && layer.Text?.Kind == candidate.Text?.Kind).ToArray();
            if (candidate.Text is { Kind: LayerTextKind.Word or LayerTextKind.Glyph } ordered)
                return siblings.FirstOrDefault(layer => layer.Text!.Order == ordered.Order);
            if (candidate.Text?.Kind == LayerTextKind.Area)
                return siblings.Where(layer => shapes.TryGetValue(layer.Id, out var shape) && area.Contains(shape.Bounds(Source.Width)))
                    .Select(layer => (Layer: layer, Agreement: TextAreaAgreement(shapes[layer.Id], candidate.Mask)))
                    .Where(entry => entry.Agreement >= .6).OrderByDescending(entry => entry.Agreement)
                    .Select(entry => entry.Layer).FirstOrDefault();
            var sameName = siblings.Where(layer => layer.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameName.Length == 1) return sameName[0];
            var predicted = candidate.Mask.Intersect(areaMask);
            if (!predicted.Any) return null;
            var scores = (sameName.Length > 0 ? sameName : siblings).Select(layer =>
            {
                var old = Aggregate(layer.Id).Intersect(areaMask);
                var common = old.Intersect(predicted).Count;
                return (layer, overlap: common / (double)(old.Count + predicted.Count - common));
            }).OrderByDescending(item => item.overlap).ToArray();
            // A geometric match keeps history even if the model renamed the same object.
            // Require a clear winner before associating an old layer with a different name.
            return scores is [{ overlap: >= .65 } first, ..]
                && (scores.Length == 1 || first.overlap - scores[1].overlap >= .15) ? first.layer : null;
        }

        var textRoots = proposal.Layers.Where(item => item.Text?.Kind == LayerTextKind.Area).ToArray();
        var acceptedText = new HashSet<string>(StringComparer.Ordinal);
        var replacedText = new HashSet<Guid>();
        foreach (var root in textRoots)
        {
            // A line is an editable unit. Do not generate a transcript from a clipped portion of an existing line.
            if (root.Mask.Difference(editable).Any || oldText.Any(entry =>
                !area.Contains(entry.Shape.Bounds(Source.Width)) && root.Mask.Meets(entry.Shape))) continue;
            var id = Map(root);
            acceptedText.Add(root.Id);
            if (shapes.ContainsKey(id)) replacedText.Add(id);
            newTextZones.Add((root.Mask.Bounds(Source.Width), root.Mask));
        }

        var protectedInk = Merge(oldText.Where(entry => !replacedText.Contains(entry.Layer.Id)).Select(entry => entry.Shape));
        var available = editable.Difference(protectedInk);
        var added = new Dictionary<Guid, LayerMask>();
        var found = new Dictionary<Guid, LayerRecognizedText?>();
        foreach (var candidate in proposal.Layers)
        {
            if (Included(candidate)) Map(candidate);
        }
        foreach (var candidate in proposal.Layers.Where(item => translated.ContainsKey(item.Id)
                && item.Text?.Kind is not (LayerTextKind.Area or LayerTextKind.Word))
            .OrderByDescending(item => item.Text is not null).ThenByDescending(item => depth[item.Id])
            .ThenBy(item => item.OwnershipPriority))
        {
            var mask = candidate.Mask.Intersect(available);
            if (mask.Any)
            {
                added.Add(translated[candidate.Id], mask);
                available = available.Difference(mask);
                for (var node = candidate; found.TryAdd(translated[node.Id], node.Text) && node.Parent is { } parent;)
                    node = candidates[parent];
            }
        }
        if (added.Count == 0) throw new InvalidDataException("ImageLayers.NoResult");
        var assigned = editable.Difference(protectedInk).Difference(available);
        var replacedBranches = replacedText.SelectMany(id => Branch(id, _layers)).ToHashSet();
        foreach (var id in scoped.Where(id => _layers[id].Mask is not null))
        {
            var layer = _layers[id];
            var previous = Direct(layer);
            var replaced = found.ContainsKey(id) || replacedBranches.Contains(id)
                || layer.Text is null && SupersededLabel(layer.Name, previous, newTextZones);
            result[id] = layer with { Mask = Remember(previous.Difference(replaced ? editable : assigned)) };
        }
        foreach (var (id, recognized) in found)
        {
            var layer = result[id];
            var mask = added.TryGetValue(id, out var pixels) ? Remember(Direct(layer).Union(pixels)) : layer.Mask;
            result[id] = recognized is null ? layer with { Mask = mask }
                : layer with { Mask = mask, Name = TextLayerName(recognized), Text = recognized };
        }
        PruneNewEmptyLayers(result);
        return result;

        bool Included(LayerCandidate item)
        {
            if (item.Text is null)
                return item.Id == "$target" || !SupersededLabel(item.Name, item.Mask, newTextZones);
            while (item.Text!.Kind != LayerTextKind.Area)
                item = candidates[item.Parent!];
            return acceptedText.Contains(item.Id);
        }
    }

    internal Dictionary<Guid, ImageLayer> LimitPlan(IReadOnlyDictionary<Guid, ImageLayer> full, Rectangle area)
    {
        var scope = LayerMask.Area(Source.Width, Source.Height, area);
        // Accept a recognized area together with the text and every glyph that the user saw in the comparison.
        var changedText = full.Values.Where(layer => layer.Text is not null && _layers.GetValueOrDefault(layer.Id) != layer)
            .Select(layer => TextAreaOf(layer, full)).DistinctBy(layer => layer.Id);
        foreach (var root in changedText)
        {
            var bounds = Aggregate(root.Id).Union(Aggregate(root.Id, full)).Bounds(Source.Width);
            if (bounds.IntersectsWith(area) && !area.Contains(bounds))
                scope = scope.Difference(LayerMask.Area(Source.Width, Source.Height, bounds));
        }
        var result = new Dictionary<Guid, ImageLayer>(_layers);
        foreach (var next in full.Values)
        {
            var previous = _layers.GetValueOrDefault(next.Id);
            var kept = Mask(previous?.Mask).Difference(scope);
            var adopted = Direct(next).Intersect(scope);
            var metadata = next.Text is not null && adopted.Any ? next : previous ?? next;
            if (next.Text?.Kind is LayerTextKind.Area or LayerTextKind.Word
                && Aggregate(next.Id, full).Meets(scope))
                metadata = next;
            result[next.Id] = metadata with { Mask = Remember(kept.Union(adopted)) };
        }
        PruneNewEmptyLayers(result);
        return result;
    }

    internal static ImageLayer TextAreaOf(ImageLayer layer, IReadOnlyDictionary<Guid, ImageLayer> state)
    {
        while (layer.Text?.Kind is LayerTextKind.Word or LayerTextKind.Glyph)
            layer = state[layer.Parent!.Value];
        return layer;
    }

    internal static string TextLayerName(LayerRecognizedText text)
    {
        var name = text.Value.Replace('\n', ' ');
        if (name.Length <= 160) return name;
        return name[..(char.IsHighSurrogate(name[158]) ? 158 : 159)] + "…";
    }

    private double TextAreaAgreement(LayerMask old, LayerMask predicted)
    {
        var first = old.Bounds(Source.Width);
        var second = predicted.Bounds(Source.Width);
        var common = Rectangle.Intersect(first, second);
        return common.Width * (double)common.Height / Math.Max(first.Width * (double)first.Height, second.Width * (double)second.Height);
    }

    private bool SupersededLabel(string name, LayerMask mask, List<(Rectangle Bounds, LayerMask Ink)> zones)
    {
        if (zones.Count == 0 || !LayerRecognizedText.IsTextLabel(name) || !mask.Any) return false;
        var bounds = mask.Bounds(Source.Width);
        foreach (var zone in zones)
        {
            var shared = Rectangle.Intersect(bounds, Rectangle.Inflate(zone.Bounds, 3, 3));
            if (shared.Width * (double)shared.Height >= .65 * bounds.Width * bounds.Height && mask.Meets(zone.Ink))
                return true;
        }
        return false;
    }

    private void PruneNewEmptyLayers(Dictionary<Guid, ImageLayer> state)
    {
        var keep = new HashSet<Guid>(_layers.Keys);
        foreach (var layer in state.Values.Where(layer => layer.Mask is not null))
        {
            for (ImageLayer? node = layer; node is not null && keep.Add(node.Id);)
                node = node.Parent is Guid id ? state[id] : null;
        }
        foreach (var id in state.Keys.Where(id => !keep.Contains(id)).ToArray()) state.Remove(id);
    }

    internal void DiscardPreviewMasks()
    {
        var used = _history.SelectMany(step => step.Changes)
            .SelectMany(change => new[] { change.Before?.Mask, change.After.Mask }).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _masks.Keys.Where(key => !used.Contains(key)).ToArray()) _masks.Remove(key);
        var mattes = _history.SelectMany(step => step.Changes)
            .SelectMany(change => new[] { change.Before?.Matte?.Key, change.After.Matte?.Key }).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _mattes.Keys.Where(key => !mattes.Contains(key)).ToArray()) _mattes.Remove(key);
    }

    internal Dictionary<Guid, ImageLayer> PlanAdditionalLayer(string name, LayerMask mask, LayerMatteReference? matte = null) =>
        PlanAdditionalLayers([(name, mask, matte)]);

    internal Dictionary<Guid, ImageLayer> PlanAdditionalLayers(IEnumerable<(string Name, LayerMask Mask, LayerMatteReference? Matte)> addition)
    {
        var entries = new List<ImageLayer>();
        var used = Empty;
        foreach (var (name, proposed, matte) in addition)
        {
            var mask = proposed.Difference(used);
            if (!mask.Any) continue;
            entries.Add(new(Guid.NewGuid(), null, name, Remember(mask), Matte: matte));
            used = used.Union(mask);
        }
        if (entries.Count == 0) throw new InvalidDataException("ImageLayers.NoResult");
        var result = _layers.ToDictionary(pair => pair.Key, pair =>
        {
            var layer = pair.Value;
            var previous = Direct(layer);
            return previous.Meets(used) ? layer with { Mask = Remember(previous.Difference(used)) } : layer;
        });
        foreach (var layer in entries) result.Add(layer.Id, layer);
        return result;
    }
}
