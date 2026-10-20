using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AIHub.Services.Books;

public static class BookConversionCapabilities
{
    private static readonly string[] Graphics = ["png", "jpg", "jpeg", "tif", "tiff", "webp", "bmp", "gif", "cbz", "cbr", "pdf"];
    public static string[] GetOutputFormats(IEnumerable<string> sources, bool smart = false)
    {
        var extensions = sources.Select(s => Path.GetExtension(s).TrimStart('.').ToLowerInvariant()).ToArray();
        return BookConversionFormats.OutputExtensions.Where(f => (f != "cbz" || extensions.Length > 0 && extensions.All(Graphics.Contains)) && (smart || !(f is "txt" or "md") || extensions.Length == 0 || !extensions.All(e => e != "pdf" && Graphics.Contains(e)))).ToArray();
    }
    public static string? GetUnavailableReason(string source, string outputFormat)
    {
        var ext = Path.GetExtension(source).TrimStart('.').ToLowerInvariant(); var format = BookConversionFormats.Normalize(outputFormat);
        if (!BookConversionFormats.InputExtensions.Contains(ext)) return "BookConverter.Error.InputFormat";
        if (!GetOutputFormats([source]).Contains(format)) return "BookConverter.Error.UnavailableOutput";
        if ((ext == "doc" || format == "odt") && BookConverterRuntime.FindLibreOffice() is null) return "BookConverter.Error.OfficeSourceRequired";
        var inputNeedsCalibre = !BookConversionFormats.NativeInputs.Contains(ext) && ext != "doc" && !(ext == "odt" && BookConverterRuntime.FindLibreOffice() is not null);
        if ((inputNeedsCalibre || BookConversionFormats.CalibreOutputs.Contains(format) && format != "odt") && BookConverterRuntime.FindCalibre() is null) return "BookConverter.Error.CalibreRequired";
        return null;
    }
}

/// <summary>All writers operate on a private staging copy. Only a validated result is published, without overwrite.</summary>
public sealed class BookConversionService
{
    private readonly Func<BookReviewSession, CancellationToken, Task<BookReviewDecision>>? _review;
    public BookConversionService(Func<BookReviewSession, CancellationToken, Task<BookReviewDecision>>? review = null) => _review = review;
    private readonly Smart.IBookSmartProcessor? _smart;
    private readonly Func<string, string, string, string, CancellationToken, string>? _publish;
    internal BookConversionService(Func<BookReviewSession, CancellationToken, Task<BookReviewDecision>>? review,
        Smart.IBookSmartProcessor smart, Func<string, string, string, string, CancellationToken, string>? publish = null)
    { _review = review; _smart = smart; _publish = publish; }
    public async Task<BookConversionResult> ConvertAsync(BookConversionRequest request, IProgress<BookConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outputs = new List<string>(); var skipped = new List<string>(); var failures = new List<BookConversionFailure>(); var warnings = new List<string>();
        var sources = request.Sources.Select(Path.GetFullPath).ToArray(); var format = BookConversionFormats.Normalize(request.OutputFormat);
        Validate(request, sources, format);
        if (request.Smart is not null && _smart is null) throw new InvalidOperationException("BookConverter.Smart.NotPrepared");
        var total = request.Merge ? 1 : sources.Length; var work = Path.Combine(Path.GetTempPath(), "LOPATA-Books", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var groups = request.Merge ? new[] { sources } : sources.Select(s => new[] { s }).ToArray();
            for (var index = 0; index < groups.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var group = groups[index]; var source = group[0];
                progress?.Report(new("reading", index, total, source, Message: request.Merge ? "BookConverter.Progress.Merge" : "BookConverter.Progress.Reading"));
                try
                {
                    var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in group) { cancellationToken.ThrowIfCancellationRequested(); using var stream = File.OpenRead(item); hashes[item] = await SHA256.HashDataAsync(stream, cancellationToken); }
                    var stageDirectory = Path.Combine(work, index.ToString()); Directory.CreateDirectory(stageDirectory); var staged = Path.Combine(stageDirectory, "result." + format);
                    var books = new List<BookContent>();
                    var marks = new List<BookReviewMark>(); var usedModels = new List<OutputModelUse>();
                    var sourceHistory = group.SelectMany(BookConversionHistory.Read).Distinct().ToArray();
                    var facts = new OutputProvenance { Scenario = "books/" + (request.Merge ? "merge" : "convert") + "/" + format, CreatedUtc = DateTimeOffset.UtcNow, ModelHistory = "not-applicable" };
                    var ext = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
                    // The same-format path retains original package-specific metadata, resources, notes and layout.
                    var directCopy = request.Smart is null && !request.Merge && request.Options == new BookConversionOptions() && ext == format && string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Author) && string.IsNullOrWhiteSpace(request.Language) && string.IsNullOrWhiteSpace(request.CoverPath) && format is "epub" or "fb2" or "docx" or "cbz";
                    if (directCopy) File.Copy(source, staged);
                    else
                    {
                        foreach (var item in group)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var content = await ReadAsync(item, request.Options, stageDirectory, format == "cbz" || request.Smart is not null, cancellationToken);
                            if (request.Smart is not null)
                            {
                                var processed = await _smart!.ProcessAsync(content, item, cancellationToken);
                                content = processed.Content; marks.AddRange(processed.Marks); usedModels.AddRange(processed.Models);
                            }
                            books.Add(content);
                        }
                        if (request.Smart is not null) facts = facts with { Scenario = "books/smart/" + (request.Merge ? "merge/" : "convert/") + format,
                            ModelHistory = usedModels.Count > 0 ? "recorded" : "not-applicable", Models = usedModels.Distinct().ToArray() };
                        var book = request.Merge ? BookMarkup.Merge(books, request.Title ?? "Сборник") : books[0];
                        book.SourceHistory = sourceHistory;
                        if (format is "txt" or "md" && book.PlainText.Trim().Length == 0) throw new InvalidDataException("BookConverter.Error.NoTextLayer");
                        if (!string.IsNullOrWhiteSpace(request.Title)) book.Title = request.Title.Trim();
                        if (!string.IsNullOrWhiteSpace(request.Author)) book.Author = request.Author.Trim();
                        if (!string.IsNullOrWhiteSpace(request.Language)) book.Language = request.Language.Trim();
                        if (book.Title.Length == 0) book.Title = Path.GetFileNameWithoutExtension(source);
                        if (!string.IsNullOrWhiteSpace(request.CoverPath))
                        {
                            var coverKey = "custom-cover" + Path.GetExtension(request.CoverPath).ToLowerInvariant(); book.Assets[coverKey] = await File.ReadAllBytesAsync(request.CoverPath, cancellationToken); book.Cover = coverKey;
                            if (format is "docx" or "pdf") book.Parts.Insert(0, new BookPart("Обложка", "<p><img src=\"" + coverKey + "\"/></p>"));
                        }
                        progress?.Report(new("converting", index, total, source, Message: "BookConverter.Progress.Converting"));
                        if (BookConversionFormats.NativeOutputs.Contains(format))
                            await Task.Run(() => BookConversionWriter.Write(book, staged, format, request.Options, cancellationToken, facts), cancellationToken);
                        else
                        {
                            var intermediateFormat = format == "odt" ? "docx" : "epub";
                            var intermediate = Path.Combine(stageDirectory, "intermediate." + intermediateFormat); BookConversionWriter.Write(book, intermediate, intermediateFormat, request.Options, cancellationToken);
                            if (format == "odt") await LibreOfficeAsync(intermediate, staged, cancellationToken);
                            else await CalibreAsync(intermediate, staged, book, request.Options, cancellationToken);
                        }
                        warnings.AddRange(book.Warnings);
                    }
                    progress?.Report(new("metadata", index, total, source, Message: "BookConverter.Progress.Metadata"));
                    BookConversionHistory.Write(staged, format, sourceHistory);
                    await BookOutputMetadata.WriteAndVerifyAsync(staged, format, facts, cancellationToken);
                    ValidateContainer(staged, format);
                    if (_review is not null)
                    {
                        progress?.Report(new("reviewing", index, total, source, Message: "BookConverter.Review.Progress"));
                        var readOptions = request.Options with { TextEncoding = "utf-8" };
                        var reviewWork = Path.Combine(stageDirectory, "review"); Directory.CreateDirectory(reviewWork);
                        async Task<BookReviewDocument> ReadReviewAsync(string target, Guid? origin, CancellationToken ct)
                        {
                            var actual = await ReadAsync(target, readOptions, reviewWork, format == "cbz", ct);
                            actual.SourceHistory = sourceHistory;
                            return BookReviewDocument.FromContent(actual, origin);
                        }
                        var reviewDocument = await ReadReviewAsync(staged, null, cancellationToken);
                        byte[] reviewedHash;
                        using (var stream = File.OpenRead(staged)) reviewedHash = await SHA256.HashDataAsync(stream, cancellationToken);
                        var session = new BookReviewSession(staged, format, Path.GetFileName(source), reviewDocument, async (snapshot, ct) =>
                        {
                            var candidate = Path.Combine(stageDirectory, "review-" + Guid.NewGuid().ToString("N") + "." + format);
                            try
                            {
                                var edited = PrepareReviewedContent(snapshot, format);
                                await ExportReviewedAsync(edited, candidate, format, request.Options, facts, ct);
                                BookConversionHistory.Write(candidate, format, sourceHistory);
                                await BookOutputMetadata.WriteAndVerifyAsync(candidate, format, facts, ct);
                                ValidateContainer(candidate, format);
                                var actualDocument = await ReadReviewAsync(candidate, snapshot.Origin, ct);
                                byte[] candidateHash; using (var stream = File.OpenRead(candidate)) candidateHash = await SHA256.HashDataAsync(stream, ct);
                                ct.ThrowIfCancellationRequested();
                                File.Move(candidate, staged, true); // Same private directory/volume; replace only after export, validation and independent read.
                                reviewedHash = candidateHash;
                                warnings.AddRange(edited.Warnings);
                                return actualDocument;
                            }
                            finally { if (File.Exists(candidate)) File.Delete(candidate); }
                        });
                        session.Evidence = new(group, Smart.BookSmartReviewBinding.Bind(reviewDocument, marks));
                        var decision = await _review(session, cancellationToken);
                        if (decision == BookReviewDecision.Cancel) throw new OperationCanceledException(cancellationToken);
                        if (decision == BookReviewDecision.Skip)
                        {
                            skipped.Add(source); progress?.Report(new("skipped", index + 1, total, source, Message: "BookConverter.Review.Skipped")); continue;
                        }
                        if (decision != BookReviewDecision.Save || !session.CanSave) throw new InvalidOperationException("BookConverter.Review.Unrefreshed");
                        using var reviewedStream = File.OpenRead(staged);
                        var currentHash = await SHA256.HashDataAsync(reviewedStream, cancellationToken);
                        if (!currentHash.SequenceEqual(reviewedHash)) throw new InvalidDataException("BookConverter.Review.StageChanged");
                    }
                    // Check every source again, including cancellation/failure paths through read-only access.
                    foreach (var item in group)
                    {
                        using var input = File.OpenRead(item); var actual = await SHA256.HashDataAsync(input, cancellationToken);
                        if (!actual.SequenceEqual(hashes[item])) throw new IOException("BookConverter.Error.SourceChanged");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(request.OutputDirectory); Directory.CreateDirectory(directory);
                    var baseName = request.Merge ? SafeName(request.Title ?? "Сборник") : Path.GetFileNameWithoutExtension(source);
                    var published = (_publish ?? Publish)(staged, directory, baseName, format, cancellationToken); outputs.Add(published);
                    progress?.Report(new("completed", index + 1, total, source, published, "BookConverter.Progress.Completed"));
                }
                catch (OperationCanceledException) { throw; }
                catch (AIHub.Models.BackgroundOperationWaitingException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    failures.Add(new(source, ex.Message)); progress?.Report(new("failed", index + 1, total, source, Message: ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { return new() { Outputs = outputs.ToArray(), Skipped = skipped.ToArray(), Failures = failures.ToArray(), Warnings = warnings.Distinct().ToArray(), Cancelled = true }; }
        finally
        {
            // Only delete the freshly allocated private working directory, never a computed user path.
            if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new() { Outputs = outputs.ToArray(), Skipped = skipped.ToArray(), Failures = failures.ToArray(), Warnings = warnings.Distinct().ToArray() };
    }
    private static BookContent PrepareReviewedContent(BookReviewDocument document, string format)
    {
        var content = document.ToContent();
        // The model is read from the target. Do not add another generated heading on each edit/preview cycle.
        if (format == "fb2") content.UseExistingPartTitles = true;
        if (format == "md" && content.Parts.Count > 0)
        {
            var first = content.Parts[0]; var dom = BookMarkup.Parse(first.Html);
            if (dom.Body?.FirstElementChild is { LocalName: "h1" } heading)
            {
                content.Title = heading.TextContent; heading.Remove();
                content.Parts[0] = first with { Html = string.Join("", dom.Head!.QuerySelectorAll("style").Select(e => e.OuterHtml)) + dom.Body.InnerHtml };
            }
        }
        return content;
    }
    private static async Task ExportReviewedAsync(BookContent book, string target, string format, BookConversionOptions options, OutputProvenance facts, CancellationToken ct)
    {
        if (BookConversionFormats.NativeOutputs.Contains(format)) await Task.Run(() => BookConversionWriter.Write(book, target, format, options, ct, facts), ct);
        else
        {
            var intermediateFormat = format == "odt" ? "docx" : "epub";
            var intermediate = Path.Combine(Path.GetDirectoryName(target)!, "review-intermediate-" + Guid.NewGuid().ToString("N") + "." + intermediateFormat);
            try
            {
                BookConversionWriter.Write(book, intermediate, intermediateFormat, options, ct);
                if (format == "odt") await LibreOfficeAsync(intermediate, target, ct);
                else await CalibreAsync(intermediate, target, book, options, ct);
            }
            finally { if (File.Exists(intermediate)) File.Delete(intermediate); }
        }
    }
    private static void Validate(BookConversionRequest request, string[] sources, string format)
    {
        if (sources.Length == 0) throw new ArgumentException("BookConverter.Error.NoSources");
        if (!BookConversionFormats.OutputExtensions.Contains(format)) throw new ArgumentException("BookConverter.Error.OutputFormat");
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length) throw new ArgumentException("BookConverter.Error.DuplicateSource");
        if (!BookConversionCapabilities.GetOutputFormats(sources, request.Smart is not null).Contains(format)) throw new ArgumentException("BookConverter.Error.UnavailablePair");
        if (request.Options.FontSize is < 6 or > 48 || request.Options.MarginMillimeters is < 0 or > 60 || request.Options.ImageQuality is < 10 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "BookConverter.Error.OptionRange");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); _ = Encoding.GetEncoding(request.Options.TextEncoding);
    }
    private static async Task<BookContent> ReadAsync(string source, BookConversionOptions options, string work, bool graphic, CancellationToken ct)
    {
        var ext = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        if (ext == "pdf") return await ReadPdfAsync(source, work, graphic, ct);
        if (BookConversionFormats.NativeInputs.Contains(ext)) return await Task.Run(() => BookConversionReader.Read(source, options, ct), ct);
        var reason = BookConversionCapabilities.GetUnavailableReason(source, "epub"); if (reason is not null) throw new NotSupportedException(reason);
        // LibreOffice-generated image identifiers are not reliably handled by calibre
        // (e.g. Bitmap_20_1). Prefer the prepared office reader for ODT, keep calibre fallback.
        if (ext == "doc" || ext == "odt" && BookConverterRuntime.FindLibreOffice() is not null)
        {
            var converted = Path.Combine(work, Guid.NewGuid() + ".docx"); await LibreOfficeAsync(source, converted, ct); return BookConversionReader.Read(converted, options, ct);
        }
        var epub = Path.Combine(work, Guid.NewGuid() + ".epub"); await CalibreAsync(source, epub, null, options, ct);
        var book = BookConversionReader.Read(epub, options, ct);
        if (ext == "pdf") book.Warnings.Add("PDF обработан без ИИ: сложные колонки, таблицы и порядок чтения могут потребовать умного режима. Скан без текстового слоя не распознаётся.");
        return book;
    }
    private static async Task<BookContent> ReadPdfAsync(string path, string work, bool graphic, CancellationToken ct)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var book = new BookContent { Title = pdf.Information.Title ?? Path.GetFileNameWithoutExtension(path), Author = pdf.Information.Author ?? "", Description = pdf.Information.Subject ?? "" };
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var words = page.GetWords().ToArray();
            if (graphic || words.Length == 0)
            {
                if (words.Length > 0) book.ExactPageWords[book.Parts.Count] = words.Select(word => new BookPdfWord(word.Text,
                    word.BoundingBox.Left / page.Width, 1 - word.BoundingBox.Top / page.Height,
                    word.BoundingBox.Right / page.Width, 1 - word.BoundingBox.Bottom / page.Height)).ToArray();
                var script = Path.Combine(AppContext.BaseDirectory, "Tools", "rag_pdf_render.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("BookConverter.Error.PdfRenderer");
                var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                var png = Path.Combine(work, "pdf-" + Guid.NewGuid().ToString("N") + ".png");
                await RunAsync(shell, ["-NoProfile", "-NonInteractive", "-File", script, "-InputPdf", path, "-Page", page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "-OutputPng", png], work, ct);
                var key = "page" + book.Assets.Count + ".png"; book.Assets[key] = await File.ReadAllBytesAsync(png, ct); book.Cover ??= key;
                book.Parts.Add(new BookPart("Страница " + page.Number, "<p><img src=\"" + key + "\"/></p>"));
                if (words.Length == 0 && !graphic) book.Warnings.Add("BookConverter.Warning.PdfScanned");
                continue;
            }
            var html = "<p>" + BookMarkup.Escape(string.Join(" ", words.Select(w => w.Text))) + "</p>";
            foreach (var image in page.GetImages())
            {
                if (!image.TryGetPng(out var bytes)) { book.Warnings.Add("BookConverter.Warning.PdfImage"); continue; }
                var key = "image" + book.Assets.Count + ".png"; book.Assets[key] = bytes; html += "<p><img src=\"" + key + "\"/></p>";
            }
            book.Parts.Add(new BookPart("Страница " + page.Number, html));
        }
        book.Warnings.Add("BookConverter.Warning.PdfReadingOrder");
        return book;
    }
    private static async Task CalibreAsync(string source, string target, BookContent? metadata, BookConversionOptions options, CancellationToken ct)
    {
        var exe = BookConverterRuntime.FindCalibre() ?? throw new FileNotFoundException("BookConverter.Error.CalibreRequired");
        await BookConverterRuntime.EnsureCalibreLicenseAsync(ct);
        var args = new List<string> { source, target };
        if (metadata is not null)
        {
            args.AddRange(["--title", metadata.Title, "--authors", metadata.Author, "--language", metadata.Language]);
        }
        args.AddRange(["--book-producer", "LOPATA " + new OutputProvenance().ProgramVersion + (metadata?.SourceHistory.Length > 0 ? "; LOPATA.SourceProvenance=" + System.Text.Json.JsonSerializer.Serialize(metadata.SourceHistory) : ""), "--disable-font-rescaling"]);
        await RunAsync(exe, args, Path.GetDirectoryName(target)!, ct);
        if (!File.Exists(target)) throw new IOException("BookConverter.Error.CalibreNoOutput");
    }
    private static async Task LibreOfficeAsync(string source, string target, CancellationToken ct)
    {
        var exe = BookConverterRuntime.FindLibreOffice() ?? throw new FileNotFoundException("BookConverter.Error.OfficeRequired");
        await BookConverterRuntime.EnsureLibreOfficeAsync(null, ct);
        var folder = Path.Combine(Path.GetDirectoryName(target)!, "lo-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        // Dedicated user profile prevents forwarding to or closing the user's running office session.
        var profile = new Uri(Path.Combine(folder, "profile")).AbsoluteUri;
        await RunAsync(exe, ["-env:UserInstallation=" + profile, "--headless", "--convert-to", Path.GetExtension(target).TrimStart('.'), "--outdir", folder, source], folder, ct);
        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + Path.GetExtension(target));
        if (!File.Exists(output)) throw new IOException("BookConverter.Error.OfficeNoOutput");
        File.Move(output, target, false);
    }
    private static async Task RunAsync(string exe, IEnumerable<string> args, string work, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["CALIBRE_CONFIG_DIRECTORY"] = Path.Combine(work, "calibre-config");
        info.Environment["CALIBRE_TEMP_DIR"] = work;
        using var process = OwnedProcessRegistry.Shared.Start(info, "Books.Conversion");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("Converter exit code " + process.ExitCode + ": " + (error.Length > 0 ? error : output)[..Math.Min(1600, (error.Length > 0 ? error : output).Length)]);
    }
    private static string Publish(string staged, string directory, string name, string format, CancellationToken ct)
    {
        // The final name is introduced atomically on the destination volume. A crash during copy
        // can only leave an explicitly unfinished .partial file, never a seemingly finished book.
        var pending = Path.Combine(directory, ".lopata-book-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var input = File.OpenRead(staged))
            {
                var buffer = new byte[128 * 1024]; int read;
                while ((read = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
            for (var i = 0; ; i++)
            {
                ct.ThrowIfCancellationRequested(); var target = Path.Combine(directory, name + (i == 0 ? "" : " (" + i + ")") + "." + format);
                try { File.Move(pending, target, false); return target; }
                catch (IOException) when (File.Exists(target)) { }
            }
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private static string SafeName(string name)
    {
        var result = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return result.Length == 0 ? "Сборник" : result;
    }
    private static void ValidateContainer(string path, string format)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("BookConverter.Error.EmptyOutput");
        if (format is "epub" or "docx" or "odt" or "cbz")
        {
            var zip = BookMarkup.ReadZip(path, CancellationToken.None);
            var required = format switch { "epub" => "META-INF/container.xml", "docx" => "word/document.xml", "odt" => "content.xml", _ => "ComicInfo.xml" };
            if (!zip.ContainsKey(required)) throw new InvalidDataException("Invalid output container: " + required);
        }
        if (format == "fb2") { using var stream = File.OpenRead(path); if (BookMarkup.Xml(stream).Root?.Name.LocalName != "FictionBook") throw new InvalidDataException("BookConverter.Error.InvalidFb2"); }
    }
}
