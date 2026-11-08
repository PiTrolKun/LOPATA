using System.IO;
using System.IO.Compression;
using System.Xml;
using AIHub.Services.Books;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;

namespace AIHub.Services.OfficeEditor;

internal sealed partial class OfficeTextWork
{
    // The material entry is immutable for the lifetime of the project. A checkpoint
    // must never turn a protected original into a writable source.
    private readonly Dictionary<string, bool> _editableNativeSources = new(StringComparer.Ordinal);

    internal string? NativeSectionExtension(string sheetId) => NativeSectionExtension(FindSheet(sheetId));

    internal static string? NativeSectionExtension(OfficeSheet sheet)
    {
        if (sheet.Kind is not (OfficeSheetKind.Book or OfficeSheetKind.Presentation) || sheet.Files.Length == 0) return null;
        var name = sheet.Files[0].Name;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return OfficeNativeDocument.Supports(extension) && ClassifySource(name) == sheet.Kind ? extension : null;
    }

    internal static string NativeSectionPath(string sheetId, string extension) => "sheets/" + sheetId + "/native" + extension;

    internal Task<byte[]> ReadNativeSectionAsync(string sheetId, CancellationToken token) =>
        LockedResultAsync(() => NativeSectionBytes(FindSheet(sheetId)), token);

    private byte[] NativeSectionBytes(OfficeSheet sheet, bool original = false)
    {
        var extension = NativeSectionExtension(sheet) ?? throw new InvalidDataException("OfficeEditor.Unsupported");
        using var zip = ZipFile.OpenRead(TemporaryProjectPath);
        var entry = original ? null : zip.GetEntry(NativeSectionPath(sheet.Id, extension));
        return ReadBytes(entry ?? zip.GetEntry(sheet.Files[0].Entry) ?? throw new InvalidDataException("OfficeEditor.InvalidPackage"));
    }

    private static bool CanEditNative(byte[] input, string extension)
    {
        try
        {
            var document = OfficeNativeDocument.Open(input, extension);
            return document.CanEdit;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or XmlException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[] ReadBoundedNativeFile(string path)
    {
        if (new FileInfo(path).Length > OfficeTextPackage.MaxBytes) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        return File.ReadAllBytes(path);
    }

    private OutputProvenance? InspectNativeInput(OfficeSheet sheet, string path, string extension, out bool editable)
        => InspectNativeInput(sheet, ReadBoundedNativeFile(path), extension, out editable);

    private OutputProvenance? InspectNativeInput(OfficeSheet sheet, byte[] source, string extension, out bool editable)
    {
        editable = CanEditNative(source, extension);
        OutputProvenance? facts = null;
        if (editable)
        {
            try
            {
                using var input = new MemoryStream(source, false);
                facts = ReadNativeSectionFacts(input, extension);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or XmlException or FormatException or OpenXmlPackageException)
            { editable = false; }
        }
        _editableNativeSources[sheet.Files[0].Entry] = editable;
        return facts;
    }

    internal Task<bool> CanEditNativeSheetAsync(string sheetId, CancellationToken token) => LockedResultAsync(() =>
    {
        var sheet = FindSheet(sheetId);
        return NativeSectionExtension(sheet) is { } extension && CanEditNativeSource(sheet, extension);
    }, token);

    private bool CanEditNativeSource(OfficeSheet sheet, string extension)
    {
        var entry = sheet.Files[0].Entry;
        if (!_editableNativeSources.TryGetValue(entry, out var editable))
            InspectNativeInput(sheet, NativeSectionBytes(sheet, original: true), extension, out editable);
        return editable;
    }

    internal Task WriteNativeSectionAsync(string sheetId, byte[] content, CancellationToken token) => LockedAsync(() =>
    {
        var sheet = FindSheet(sheetId);
        var extension = NativeSectionExtension(sheet) ?? throw new InvalidDataException("OfficeEditor.Unsupported");
        if (!CanEditNativeSource(sheet, extension) || content.LongLength > OfficeTextPackage.MaxBytes ||
            OfficeNativeDocument.Open(content, extension).ReadOnly)
            throw new InvalidDataException("OfficeEditor.Unsupported");
        if (!content.AsSpan().SequenceEqual(NativeSectionBytes(sheet)))
        {
            token.ThrowIfCancellationRequested();
            Store(Document, OfficeSheets.Log(_manifest, "Edited", sheetId), replaceEntry: NativeSectionPath(sheetId, extension), content: content);
        }
        return Task.CompletedTask;
    }, token);

    internal Task ExportNativeSectionAsync(string sheetId, string path, CancellationToken token) => LockedAsync(() =>
    {
        RejectExportCollision(path);
        return WriteNativeSectionOutputAsync(FindSheet(sheetId), path, _manifest.Provenance! with { ExportedUtc = DateTimeOffset.UtcNow }, token);
    }, token);

    private async Task WriteNativeSectionOutputAsync(OfficeSheet sheet, string path, OutputProvenance project, CancellationToken token)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!OfficeNativeDocument.Supports(extension) || ClassifySource(path) != sheet.Kind)
            throw new InvalidDataException("OfficeEditor.Unsupported");
        byte[] bytes;
        if (sheet.Files.Length == 0)
        {
            using var zip = ZipFile.OpenRead(TemporaryProjectPath);
            var sections = OfficeSectionContent.Read(ReadBytes(zip.GetEntry(OfficeSheets.Payload(sheet))));
            bytes = OfficeNativeDocument.Create(extension, sections);
        }
        else
        {
            if (extension != NativeSectionExtension(sheet) || !CanEditNativeSource(sheet, extension))
                throw new InvalidDataException("OfficeEditor.Unsupported");
            bytes = NativeSectionBytes(sheet);
            if (OfficeNativeDocument.Open(bytes, extension).ReadOnly) throw new InvalidDataException("OfficeEditor.Unsupported");
        }
        var staged = OfficeTextPackage.StagePath(path);
        try
        {
            await File.WriteAllBytesAsync(staged, bytes, token);
            // Older project manifests may only know the attachment name. Recover the
            // original producer's model history from the actual book or presentation.
            using var input = new MemoryStream(bytes, false);
            var inherited = sheet.Files.Length == 0 ? null : ReadNativeSectionFacts(input, extension);
            var facts = FactsForSheet(sheet.Id, project, inherited);
            if (extension == ".pptx")
            {
                using (var file = PresentationDocument.Open(staged, true)) OutputOfficeMetadata.Write(file, facts);
                using var check = File.OpenRead(staged);
                if (ReadPresentationProvenance(check) != facts.Json()) throw new InvalidDataException("OfficeEditor.InvalidPackage");
            }
            else await BookOutputMetadata.WriteEditedNativeAsync(staged, extension, facts, token);
            token.ThrowIfCancellationRequested();
            OfficeTextPackage.Publish(staged, path);
        }
        finally { File.Delete(staged); }
    }

    private static string? ReadPresentationProvenance(Stream input)
    {
        using var document = PresentationDocument.Open(input, false);
        return document.CustomFilePropertiesPart?.Properties?.Elements<CustomDocumentProperty>()
            .FirstOrDefault(p => p.Name == "LOPATA.Provenance")?.InnerText;
    }

    private static OutputProvenance? ReadNativeSectionFacts(Stream input, string extension)
    {
        var raw = extension == ".pptx" ? ReadPresentationProvenance(input) : BookOutputMetadata.ReadNativeProvenance(input, extension);
        if (raw == null) return null;
        var facts = ParseFacts(raw);
        if (facts?.Models == null || facts.Models.Any(model => model == null)) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        _ = facts.Json();
        return facts;
    }
}
