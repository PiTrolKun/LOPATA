using System.IO;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.CustomProperties;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Services.OfficeEditor;

internal sealed partial class OfficeTextWork
{
    private static string WorkbookPath(string id) => "sheets/" + id + "/workbook.xlsx";

    private static bool ProtectedWorkbook(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase));
    }

    private static OutputProvenance? ReadWorkbookFacts(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var properties = archive.GetEntry("docProps/custom.xml");
        if (properties == null || properties.Length > 4 * 1024 * 1024) return null;
        using var stream = properties.Open();
        using var xml = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings {
            XmlResolver = null, DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var root = System.Xml.Linq.XDocument.Load(xml);
        return ParseFacts(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "property" &&
            (string?)e.Attribute("name") == "LOPATA.Provenance")?.Value);
    }

    internal bool IsWorkbookSheet(string id)
    {
        var sheet = FindSheet(id);
        return sheet.Kind == OfficeSheetKind.Table && (sheet.Files.Length == 0 ||
            sheet.Files.Any(f => Path.GetExtension(f.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)));
    }

    internal Task<byte[]> ReadWorkbookAsync(string sheetId, CancellationToken token)
        => LockedResultAsync(() => WorkbookBytes(sheetId), token);

    private byte[] WorkbookBytes(string id)
    {
        if (!IsWorkbookSheet(id)) throw new InvalidDataException("OfficeEditor.Unsupported");
        using var zip = ZipFile.OpenRead(TemporaryProjectPath);
        var working = zip.GetEntry(WorkbookPath(id));
        if (working != null) return ReadBytes(working);
        var original = FindSheet(id).Files.FirstOrDefault(f => Path.GetExtension(f.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase));
        if (original != null)
            return ReadBytes(zip.GetEntry(original.Entry) ?? throw new InvalidDataException("OfficeEditor.InvalidPackage"));
        return EmptyWorkbook();
    }

    internal static byte[] EmptyWorkbook(string firstName = "Sheet1")
    {
        using var output = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook))
        {
            var part = document.AddWorkbookPart();
            var page = part.AddNewPart<WorksheetPart>();
            page.Worksheet = new S.Worksheet(new S.SheetData());
            part.Workbook = new S.Workbook(new S.Sheets(new S.Sheet { SheetId = 1, Name = firstName, Id = part.GetIdOfPart(page) }));
        }
        return output.ToArray();
    }

    internal Task WriteWorkbookAsync(string sheetId, byte[] xlsx, CancellationToken token) => LockedAsync(() =>
    {
        if (!IsWorkbookSheet(sheetId) || xlsx.LongLength > OfficeTextPackage.MaxBytes)
            throw new InvalidDataException("OfficeEditor.Unsupported");
        using (var document = SpreadsheetDocument.Open(new MemoryStream(xlsx, false), false))
            if (document.DocumentType != SpreadsheetDocumentType.Workbook || document.WorkbookPart?.Workbook == null)
                throw new InvalidDataException("OfficeEditor.InvalidPackage");
        if (WorkbookBytes(sheetId).AsSpan().SequenceEqual(xlsx)) return Task.CompletedTask;
        Store(Document, OfficeSheets.Log(_manifest, "Edited", sheetId), replaceEntry: WorkbookPath(sheetId), content: xlsx);
        return Task.CompletedTask;
    }, token);

    private Task ExportPrimaryAsync(string path, OutputProvenance facts, CancellationToken token)
    {
        if (Sheets[0].Kind == OfficeSheetKind.Text)
            return OfficeTextFormats.ExportAsync(Document, path, _draft.DirectoryPath, FactsForSheet(OfficeSheets.PrimaryId, facts), token);
        if (Sheets[0].Kind is OfficeSheetKind.Book or OfficeSheetKind.Presentation)
            return WriteNativeSectionOutputAsync(Sheets[0], path, facts, token);
        return WriteWorkbookOutputAsync(OfficeSheets.PrimaryId, path, facts, token);
    }

    private OutputProvenance FactsForSheet(string sheetId, OutputProvenance project, OutputProvenance? inherited = null)
    {
        var sheet = FindSheet(sheetId);
        var origin = sheet.SourceProvenance;
        var activity = AiEntries.Where(e => e.SheetId == sheetId).ToArray();
        var models = (origin?.Models ?? []).Concat(inherited?.Models ?? []).Concat(activity.SelectMany(e => e.Models)).Distinct().ToArray();
        var history = models.Length > 0 ? "recorded" : activity.Length > 0 ? "unknown" :
            inherited?.ModelHistory ?? origin?.ModelHistory ?? (sheet.Files.Length == 0 ? "not-applicable" : "unknown");
        return project with { ModelScope = "output", ModelHistory = history, Models = models,
            CreatedUtc = inherited?.CreatedUtc ?? origin?.CreatedUtc ?? project.CreatedUtc };
    }

    internal Task ExportWorkbookAsync(string id, string path, CancellationToken token)
        => LockedAsync(async () =>
        {
            RejectExportCollision(path);
            await WriteWorkbookOutputAsync(id, path, _manifest.Provenance! with { ExportedUtc = DateTimeOffset.UtcNow }, token);
            CollectionExportJournal.Record(path, _manifest.Provenance!.Scenario);
        }, token);

    private async Task WriteWorkbookOutputAsync(string id, string path, OutputProvenance facts, CancellationToken token)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OfficeEditor.Unsupported");
        var bytes = WorkbookBytes(id);
        facts = FactsForSheet(id, facts);
        var staged = OfficeTextPackage.StagePath(path);
        try
        {
            await File.WriteAllBytesAsync(staged, bytes, token);
            using (var doc = SpreadsheetDocument.Open(staged, true)) OutputOfficeMetadata.Write(doc, facts);
            using (var check = SpreadsheetDocument.Open(staged, false))
                if (check.CustomFilePropertiesPart?.Properties?.Elements<CustomDocumentProperty>()
                    .SingleOrDefault(p => p.Name == "LOPATA.Provenance")?.InnerText != facts.Json())
                    throw new InvalidDataException("OfficeEditor.InvalidPackage");
            token.ThrowIfCancellationRequested();
            OfficeTextPackage.Publish(staged, path);
        }
        finally { File.Delete(staged); }
    }

    internal Task ExportDocumentBytesAsync(byte[] docx, string path, CancellationToken token, string? sheetId = null)
        => LockedAsync(async () =>
        {
            RejectExportCollision(path);
            var facts = FactsForSheet(sheetId ?? OfficeSheets.PrimaryId, _manifest.Provenance! with { ExportedUtc = DateTimeOffset.UtcNow });
            await OfficeTextFormats.ExportAsync(docx, path, _draft.DirectoryPath, facts, token);
            CollectionExportJournal.Record(path, facts.Scenario);
        }, token);

    internal Task ExportSheetDocumentAsync(string id, string path, CancellationToken token)
        => LockedAsync(async () =>
        {
            RejectExportCollision(path);
            var sheet = FindSheet(id);
            if (sheet.Kind != OfficeSheetKind.Text) throw new InvalidDataException("OfficeEditor.Unsupported");
            using var zip = ZipFile.OpenRead(TemporaryProjectPath);
            var content = id == OfficeSheets.PrimaryId ? Document : ReadBytes(zip.GetEntry(OfficeSheets.Payload(sheet)));
            var facts = FactsForSheet(id, _manifest.Provenance! with { ExportedUtc = DateTimeOffset.UtcNow });
            await OfficeTextFormats.ExportAsync(content, path, _draft.DirectoryPath, facts, token);
            CollectionExportJournal.Record(path, facts.Scenario);
        }, token);

    private void RejectExportCollision(string path)
    {
        if (SameFile(SourcePath,path) || SameFile(TargetPath,path) || SameFile(TemporaryProjectPath,path))
            throw new IOException("OfficeEditor.Unsupported");
    }
}
