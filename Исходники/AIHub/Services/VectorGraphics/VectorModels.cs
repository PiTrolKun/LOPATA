using System.IO;
using System.Text.Json;

namespace AIHub.Services.VectorGraphics;

/// <summary>Supported SVG model. Historical requests keep their original model identifier.</summary>
public static class VectorModels
{
    public const string Ridge = "ridge";
    public static string Validate(string id) => id == Ridge ? id : throw new InvalidDataException("Vector.Error.Model");
    public static string Name(string id) { _ = Validate(id); return LiteraryModelLocation.DisplayName; }
    public static string ArtifactId(string id) { _ = Validate(id); return ManagedModelCatalog.OmniGammaArtifactId; }
    public static bool CanGenerate(string id) => id == Ridge;
    internal static string RestorePreparationModel(JsonElement input) => input.TryGetProperty("ModelId", out var model)
        ? Validate(model.GetString() ?? "") : Ridge;

}
