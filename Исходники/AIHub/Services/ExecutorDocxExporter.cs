using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services;

public static partial class ExecutorDocxExporter
{
    public static void Export(ExecutorResultSnapshot result, string destinationPath, OutputProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (string.IsNullOrWhiteSpace(result.Markdown))
        {
            throw new InvalidOperationException("The final executor result is empty.");
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The export directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDocument(temporaryPath, result.Markdown, provenance);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteDocument(string path, string markdown, OutputProvenance? provenance)
    {
        using var document = WordprocessingDocument.Create(
            path,
            WordprocessingDocumentType.Document);
        if (provenance is not null) OutputOfficeMetadata.Write(document, provenance);
        var mainPart = document.AddMainDocumentPart();
        AddStyles(mainPart);
        var body = new Body();
        mainPart.Document = new Document(body);

        var inCodeBlock = false;
        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            body.Append(inCodeBlock
                ? CreateCodeParagraph(line)
                : CreateMarkdownParagraph(line));
        }

        body.Append(new SectionProperties(
            new PageSize
            {
                Width = 11906,
                Height = 16838
            },
            new PageMargin
            {
                Top = 1134,
                Right = 1134,
                Bottom = 1134,
                Left = 1134,
                Header = 720,
                Footer = 720,
                Gutter = 0
            }));
        mainPart.Document.Save();
    }

    private static Paragraph CreateMarkdownParagraph(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new Paragraph();
        }

        var heading = HeadingRegex().Match(line);
        if (heading.Success)
        {
            var level = Math.Min(3, heading.Groups[1].Value.Length);
            return CreateParagraph(
                heading.Groups[2].Value,
                level == 1 ? "Title" : $"Heading{level - 1}");
        }

        var bullet = BulletRegex().Match(line);
        if (bullet.Success)
        {
            return CreateListParagraph("•", bullet.Groups[1].Value);
        }

        var numbered = NumberedRegex().Match(line);
        if (numbered.Success)
        {
            return CreateListParagraph(numbered.Groups[1].Value + ".", numbered.Groups[2].Value);
        }

        return CreateParagraph(line, "Normal");
    }

    private static Paragraph CreateParagraph(string text, string styleId)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = styleId }));
        AppendInlineRuns(paragraph, text);
        return paragraph;
    }

    private static Paragraph CreateListParagraph(string marker, string text)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "Normal" },
                new Indentation { Left = "720", Hanging = "360" }));
        paragraph.Append(CreateRun(marker + " ", bold: false));
        AppendInlineRuns(paragraph, text);
        return paragraph;
    }

    private static Paragraph CreateCodeParagraph(string text)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "Code" },
                new Shading { Fill = "EEF1F5" },
                new Indentation { Left = "360", Right = "360" }));
        paragraph.Append(new Run(
            new RunProperties(
                new RunFonts
                {
                    Ascii = "Cascadia Mono",
                    HighAnsi = "Cascadia Mono",
                    EastAsia = "Cascadia Mono"
                }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    private static void AppendInlineRuns(Paragraph paragraph, string text)
    {
        var index = 0;
        foreach (Match match in BoldRegex().Matches(text))
        {
            if (match.Index > index)
            {
                paragraph.Append(CreateRun(text[index..match.Index], bold: false));
            }

            paragraph.Append(CreateRun(match.Groups[1].Value, bold: true));
            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            paragraph.Append(CreateRun(text[index..], bold: false));
        }
    }

    private static Run CreateRun(string text, bool bold)
    {
        var run = new Run();
        if (bold)
        {
            run.Append(new RunProperties(new Bold()));
        }

        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            CreateStyle("Normal", "Обычный", 22, isDefault: true),
            CreateStyle("Title", "Заголовок документа", 36, bold: true),
            CreateStyle("Heading1", "Заголовок 1", 30, bold: true),
            CreateStyle("Heading2", "Заголовок 2", 26, bold: true),
            CreateStyle("Code", "Код", 20));
        stylesPart.Styles.Save();
    }

    private static Style CreateStyle(
        string id,
        string name,
        int fontSize,
        bool bold = false,
        bool isDefault = false)
    {
        var runProperties = new StyleRunProperties(
            new RunFonts
            {
                Ascii = "Segoe UI",
                HighAnsi = "Segoe UI",
                EastAsia = "Segoe UI"
            },
            new FontSize { Val = fontSize.ToString() });
        if (bold)
        {
            runProperties.Append(new Bold());
        }

        var style = new Style(new StyleName { Val = name })
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            Default = isDefault
        };
        if (!string.Equals(id, "Normal", StringComparison.Ordinal))
        {
            style.Append(new BasedOn { Val = "Normal" });
        }

        style.Append(new NextParagraphStyle { Val = "Normal" }, runProperties);
        return style;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-+*]\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex BoldRegex();
}
