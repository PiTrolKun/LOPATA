using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal static class LayerPortraitParts
{
    private static readonly string[] Russian = ["Волосы", "Кожа тела", "Лицо", "Одежда"];
    private static readonly string[] English = ["Hair", "Body skin", "Face", "Clothing"];

    internal static async Task<bool> AppendAsync(ManagedModelLibraryStore library, LayerSource source,
        LayerAnalysisRequest request, List<LayerCandidate> candidates, IReadOnlySet<string> people,
        IProgress<LayerAnalysisProgress> progress, CancellationToken token)
    {
        var persons = candidates.Where(candidate => people.Contains(candidate.Id) && candidate.Mask.Any).ToArray();
        if (persons.Length == 0) return false;
        var crops = persons.Select(person => Crop(person.Mask, source.Width, request.Area)).ToArray();
        progress.Report(new("PortraitPartsProgress"));
        var labeled = await LayerToolWorker.PartsAsync(library, source, crops, token);
        var names = request.Russian ? Russian : English;
        var occupied = candidates.ToArray();
        for (var index = 0; index < persons.Length; index++)
        {
            var person = persons[index];
            var childMask = new LayerMask(source.Width * source.Height);
            // Specific garments and separately named items keep their pixels.
            foreach (var layer in occupied.Where(layer => layer.Id != person.Id && layer.Parent == person.Id))
                childMask = childMask.Union(layer.Mask);
            var available = person.Mask.Difference(childMask);
            foreach (var part in FromLabels(source.Width, source.Height, crops[index], labeled[index], available, token))
            {
                if (!part.Mask.Any) continue;
                var id = "$portrait-" + person.Id + "-" + part.Category;
                candidates.Add(new(id, person.Id, names[part.Category - 1], part.Mask));
            }
        }
        return true;
    }

    internal static (int Category, LayerMask Mask)[] FromLabels(int width, int height, Rectangle crop, byte[] labels,
        LayerMask available, CancellationToken token)
    {
        var masks = new List<(int, LayerMask)>(4);
        for (var category = 1; category <= 4; category++)
        {
            var pixels = LayerMask.Build(width * height, bits =>
            {
                for (var y = 0; y < crop.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var global = (crop.Top + y) * width + crop.Left;
                    var local = y * crop.Width;
                    for (var x = 0; x < crop.Width; x++)
                        if (labels[local + x] == category && available[global + x]) bits[global + x] = true;
                }
            });
            masks.Add((category, pixels));
        }
        return [.. masks];
    }

    internal static Rectangle Crop(LayerMask mask, int width, Rectangle area)
    {
        var bounds = mask.Bounds(width);
        return Rectangle.Intersect(area, Rectangle.Inflate(bounds, Math.Max(5, bounds.Width / 8), Math.Max(5, bounds.Height / 8)));
    }
}
