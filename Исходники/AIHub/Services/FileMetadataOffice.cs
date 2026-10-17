using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace AIHub.Services;

internal static class FileMetadataOffice
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    internal static readonly Dictionary<string, XName> Names = new()
    { ["title"]=Dc+"title", ["author"]=Dc+"creator", ["comment"]=Dc+"description", ["keywords"]=Cp+"keywords" };
    internal static string? CoreName(ZipArchive zip)
    {
        if(zip.GetEntry("word/document.xml") is null)return null;
        var entry=zip.GetEntry("_rels/.rels");if(entry is null||entry.Length>1024*1024)return null;
        using var stream=entry.Open();var relationships=XDocument.Load(stream,LoadOptions.PreserveWhitespace);
        var core=relationships.Root?.Elements().Where(e=>(string?)e.Attribute("Type")=="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties").ToArray();
        if(core?.Length!=1||(string?)core[0].Attribute("TargetMode")=="External")return null;
        var name=Uri.UnescapeDataString((string?)core[0].Attribute("Target")??"").TrimStart('/');
        return name.Length==0||name.Contains('\\')||name.Split('/').Any(s=>s is ".." or "."||s.Contains(':'))?null:name;
    }
    internal static (Dictionary<string,string> Values, bool Writable) Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 20000 || zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.Ordinal).Count()!=zip.Entries.Count
            ||zip.Entries.Any(e => e.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase))) return ([], false);
        long remaining=8*1024*1024;
        foreach(var relation in zip.Entries.Where(e=>e.FullName.EndsWith(".rels",StringComparison.OrdinalIgnoreCase)))
        {
            if(relation.Length>1024*1024||(remaining-=relation.Length)<0)return([],false);
            using var rel=relation.Open();var relationships=XDocument.Load(rel,LoadOptions.PreserveWhitespace);
            if(relationships.Root?.Elements().Any(e=>((string?)e.Attribute("Type"))?.Contains("digital-signature",StringComparison.OrdinalIgnoreCase)==true)==true)return([],false);
        }
        var core=CoreName(zip);var matches = zip.Entries.Where(e => e.FullName == core).ToArray();
        if (matches.Length != 1 || matches[0].Length > 1024*1024) return ([], false);
        using var stream = matches[0].Open(); var document = XDocument.Load(stream,LoadOptions.PreserveWhitespace);
        return (Names.ToDictionary(p => p.Key, p => document.Root?.Element(p.Value)?.Value ?? ""), true);
    }
    internal static async Task WriteAsync(string path, IReadOnlyDictionary<string,string> values, CancellationToken token)
    {
        if (!Read(path).Writable || values.Keys.Any(k => !Names.ContainsKey(k))) throw new InvalidDataException("Metadata.Protected");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var name=CoreName(zip)??throw new InvalidDataException("Metadata.ReadOnly");
        var entry = zip.GetEntry(name)!; var timestamp = entry.LastWriteTime;
        XDocument document; using (var input = entry.Open()) document = XDocument.Load(input,LoadOptions.PreserveWhitespace);
        foreach (var pair in values) document.Root!.SetElementValue(Names[pair.Key], pair.Value);
        entry.Delete(); var replacement = zip.CreateEntry(name, CompressionLevel.Optimal); replacement.LastWriteTime = timestamp;
        await using var output = replacement.Open(); await document.SaveAsync(output, SaveOptions.DisableFormatting, token);
    }
}
