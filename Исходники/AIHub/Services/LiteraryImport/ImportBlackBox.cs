using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Services.LiteraryImport;

public static class ImportBlackBox
{
    public static void Export(ImportSession session, string destination, CancellationToken ct)
    {
        var artifacts = session.State.Artifacts.ToArray();
        ImportDisk.Require(Path.GetDirectoryName(Path.GetFullPath(destination))!, artifacts.Sum(a => a.Bytes) * 6);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart(); workbook.Workbook = new Workbook(new Sheets());
                WriteSheet(workbook, "Описание", [
                    ["Поле / Field", "Значение / Value"], ["Format", "LOPATA import black box v1"],
                    ["Session", session.State.Id], ["Source SHA256", session.State.SourceHash], ["Stage", session.State.Stage],
                    ["RAG", session.State.RagStatus], ["Jelly", session.State.MemoryStatus],
                    ["Recovery", "Точные данные / Raw: group rows by artifact ID; decode each Base64 part in numeric order, concatenate bytes without separators; verify byte count and SHA256 from Шаги / Steps."],
                    ["Privacy", "Личные исходники и ответы модели. Не публиковать. / Private source and model responses. Do not publish."],
                    ["Scope", "Text artifacts are embedded losslessly. Binary attachments remain in the unchanged original ZIP."],
                    ["Display", "Readable cells are previews in numbered chunks. Exact bytes, including unsupported XML characters, are in Raw sheets."]
                ], ct);
                WriteSheet(workbook, "Шаги", Steps(), ct);
                WriteSheet(workbook, "Связи", new[] { new[] { "Artifact ID", "Parent ID" } }
                    .Concat(artifacts.SelectMany(a => a.Parents.Select(parent => new[] { a.Id, parent }))), ct);
                WriteSheet(workbook, "Исходник", Readable(a => a.Step.StartsWith("source") || a.Step.StartsWith("normalized")), ct);
                WriteSheet(workbook, "Входы", Readable(a => a.Step.EndsWith("/request") || a.Step.EndsWith("/settings")), ct);
                WriteSheet(workbook, "Ответы", Readable(a => a.Step.EndsWith("/answer") || a.Step.EndsWith("/raw-response") || a.Status != "complete"), ct);
                WriteSheet(workbook, "Изменения", Readable(a => !a.Step.Contains("/request") && !a.Step.Contains("/raw-response") && !a.Step.StartsWith("source")), ct);
                WriteSheet(workbook, "Сборка", Readable(a => a.Step.StartsWith("assembly") || a.Step.StartsWith("project")), ct);
                WriteSheet(workbook, "Сомнения", Doubts(), ct);
                WriteSheet(workbook, "Точные данные", Raw(), ct);
                workbook.Workbook.Save();
            }
            Verify(temporary, artifacts, ct);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        IEnumerable<string[]> Steps()
        {
            yield return ["ID", "Step", "Status", "Bytes", "SHA256", "Created UTC", "Parent IDs"];
            foreach (var a in artifacts)
            {
                var parents = string.Join(",", a.Parents);
                yield return [a.Id, a.Step, a.Status, a.Bytes.ToString(CultureInfo.InvariantCulture), a.Sha256, a.Created.ToString("O"), parents.Length <= 30000 ? parents : "See Связи / Links"];
            }
        }
        IEnumerable<string[]> Readable(Func<ImportArtifact, bool> predicate)
        {
            yield return ["ID", "Step", "Part", "Text (display; exact bytes in Raw)"];
            foreach (var a in artifacts.Where(predicate))
            {
                using var reader = new StreamReader(session.ArtifactPath(a), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                var part = 0;
                while (!reader.EndOfStream)
                {
                    var text = new StringBuilder(); var lines = 0;
                    while (text.Length < 29998 && lines < 250 && reader.Read() is var c && c >= 0)
                    {
                        text.Append((char)c); if (c == '\n') lines++;
                        if (char.IsHighSurrogate((char)c) && reader.Peek() is var low && low >= 0 && char.IsLowSurrogate((char)low)) text.Append((char)reader.Read());
                    }
                    yield return [a.Id, a.Step, (++part).ToString(CultureInfo.InvariantCulture), SafeXml(text.ToString())];
                }
            }
        }
        IEnumerable<string[]> Raw()
        {
            yield return ["Artifact ID", "Part", "Parts", "Encoding", "Base64 bytes"];
            var buffer = new byte[22500];
            foreach (var a in artifacts)
            {
                using var file = File.OpenRead(session.ArtifactPath(a));
                var count = Math.Max(1, (a.Bytes + buffer.Length - 1) / buffer.Length);
                for (long n = 1; n <= count; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = file.ReadAtLeast(buffer, (int)Math.Min(buffer.Length, a.Bytes - file.Position), false);
                    yield return [a.Id, n.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture), "base64", Convert.ToBase64String(buffer, 0, bytes)];
                }
            }
        }
        IEnumerable<string[]> Doubts()
        {
            yield return ["Unit ID", "Kind", "Chapter", "Reason"];
            foreach (var a in artifacts.Where(a => a.Step.StartsWith("assembly/")))
            {
                var decisions = System.Text.Json.JsonSerializer.Deserialize<ImportDecision[]>(File.ReadAllText(session.ArtifactPath(a)), ImportJson.Options)!;
                foreach (var d in decisions.Where(d => d.Kind is "DOUBT" or "ALT")) yield return [d.Id, d.Kind, d.Chapter, SafeXml(d.Reason)];
            }
        }
    }
    private static string SafeXml(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (XmlConvert.IsXmlChar(value[i])) builder.Append(value[i]);
            else if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) builder.Append(value[i]).Append(value[++i]);
            else builder.Append('\uFFFD');
        }
        return builder.ToString();
    }
    private static void WriteSheet(WorkbookPart workbook, string name, IEnumerable<string[]> rows, CancellationToken ct)
    {
        OpenXmlWriter? writer = null; var rowCount = 0; var page = 0;
        try
        {
            foreach (var values in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (writer is null || rowCount >= 1048576)
                {
                    if (writer is not null) { writer.WriteEndElement(); writer.WriteEndElement(); writer.Dispose(); }
                    var part = workbook.AddNewPart<WorksheetPart>();
                    var sheets = workbook.Workbook!.GetFirstChild<Sheets>()!;
                    sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = (uint)sheets.ChildElements.Count + 1, Name = name + (++page > 1 ? " " + page : "") });
                    writer = OpenXmlWriter.Create(part);
                    writer.WriteStartElement(new Worksheet());
                    writer.WriteElement(new SheetViews(new SheetView { WorkbookViewId = 0U }));
                    writer.WriteElement(new Columns(new Column { Min = 1, Max = 7, Width = 34, CustomWidth = true }));
                    writer.WriteStartElement(new SheetData()); rowCount = 0;
                }
                writer.WriteStartElement(new Row { RowIndex = (uint)++rowCount });
                foreach (var value in values)
                {
                    if (value.Length > 32767) throw new InvalidDataException("Spreadsheet cell is too long.");
                    writer.WriteElement(new Cell(new InlineString(new Text(SafeXml(value)) { Space = SpaceProcessingModeValues.Preserve })) { DataType = CellValues.InlineString });
                }
                writer.WriteEndElement();
            }
            writer?.WriteEndElement(); writer?.WriteEndElement();
        }
        finally { writer?.Dispose(); }
    }
    public static void Verify(string path, IReadOnlyList<ImportArtifact> expected, CancellationToken ct)
    {
        using var book = SpreadsheetDocument.Open(path, false);
        var workbook = book.WorkbookPart!; var seen = new HashSet<string>();
        IncrementalHash? hash = null; ImportArtifact? current = null; long length = 0, next = 1, total = 0;
        void Finish()
        {
            if (current is null) return;
            if (next - 1 != total || length != current.Bytes || Convert.ToHexString(hash!.GetHashAndReset()) != current.Sha256)
                throw new InvalidDataException("XLSX exact artifact verification failed.");
            hash!.Dispose(); hash = null;
        }
        try
        {
            foreach (var sheet in workbook.Workbook!.Sheets!.Elements<Sheet>().Where(s => s.Name!.Value!.StartsWith("Точные данные", StringComparison.Ordinal)))
            {
                using var reader = OpenXmlReader.Create((WorksheetPart)workbook.GetPartById(sheet.Id!));
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
                    var cells = ((Row)reader.LoadCurrentElement()!).Elements<Cell>().Select(c => c.InnerText).ToArray();
                    if (cells[0] == "Artifact ID") continue;
                    if (current?.Id != cells[0])
                    {
                        Finish(); if (!seen.Add(cells[0])) throw new InvalidDataException("Duplicated artifact.");
                        current = expected.Single(a => a.Id == cells[0]); hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        next = 1; length = 0; total = long.Parse(cells[2], CultureInfo.InvariantCulture);
                    }
                    if (long.Parse(cells[1], CultureInfo.InvariantCulture) != next++ || long.Parse(cells[2], CultureInfo.InvariantCulture) != total)
                        throw new InvalidDataException("Non-contiguous artifact parts.");
                    var bytes = Convert.FromBase64String(cells[4]); length += bytes.Length; hash!.AppendData(bytes);
                }
            }
            Finish();
            if (seen.Count != expected.Count) throw new InvalidDataException("Missing XLSX artifacts.");
        }
        finally { hash?.Dispose(); }
    }
}
