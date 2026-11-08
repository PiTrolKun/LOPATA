using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using AIHub.Services.Books;

namespace AIHub.Services.OfficeReader;

internal static class ReaderPreparation
{
    internal static string CacheRoot => Path.Combine(AppDataPaths.BaseDirectory, "OfficeReader", "Sessions");
    internal static readonly string[] OfficeExtensions = [OfficeEditor.OfficePackage.Extension, ".docx", ".doc", ".odt", ".rtf", ".xlsx", ".xlsm", ".xls", ".ods", ".pptx", ".ppt", ".odp", ".csv", ".txt", ".html", ".htm", ".md", ".markdown"];
    internal static readonly string[] BookExtensions = [".epub", ".fb2", ".mobi", ".azw", ".azw3", ".kepub"];
    internal static readonly string[] SupportedExtensions = OfficeExtensions.Concat(BookExtensions)
        .Concat(ReaderImages.Extensions).Concat([".pdf", ".djvu", ".djv", ".cbz", ".cbr", ".zip"]).ToArray();

    internal static async Task<ReaderDocument> OpenAsync(string path, IProgress<string>? progress, CancellationToken token,
        string? displayPath = null, bool useCache = true)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var elapsed = Stopwatch.StartNew();
        var cacheHit = false;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Reader.Missing");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension)) throw new InvalidDataException("Reader.Unsupported");
        await ComponentLicenseGate.EnsureAsync(["nuget.PdfPig", "nuget.SharpCompress", "nuget.SkiaSharp", "nuget.SkiaSharp.NativeAssets.Win32"], token);
        var workspace = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        IReaderPages? pages = null;
        try
        {
            progress?.Report("Reader.Loading");
            var source = Path.Combine(workspace, "source" + extension);
            await using (var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = File.Create(source)) await input.CopyToAsync(output, token);
            if (extension == OfficeEditor.OfficePackage.Extension)
            {
                source = await Task.Run(() => OfficeEditor.OfficePackage.Extract(source, Path.Combine(workspace, "native")), token);
                extension = Path.GetExtension(source).ToLowerInvariant();
            }
            if (extension == ".zip")
            { source = await Task.Run(() => UnpackBook(source, workspace), token); extension = Path.GetExtension(source).ToLowerInvariant(); }
            if (extension is ".cbz" or ".cbr")
                pages = await Task.Run(() => ReaderImages.Comic(source, workspace, token), token);
            else if (ReaderImages.Extensions.Contains(extension))
                pages = await Task.Run(() => new ReaderImages([source]), token);
            string? pdf = null;
            if (pages is null)
            {
                pdf = source;
                var cacheKey = useCache ? await ReaderPreviewCache.KeyAsync(source, extension, token) : null;
                var cachedPdf = Path.Combine(workspace, "preview.pdf");
                cacheHit = await ReaderPreviewCache.RestoreAsync(cacheKey, cachedPdf, token);
                if (cacheHit) pdf = cachedPdf;
                else if (extension is ".djvu" or ".djv")
                {
                    progress?.Report("Reader.PreparingBook");
                    var decoder = await ReaderDjvuRuntime.EnsureAsync(token);
                    pdf = Path.Combine(workspace, "preview.pdf");
                    await ReaderProcess.RunAsync(decoder, ["-format=pdf", source, pdf], token);
                }
                else if (BookExtensions.Contains(extension))
                {
                    progress?.Report("Reader.PreparingBook");
                    await BookConverterRuntime.EnsureCalibreAsync(null, token);
                    pdf = Path.Combine(workspace, "preview.pdf");
                    await ReaderProcess.RunAsync(BookConverterRuntime.FindCalibre()!, [source, pdf,
                        "--paper-size", "a4", "--pdf-add-toc", "--pdf-page-numbers"], token);
                }
                else if (extension != ".pdf")
                {
                    progress?.Report("Reader.PreparingOffice");
                    pdf = await ReaderOfficeExport.ExportAsync(path, source, workspace, extension, token);
                }
                token.ThrowIfCancellationRequested();
                if (!cacheHit) await ReaderPreviewCache.StoreAsync(cacheKey, pdf, token);
                OwnedProcessRegistry.Log("reader_prepared", "Office.Reader", detail: extension + "; cache=" + cacheHit + "; ms=" + elapsed.ElapsedMilliseconds);
                pages = await ReaderPdfPages.OpenAsync(pdf, workspace, token);
            }
            var ready = await Task.Run(() => new ReaderDocument(displayPath ?? path, workspace, pages, pdf), token);
            token.ThrowIfCancellationRequested();
            OwnedProcessRegistry.Log("reader_opened", "Office.Reader", detail: extension + "; cache=" + cacheHit + "; ms=" + elapsed.ElapsedMilliseconds);
            return ready;
        }
        catch
        {
            if (pages is not null) await pages.DisposeAsync();
            DeleteWorkspace(workspace); throw;
        }
    }

    private static string UnpackBook(string source, string workspace)
    {
        using var zip = ZipFile.OpenRead(source);
        var books = zip.Entries.Where(e => e.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (books.Length != 1 || books[0].Length > 128 * 1024 * 1024) throw new InvalidDataException("Reader.Fb2ZipRequired");
        var target = Path.Combine(workspace, "source.fb2");
        using var input = books[0].Open(); using var output = File.Create(target);
        var buffer = new byte[65536]; int count; long total = 0;
        while ((count = input.Read(buffer)) > 0)
        { total += count; if (total > 128 * 1024 * 1024) throw new InvalidDataException("Reader.ArchiveLimit"); output.Write(buffer, 0, count); }
        return target;
    }

    internal static void DeleteWorkspace(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
