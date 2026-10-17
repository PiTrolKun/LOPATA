using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.VariantTypes;

namespace AIHub.Services;

public static class OutputOfficeMetadata
{
    public static void Write(WordprocessingDocument doc, OutputProvenance facts, string? author = null)
    {
        var extended = doc.ExtendedFilePropertiesPart ?? doc.AddExtendedFilePropertiesPart();
        extended.Properties ??= new();
        extended.Properties.Application = new("LOPATA");
        var custom = doc.CustomFilePropertiesPart ?? doc.AddCustomFilePropertiesPart();
        Write(doc, custom, facts, author);
    }
    public static void Write(SpreadsheetDocument doc, OutputProvenance facts)
    {
        var extended = doc.ExtendedFilePropertiesPart ?? doc.AddExtendedFilePropertiesPart();
        extended.Properties ??= new();
        extended.Properties.Application = new("LOPATA");
        Write(doc, doc.CustomFilePropertiesPart ?? doc.AddCustomFilePropertiesPart(), facts, null);
    }
    private static void Write(OpenXmlPackage doc, CustomFilePropertiesPart custom, OutputProvenance facts, string? author)
    {
        if (!string.IsNullOrWhiteSpace(author)) doc.PackageProperties.Creator = author;
        doc.PackageProperties.LastModifiedBy = "LOPATA " + facts.ProgramVersion;
        doc.PackageProperties.Modified = facts.ExportedUtc.UtcDateTime;
        custom.Properties ??= new Properties();
        foreach (var old in custom.Properties.Elements<CustomDocumentProperty>().Where(p => p.Name == "LOPATA.Provenance").ToArray()) old.Remove();
        var id = custom.Properties.Elements<CustomDocumentProperty>().Select(p => p.PropertyId?.Value ?? 1).DefaultIfEmpty(1).Max() + 1;
        custom.Properties.Append(new CustomDocumentProperty(new VTLPWSTR(facts.Json()))
        { FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}", PropertyId = id, Name = "LOPATA.Provenance" });
        custom.Properties.Save();
    }
}
