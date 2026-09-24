using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services.LiteraryImport;

public static class ImportBookExporter
{
    public static void Export(string root, string destination, bool russian, CancellationToken ct)
    {
        var store = new LiteraryChapterStore(root); store.Open();
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = WordprocessingDocument.Create(temporary, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart(); main.Document = new Document(new Body());
                var body = main.Document.Body!;
                body.Append(new Paragraph(new Run(new Text(russian
                    ? "Импортированный черновик. Жёлтым отмечены фрагменты для проверки. Альтернативы и история обработки — в XLSX."
                    : "Imported draft. Yellow passages require review. Alternatives and processing history are in the XLSX."))));
                if (File.Exists(Path.Combine(root, "Import", "book-review.json"))
                    || File.Exists(Path.Combine(root, "Import", "book-review.json.bak")))
                    ImportReviewBookDocx.Append(body, new ImportReviewBookStore(root).Load(), ct);
                else foreach (var group in store.Index.Parts.Where(p => !string.IsNullOrWhiteSpace(
                    LiteraryChapterFiles.Read(Path.Combine(root, "chapters", p.FileName)))).GroupBy(p => p.Chapter))
                {
                    ct.ThrowIfCancellationRequested();
                    body.Append(new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new RunProperties(new Bold()), new Text(group.First().Title))));
                    if (group.Any(p => ImportEligibility.Review(root, p.Id)?.Doubts.Length > 0))
                        body.Append(new Paragraph(new Run(new RunProperties(new Bold(), new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "8B2500" }),
                            new Text(russian ? "Проверить: жёлтые фрагменты этой главы требуют решения автора." : "Review: yellow passages in this chapter require the author's decision."))));
                    var paragraph = new Paragraph(); body.Append(paragraph);
                    var firstPart = true;
                    foreach (var part in group.OrderBy(p => p.Part))
                    {
                        if (!firstPart && !part.ExactContinuation) { paragraph = new Paragraph(); body.Append(paragraph); }
                        firstPart = false;
                        var text = LiteraryChapterFiles.Read(Path.Combine(root, "chapters", part.FileName));
                        var review = ImportEligibility.Review(root, part.Id);
                        var spans = review?.Doubts ?? [];
                        if (spans.Length > 0 && review!.Revision != LiteraryWorkIndex.Revision(text)) spans = [new(0, text.Length, "", "edited")];
                        var boundaries = spans.SelectMany(s => new[] { s.Start, s.Start + s.Length }).Append(0).Append(text.Length).Distinct().Order().ToArray();
                        for (var n = 0; n < boundaries.Length - 1; n++)
                        {
                            var start = boundaries[n]; var end = boundaries[n + 1];
                            var doubt = spans.Any(s => start >= s.Start && start < s.Start + s.Length);
                            var lines = text[start..end].Split('\n');
                            for (var line = 0; line < lines.Length; line++)
                            {
                                if (line > 0) { paragraph = new Paragraph(); body.Append(paragraph); }
                                var run = new Run();
                                if (doubt) run.Append(new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "8B2500" }, new Highlight { Val = HighlightColorValues.Yellow }));
                                run.Append(new Text(lines[line].TrimEnd('\r')) { Space = SpaceProcessingModeValues.Preserve }); paragraph.Append(run);
                            }
                        }
                    }
                }
                main.Document.Save();
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
