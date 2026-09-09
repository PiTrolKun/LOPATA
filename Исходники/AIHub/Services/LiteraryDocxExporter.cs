using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services;

public static class LiteraryDocxExporter
{
    public static void Export(IReadOnlyList<LiteraryExportChapter> chapters, string destination)
    {
        if (chapters.Count == 0) throw new InvalidOperationException("Literary.Editor.ExportEmpty");
        var path = Path.GetFullPath(destination);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var doc = WordprocessingDocument.Create(temporary, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart(); var body = new Body(); main.Document = new Document(body);
                foreach (var chapter in chapters.OrderBy(c => c.Number))
                {
                    var properties = new ParagraphProperties(new KeepNext());
                    if (body.HasChildren) properties.Append(new PageBreakBefore());
                    body.Append(new Paragraph(properties, new Run(new RunProperties(new Bold(), new FontSize { Val = "28" }), new Text(chapter.Title) { Space = SpaceProcessingModeValues.Preserve })));
                    var text = "";
                    foreach (var part in chapter.Parts)
                    {
                        if (part.Length == 0) continue;
                        if (text.Length > 0 && !text.EndsWith('\n') && !part.StartsWith('\n') && !part.StartsWith('\r')) text += "\n";
                        text += part;
                    }
                    foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    {
                        var run = new Run();
                        var segments = line.Split('\t');
                        for (int i = 0; i < segments.Length; i++)
                        { if (i > 0) run.Append(new TabChar()); run.Append(new Text(segments[i]) { Space = SpaceProcessingModeValues.Preserve }); }
                        body.Append(new Paragraph(run));
                    }
                }
                body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 720, Footer = 720, Gutter = 0 }));
                main.Document.Save();
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
