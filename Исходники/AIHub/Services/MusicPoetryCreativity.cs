using System.Globalization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Per-model temperature choice; defaults remain the existing sampler profile.</summary>
public static class MusicPoetryCreativity
{
    public const double Minimum = .1, Maximum = 2, Step = .1;
    public static string? ModelId(DebugModelInfo? model) => model is null ? null : MusicPoetryModelCatalog.Find(model)?.Id;
    public static bool IsValid(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;
    public static double Default(string modelId)
    {
        if (!MusicPoetryModelCatalog.All.Any(c => c.Id == modelId)) throw new ArgumentException("Unknown songwriting model.", nameof(modelId));
        return MusicPoetryProfileCatalog.Find(modelId)?.Temperature ?? .8;
    }
    public static double Resolve(DebugModelInfo model, double? selected = null)
    {
        var id = ModelId(model) ?? throw new ArgumentException("Unknown songwriting model.", nameof(model));
        var value = selected ?? Default(id);
        if (!IsValid(value)) throw new ArgumentOutOfRangeException(nameof(selected));
        return value;
    }
    public static bool TryParse(string text, out double value) => double.TryParse(text.Trim().Replace(',', '.'),
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && IsValid(value);
    public static string Format(double value, string language) => value.ToString("0.################",
        CultureInfo.GetCultureInfo(language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US"));
}
