using System.IO;
using System.Text.Json;

namespace AIHub.Services.ImageLora;

internal static class ImageLoraTensorRules
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, int[]>>> Signatures = new(() => {
        using var stream = typeof(ImageLoraCatalog).Assembly.GetManifestResourceStream("AIHub.ImageLoraSignatures")!;
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int[]>>>(stream)!;
    });
    internal static void ValidateStorage(JsonElement header, long bytes)
    {
        var ranges = new List<(long Start, long End)>(); var names = new HashSet<string>();
        foreach (var p in header.EnumerateObject()) {
            if (!names.Add(p.Name)) Fail(); if (p.Name == "__metadata__") continue;
            var t = p.Value; var dtype = t.GetProperty("dtype").GetString(); var width = dtype == "F32" ? 4 : dtype is "F16" or "BF16" ? 2 : 0;
            var shape = t.GetProperty("shape").EnumerateArray().Select(x => x.GetInt64()).ToArray();
            var offset = t.GetProperty("data_offsets").EnumerateArray().Select(x => x.GetInt64()).ToArray();
            if (width == 0 || shape.Length > 2 || shape.Any(x => x <= 0 || x > 1000000) || offset.Length != 2
                || offset[0] < 0 || offset[1] <= offset[0] || offset[1] > bytes
                || shape.Aggregate((long)width, (a,b) => checked(a*b)) != offset[1] - offset[0]) Fail();
            ranges.Add((offset[0], offset[1]));
        }
        if (ranges.Count is < 2 or > 10000) Fail();
        long end = 0; foreach (var range in ranges.OrderBy(r => r.Start)) { if (range.Start != end) Fail(); end = range.End; }
        if (end != bytes) Fail();
    }
    internal static void Validate(JsonElement header, string model)
    {
        if (!Signatures.Value.TryGetValue(model, out var signature)) Fail();
        var pairs = 0;
        foreach (var p in header.EnumerateObject()) {
            if (p.Name == "__metadata__") continue;
            if (p.Name.EndsWith(".alpha", StringComparison.Ordinal)) {
                var key = p.Name[..^6]; if (!header.TryGetProperty(key + ".lora_A.weight", out _) && !header.TryGetProperty(key + ".lora_down.weight", out _)) Fail();
                if (p.Value.GetProperty("shape").GetArrayLength() > 1) Fail(); continue;
            }
            var isA = p.Name.EndsWith(".lora_A.weight", StringComparison.Ordinal);
            var isB = p.Name.EndsWith(".lora_B.weight", StringComparison.Ordinal);
            if (!isA && !isB) Fail();
            var keyName = p.Name[..^14];
            if (!signature!.TryGetValue(keyName, out var expected)) Fail();
            if (!header.TryGetProperty(keyName + ".lora_A.weight", out var left)) Fail();
            if (!header.TryGetProperty(keyName + ".lora_B.weight", out var right)) Fail();
            var a = left.GetProperty("shape").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var b = right.GetProperty("shape").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (a.Length != 2 || b.Length != 2 || a[0] is < 1 or > 512 || a[0] != b[1] || a[1] != expected![0] || b[0] != expected[1]
                || left.GetProperty("dtype").GetString() != right.GetProperty("dtype").GetString()) Fail();
            if (isA) pairs++;
        }
        if (pairs == 0) Fail();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new InvalidDataException("Generation.Lora.Incompatible");
}
