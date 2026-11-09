using System.IO;
using System.Xml;
using System.Xml.Linq;
using AIHub.Models;

namespace AIHub.Services;

internal static class ImageUtilityProvenance
{
    internal static OutputProvenance Create(ImageUtilityOptions options, ImageUtilityMethod method)
    {
        var ai = !options.FormatOnly && method.IsAi;
        var requested = options.Parameters.GetValueOrDefault("model");
        var variant = !string.IsNullOrWhiteSpace(requested) ? requested : method.Id switch
        {
            "real-esrgan" => "realesrgan-x4plus", "real-cugan" => "models-se",
            "swinir" => "scale-" + (options.Parameters.GetValueOrDefault("scale") is { Length: > 0 } scale ? scale : "4"),
            _ => method.Id
        };
        return new()
        {
            Scenario = "image-utility/" + (options.FormatOnly ? "conversion" : "resize/" + method.Id),
            ModelHistory = ai ? "current-operation-recorded; prior-history-unknown" : "prior-history-unknown",
            Models = ai ? [new(method.Id + "/" + variant, variant, "", "image upscaling; revision unknown")] : []
        };
    }

    internal static bool SupportsXmp(string format) => format is "png" or "jpeg" or "webp" or "tiff"
        or "avif" or "jxl" or "jp2" or "psd" or "psb";

    internal static string MergeXmp(string? sourcePath, OutputProvenance facts)
    {
        var result = XDocument.Parse(facts.Xmp());
        if (sourcePath is null || !File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0) return result.ToString();
        if (new FileInfo(sourcePath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Oversized source XMP.");
        using var reader = XmlReader.Create(sourcePath, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var original = XDocument.Load(reader);
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        XNamespace lopata = "https://lopata.app/ns/provenance/1.0/";
        var container = original.Descendants(rdf + "RDF").FirstOrDefault()
            ?? throw new InvalidDataException("Unsupported source XMP.");
        // Update producer facts while preserving authors, rights, descriptions and other profiles.
        original.Descendants(xmp + "CreatorTool").Remove();
        original.Descendants(lopata + "Provenance").Remove();
        original.Descendants().Attributes(xmp + "CreatorTool").Remove();
        original.Descendants().Attributes(lopata + "Provenance").Remove();
        container.Add(result.Descendants(rdf + "Description").First());
        return original.ToString(SaveOptions.DisableFormatting);
    }
}
