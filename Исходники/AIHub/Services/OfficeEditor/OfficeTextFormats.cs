using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using AIHub.Services.Books;
using AIHub.Services.OfficeReader;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services.OfficeEditor;

internal static class OfficeTextFormats
{
    private static readonly string[] ProtectedParts = ["word/charts/", "word/embeddings/", "word/diagrams/",
        "word/activeX/", "_xmlsignatures/", "word/footnotes.xml", "word/endnotes.xml", "word/vbaProject",
        "word/glossary/", "word/ink/", "word/afchunk", "word/commentsExtended", "word/commentsExtensible"];
    private static readonly HashSet<string> ProtectedElements = new(StringComparer.Ordinal)
        { "AlternateContent", "pict", "object", "altChunk", "sdt", "ins", "del", "moveFrom", "moveTo", "anchor", "documentProtection" };

    internal static bool ImportNeedsCopy(string? input)
    {
        var extension = Path.GetExtension(input)?.ToLowerInvariant();
        // Legacy Word and arbitrary HTML are import formats. In particular HTML
        // sanitization deliberately removes active material before conversion.
        if (extension is ".doc" or ".html" or ".htm") return true;
        if (extension != ".odt" || input == null) return false;
        using var archive = ZipFile.OpenRead(input);
        if (archive.Entries.Any(entry => entry.FullName.StartsWith("Object ", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))) return true;
        var entry = archive.GetEntry("content.xml") ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = OfficeTextPackage.MaxBytes });
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "tracked-changes" or "object" or "object-ole" or "custom-shape" or "forms" or "note") return true;
        return false;
    }

    internal static bool InspectDocx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, false));
        if (zip.GetEntry("word/document.xml") == null || zip.GetEntry("[Content_Types].xml") == null ||
            zip.Entries.Count > 20000 || zip.Entries.Sum(e => e.Length) > OfficeTextPackage.MaxBytes)
            throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var complex = zip.Entries.Any(e => ProtectedParts.Any(p => e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || Path.GetExtension(e.FullName).ToLowerInvariant() is ".wmf" or ".emf");
        if (HasExternalOfficeContent(zip, false)) complex = true;
        foreach (var item in zip.Entries.Where(e => e.FullName == "word/document.xml" || e.FullName == "word/settings.xml" ||
                     e.FullName.StartsWith("word/header", StringComparison.Ordinal) || e.FullName.StartsWith("word/footer", StringComparison.Ordinal)))
        {
            using var input = item.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = OfficeTextPackage.MaxBytes });
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && ProtectedElements.Contains(reader.LocalName)) complex = true;
        }
        return complex;
    }

    internal static byte[] PlainDocument(string text)
    {
        using var buffer = new MemoryStream();
        using (var package = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document, true))
        {
            var body = new W.Body();
            foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            {
                var run = new W.Run();
                var tabs = line.Split('\t');
                for (int i = 0; i < tabs.Length; i++)
                {
                    if (i > 0) run.Append(new W.TabChar());
                    run.Append(new W.Text(tabs[i]) { Space = SpaceProcessingModeValues.Preserve });
                }
                body.Append(new W.Paragraph(run));
            }
            body.Append(new W.SectionProperties(new W.PageSize { Width = 11906U, Height = 16838U },
                new W.PageMargin { Top = 1134, Bottom = 1134, Left = 1134U, Right = 1134U, Header = 567U, Footer = 567U }));
            var main = package.AddMainDocumentPart();
            main.Document = new W.Document(body);
            main.AddNewPart<StyleDefinitionsPart>().Styles = StandardParagraphStyles();
        }
        return buffer.ToArray();
    }

    private static W.Styles StandardParagraphStyles()
    {
        var normal = new W.Style(new W.StyleName { Val = "Normal" }, new W.PrimaryStyle())
            { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true };
        var styles = new W.Styles(normal);
        for (var level = 0; level <= 3; level++)
        {
            var title = level == 0;
            var properties = new W.StyleParagraphProperties(new W.KeepNext(), new W.KeepLines(),
                new W.SpacingBetweenLines { Before = "240", After = "120" });
            if (!title) properties.Append(new W.OutlineLevel { Val = level - 1 });
            var pointSize = level switch { 0 => "52", 1 => "40", 2 => "32", _ => "28" };
            styles.Append(new W.Style(new W.StyleName { Val = title ? "Title" : "Heading " + level },
                new W.BasedOn { Val = "Normal" }, new W.NextParagraphStyle { Val = "Normal" }, new W.PrimaryStyle(),
                properties, new W.StyleRunProperties(new W.Bold(), new W.FontSize { Val = pointSize }))
                { Type = W.StyleValues.Paragraph, StyleId = title ? "Title" : "Heading" + level });
        }
        return styles;
    }

    // All office conversion is an explicit, private headless operation; normal DOCX
    // open/edit/checkpoint never starts an office process or exposes a desktop window.
    internal static async Task<byte[]> ImportAsync(string path, string workspace, CancellationToken token)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (new FileInfo(path).Length > OfficeTextPackage.MaxBytes) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        if (ext == ".docx") return await File.ReadAllBytesAsync(path, token);
        if (ext == ".txt") return PlainDocument(RemoveTextPreamble(ReaderOfficeExport.ReadText(path)));
        if (ext is not (".doc" or ".odt" or ".rtf" or ".md" or ".markdown" or ".html" or ".htm"))
            throw new InvalidDataException("OfficeEditor.TextOnly");
        var directory = Directory.CreateDirectory(Path.Combine(workspace, "import-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string input;
            if (ext is ".md" or ".markdown" or ".html" or ".htm")
            {
                input = Path.Combine(directory, "input.html");
                var html = await Task.Run(() => ReaderMarkup.Prepare(path, token), token);
                await File.WriteAllTextAsync(input, html, new UTF8Encoding(false), token);
            }
            else
            {
                input = Path.Combine(directory, "input" + ext);
                await using var source = File.OpenRead(path);
                await using var copy = File.Open(input, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(copy, token);
            }
            var docx = await ConvertAsync(input, directory, "docx", token);
            return await File.ReadAllBytesAsync(docx, token);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task ExportAsync(byte[] bytes, string path, string workspace, OutputProvenance facts, CancellationToken token)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var format = extension switch { ".markdown" => "md", ".htm" => "html", _ => extension.TrimStart('.') };
        if (format is not ("docx" or "odt" or "pdf" or "md" or "html" or "rtf" or "txt"))
            throw new InvalidDataException("OfficeEditor.Unsupported");
        var staged = OfficeTextPackage.StagePath(path);
        var directory = Directory.CreateDirectory(Path.Combine(workspace, "export-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            if (format == "docx") await File.WriteAllBytesAsync(staged, bytes, token);
            else if (format == "txt") await File.WriteAllTextAsync(staged, TextProjection(bytes), new UTF8Encoding(false), token);
            else
            {
                var docx = Path.Combine(directory, "source.docx");
                await File.WriteAllBytesAsync(docx, bytes, token);
                if (format == "pdf")
                {
                    // The bundled Writer maps the OOXML subject into the PDF info
                    // dictionary. This also avoids needing a second metadata tool.
                    using var doc = WordprocessingDocument.Open(docx, true);
                    doc.PackageProperties.Subject = (doc.PackageProperties.Subject + "\nLOPATA.Provenance=" + facts.Json()).TrimStart();
                }
                var output = await ConvertAsync(docx, directory, format, token);
                File.Copy(output, staged);
            }
            await BookOutputMetadata.WriteAndVerifyAsync(staged, format, facts, token);
            token.ThrowIfCancellationRequested();
            OfficeTextPackage.Publish(staged, path);
        }
        finally { File.Delete(staged); Directory.Delete(directory, true); }
    }

    private static string TextProjection(byte[] bytes)
    {
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes, false), false);
        var body = package.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var result = new StringBuilder();
        // Use the document's main story; a table is rows with tab-separated cells.
        // Only visible run text is projected (never field instructions or deleted revisions).
        var paragraphs = body.Descendants<W.Paragraph>().Where(p => !p.Ancestors<W.Paragraph>().Any()).ToArray();
        for (int i = 0; i < paragraphs.Length; i++)
        {
            var paragraph = paragraphs[i];
            if (i > 0)
            {
                var previous = paragraphs[i - 1];
                var row = paragraph.Ancestors<W.TableRow>().FirstOrDefault();
                var nextCell = row != null && ReferenceEquals(row, previous.Ancestors<W.TableRow>().FirstOrDefault()) &&
                    !ReferenceEquals(paragraph.Ancestors<W.TableCell>().FirstOrDefault(), previous.Ancestors<W.TableCell>().FirstOrDefault());
                result.Append(nextCell ? "\t" : Environment.NewLine);
            }
            foreach (var run in paragraph.Descendants<W.Run>())
            {
                if (run.Ancestors<W.DeletedRun>().Any() || !ReferenceEquals(run.Ancestors<W.Paragraph>().FirstOrDefault(), paragraph)) continue;
                foreach (var item in run.ChildElements)
                    switch (item)
                    {
                        case W.Text t: result.Append(t.Text); break;
                        case W.TabChar: result.Append('\t'); break;
                        case W.Break or W.CarriageReturn: result.AppendLine(); break;
                        case W.NoBreakHyphen: result.Append('\u2011'); break;
                        case W.SoftHyphen: result.Append('\u00ad'); break;
                    }
            }
        }
        return result.ToString();
    }

    private static string RemoveTextPreamble(string text)
    {
        const string prefix = "[LOPATA.Provenance=";
        var first = text.IndexOf('\n');
        if (first < 0 || first > 48000 || !text.StartsWith(prefix, StringComparison.Ordinal)) return text;
        var line = text[..first].TrimEnd('\r');
        if (!line.EndsWith(']')) return text;
        try
        {
            using var json = JsonDocument.Parse(line[prefix.Length..^1]);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("Program", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != "LOPATA") return text;
        }
        catch (JsonException) { return text; }
        var start = first + 1;
        if (text.AsSpan(start).StartsWith("\r\n")) start += 2;
        else if (start < text.Length && text[start] == '\n') start++;
        return text[start..];
    }

    private static async Task<string> ConvertAsync(string input, string directory, string format, CancellationToken token)
    {
        var extension = Path.GetExtension(input).ToLowerInvariant();
        if (extension is ".odt" or ".docx")
        {
            using var archive = ZipFile.OpenRead(input);
            if (HasExternalOfficeContent(archive, extension == ".odt")) throw new InvalidDataException("OfficeEditor.ExternalContent");
        }
        using var audit = ComponentAudit.CaptureOperation();
        await BookConverterRuntime.EnsureLibreOfficeAsync(null, token);
        var executable = BookConverterRuntime.FindLibreOffice() ?? throw new IOException("OfficeEditor.EngineUnavailable");
        var profile = Path.Combine(directory, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "user"));
        const string policy = """
            <oor:items xmlns:oor="http://openoffice.org/2001/registry">
              <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
                <prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
                <prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="BlockUntrustedRefererLinks" oor:op="fuse"><value>true</value></prop>
              </item>
              <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>0</value></prop></item>
            </oor:items>
            """;
        await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"), policy, token);
        var filter = format switch {
            "docx" => "docx:Office Open XML Text", "odt" => "odt:writer8", "pdf" => "pdf:writer_pdf_Export",
            "rtf" => "rtf:Rich Text Format", "md" => "md:Markdown", "html" => "html:HTML (StarWriter):EmbedImages",
            _ => throw new InvalidDataException("OfficeEditor.Unsupported") };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        await ReaderProcess.RunAsync(executable, ["-env:UserInstallation=" + new Uri(profile).AbsoluteUri, "--headless",
            "--nologo", "--nodefault", "--norestore", "--nofirststartwizard", "--convert-to", filter, "--outdir", directory, input], limit.Token);
        var result = Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + "." + format);
        if (!File.Exists(result) || (new FileInfo(result).Length == 0 && format is not ("txt" or "md")))
            throw new InvalidDataException("OfficeEditor.Failed");
        return result;
    }

    private static bool HasExternalOfficeContent(ZipArchive zip, bool odf)
    {
        const string xlink = "http://www.w3.org/1999/xlink";
        var entries = zip.Entries.Where(e => odf ? e.FullName is "content.xml" or "styles.xml" :
            e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase));
        foreach (var item in entries)
        {
            using var input = item.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = OfficeTextPackage.MaxBytes });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "Relationship" && string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase) &&
                    reader.GetAttribute("Type")?.EndsWith("/hyperlink", StringComparison.Ordinal) != true) return true;
                if (odf && reader.LocalName != "a" && reader.GetAttribute("href", xlink) is { Length: > 0 } href &&
                    !href.StartsWith('#') && !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                    (Uri.TryCreate(href, UriKind.Absolute, out _) || href.Replace('\\', '/').Split('/').Contains(".."))) return true;
                if (odf && reader.LocalName is "dde-source" or "section-source") return true;
            }
        }
        return false;
    }
}
