using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageBatchExporter
{
    public static bool IsPresent(string directory, ImageBatchJob job) => job.SingleDocument
        ? File.Exists(Path.Combine(directory, "Descriptions.docx"))
        : Directory.Exists(directory) && job.Items.Where(i => i.Status == "ready").All(i =>
            Directory.EnumerateFiles(directory, "*.md").Any(p => Path.GetFileName(p).EndsWith("-" + ImageBatchStore.Key(i.Id) + ".md", StringComparison.Ordinal)));
    public static string Preview(IEnumerable<(ImageBatchItem Item, ImageBatchSection Section)> sections) =>
        string.Join("\n\n", sections.Select(s => s.Section.Title + "\n" + s.Item.File.DisplayName + "\n\n" + string.Join("\n\n", s.Section.Paragraphs)));

    public static void Save(string directory, IReadOnlyList<(ImageBatchItem Item, ImageBatchSection Section)> sections, bool single, string language)
    {
        Directory.CreateDirectory(directory);
        if (!single)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var (item, section) = sections[i];
                var name = string.Concat(Path.GetFileNameWithoutExtension(item.File.DisplayName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                if (name.Length > 70) name = name[..70];
                ImageBatchStore.WriteText(Path.Combine(directory, $"{item.Position:D4}-{name}-{ImageBatchStore.Key(item.Id)}.md"),
                    $"# {section.Title}\n\n{item.File.DisplayName}\n\n" + string.Join("\n\n", section.Paragraphs));
            }
            return;
        }
        var destination = Path.Combine(directory, "Descriptions.docx");
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var document = WordprocessingDocument.Create(temp, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(new Body()); var body = main.Document.Body!;
                bool en = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
                body.Append(Heading(en ? "Image descriptions" : "Литературные описания изображений", "Title"));
                body.Append(Heading(en ? "Contents" : "Оглавление", "Heading1"));
                for (int i = 0; i < sections.Count; i++)
                    body.Append(new Paragraph(new Hyperlink(new Run(new Text($"{i + 1}. {sections[i].Section.Title}"))) { Anchor = "section" + i }));
                for (int i = 0; i < sections.Count; i++)
                {
                    var (item, section) = sections[i];
                    var heading = Heading(section.Title, "Heading1");
                    heading.InsertAfter(new BookmarkStart { Id = i.ToString(), Name = "section" + i }, heading.ParagraphProperties);
                    heading.Append(new BookmarkEnd { Id = i.ToString() });
                    body.Append(heading, new Paragraph(new Run(new RunProperties(new Italic()), new Text(item.File.DisplayName))));
                    foreach (var paragraph in section.Paragraphs) body.Append(new Paragraph(new Run(new Text(paragraph) { Space = SpaceProcessingModeValues.Preserve })));
                }
                var styles = main.AddNewPart<StyleDefinitionsPart>();
                styles.Styles = new Styles(
                    new Style(new StyleName { Val = "Title" }, new StyleRunProperties(new Bold(), new FontSize { Val = "36" })) { Type = StyleValues.Paragraph, StyleId = "Title" },
                    new Style(new StyleName { Val = "Heading 1" }, new StyleParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }, new OutlineLevel { Val = 0 }), new StyleRunProperties(new Bold(), new FontSize { Val = "28" })) { Type = StyleValues.Paragraph, StyleId = "Heading1" });
                main.Document.Save();
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static Paragraph Heading(string text, string style) => new(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text)));
}
