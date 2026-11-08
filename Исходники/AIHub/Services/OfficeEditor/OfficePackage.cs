using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services.OfficeEditor;

/// <summary>Portable v1: an unmodified ODT payload plus public provenance. No executable state.</summary>
internal static class OfficePackage
{
    internal const string Extension = ".lopatadoxai";
    private const long Limit = 512L * 1024 * 1024;
    internal static string Extract(string path, string directory)
    {
        var info = OfficeTextPackage.Inspect(path);
        using var archive = ZipFile.OpenRead(path);
        Directory.CreateDirectory(directory);
        if (info.Schema < 3 || info.Sheets[0].Kind == OfficeSheetKind.Text)
        {
            var ext = info.Schema == 1 ? ".odt" : ".docx";
            var result = ExtractEntry(archive, "content/document" + ext, directory, ext);
            using var office = ZipFile.OpenRead(result);
            if (office.GetEntry(ext == ".odt" ? "content.xml" : "word/document.xml") == null)
                throw new InvalidDataException("OfficeEditor.InvalidPackage");
            return result;
        }
        var primary = info.Sheets[0];
        var workbook = "sheets/" + primary.Id + "/workbook.xlsx";
        if (primary.Kind == OfficeSheetKind.Table && archive.GetEntry(workbook) != null)
            return ExtractEntry(archive, workbook, directory, ".xlsx");
        if (OfficeTextWork.NativeSectionExtension(primary) is { } nativeExtension)
        {
            var native = OfficeTextWork.NativeSectionPath(primary.Id, nativeExtension);
            if (archive.GetEntry(native) != null) return ExtractEntry(archive, native, directory, nativeExtension);
        }
        var original = primary.Files.FirstOrDefault();
        if (original != null && primary.Kind != OfficeSheetKind.Archive)
            return ExtractEntry(archive, original.Entry, directory, Path.GetExtension(original.Name));
        if (primary.Kind == OfficeSheetKind.Table)
        {
            var target = Path.Combine(directory, "source.xlsx");
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            output.Write(OfficeTextWork.EmptyWorkbook());
            return target;
        }
        if (primary.Kind is OfficeSheetKind.Book or OfficeSheetKind.Presentation)
        {
            using var buffer = new MemoryStream();
            if (archive.GetEntry(OfficeSheets.Payload(primary)) is { } content)
            {
                using var stream = content.Open();
                OfficeTextPackage.CopyEntry(stream, buffer, content.Length);
            }
            var sections = OfficeSectionContent.Read(buffer.ToArray());
            var target = Path.Combine(directory, "source.docx");
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            output.Write(OfficeSectionContent.ToDocument(sections));
            return target;
        }
        // Archive primaries do not execute or unpack any attachments when opened by the reader.
        var listing = Path.Combine(directory, "source.txt");
        using (var text = new StreamWriter(new FileStream(listing, FileMode.CreateNew, FileAccess.Write)))
        {
            if (primary.Name.Length > 0) text.WriteLine(primary.Name);
            foreach (var material in primary.Files) text.WriteLine(material.Name);
        }
        return listing;
    }

    private static string ExtractEntry(ZipArchive zip, string member, string directory, string extension)
    {
        var entry = zip.GetEntry(member) ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        if (entry.Length > Limit) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var target = Path.Combine(directory, "source" + extension);
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        OfficeTextPackage.CopyEntry(input, output, entry.Length);
        return target;
    }

    internal static void Save(string path, string odt, OutputProvenance provenance)
    {
        using (var payload = ZipFile.OpenRead(odt))
            if (payload.GetEntry("content.xml") is null) throw new InvalidDataException("OfficeEditor.DraftFailed");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("manifest.json");
                    using (var stream = entry.Open()) JsonSerializer.Serialize(stream, new { Format = "LOPATAdoxAI", Schema = 1, Kind = "text", Provenance = JsonSerializer.Deserialize<JsonElement>(provenance.Json()) });
                    archive.CreateEntryFromFile(odt, "content/document.odt", CompressionLevel.Fastest);
                }
                file.Flush(true);
            }
            if (File.Exists(full)) File.Replace(temporary, full, full + ".bak");
            else File.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
