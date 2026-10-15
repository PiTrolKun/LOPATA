using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace AIHub.Services;

public sealed record RagBookSource(string Id, string Name, string Sha256, string Snapshot, string[] Pages, string[] Warnings);
public sealed record RagBookPage(string Source, int Page, string Text, string[] Flags, string Method = "text");

/// <summary>Exact extracted text, never model paraphrases. Page snapshots survive application restarts.</summary>
public static class RagBookSourcePreparation
{
    private static bool Reusable(string path)
    {
        if (!File.Exists(path) || !File.Exists(path + ".sha256")) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != JsonSerializer.Deserialize<string>(File.ReadAllText(path + ".sha256"))) return false;
            var page = JsonSerializer.Deserialize<RagBookPage>(File.ReadAllText(path));
            return page is { Flags: not null } && !page.Flags.Any(flag => flag.StartsWith("ocr_component_", StringComparison.Ordinal)
                || flag.StartsWith("ocr_language_", StringComparison.Ordinal) || flag.StartsWith("ocr_failed", StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }
    public static IReadOnlyList<RagBookSource> Prepare(RagBookJob job, string work, IProgress<RagBookProgress> progress, CancellationToken token)
        => PrepareAsync(job, work, progress, token).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<RagBookSource>> PrepareAsync(RagBookJob job, string work, IProgress<RagBookProgress> progress, CancellationToken token)
    {
        var result = new List<RagBookSource>();
        var load = new RagBookLoadController(Math.Clamp(job.CpuTargetPercent, 10, 90), progress);
        for (var index = 0; index < job.Books.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = job.Books[index];
            progress.Report(new("Extract", index, job.Books.Length, Path.GetFileName(path)));
            // Copy then hash the managed snapshot; a caller editing the original cannot invalidate later offsets.
            var temporary = Path.Combine(work, "source-" + index + ".copy");
            using (var source = File.OpenRead(path))
            using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024]; int read;
                while ((read = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); target.Write(buffer, 0, read); }
                target.Flush(true);
            }
            string hash;
            using (var input = File.OpenRead(temporary)) hash = Convert.ToHexStringLower(SHA256.HashData(input));
            var root = Path.Combine(job.Directory, "sources", hash);
            Directory.CreateDirectory(root);
            var snapshot = Path.Combine(root, "original" + Path.GetExtension(path).ToLowerInvariant());
            if (!File.Exists(snapshot)) File.Move(temporary, snapshot); else File.Delete(temporary);
            using (var input = File.OpenRead(snapshot))
                if (Convert.ToHexStringLower(SHA256.HashData(input)) != hash) throw new InvalidDataException("Source snapshot checksum mismatch.");
            var metadata = Path.Combine(root, "source.json");
            if (File.Exists(metadata))
            {
                RagBookSource? saved = null;
                try { saved = JsonSerializer.Deserialize<RagBookSource>(File.ReadAllText(metadata)); } catch (JsonException) { }
                if (saved is { Warnings.Length: 0, Pages: not null } && saved.Sha256 == hash && saved.Pages.All(Reusable)) { result.Add(saved); continue; }
            }
            var pages = new System.Collections.Concurrent.ConcurrentDictionary<int, string>(); var warnings = new List<string>();
            var completed = 0;
            async Task Add(int number, string text, int total, CancellationToken pageToken)
            {
                pageToken.ThrowIfCancellationRequested();
                var page = Path.Combine(root, $"page-{number:D6}.json");
                if (Reusable(page))
                {
                    var saved = JsonSerializer.Deserialize<RagBookPage>(File.ReadAllText(page));
                    if (saved is not null && saved.Source == hash && saved.Page == number)
                    { pages[number] = page; progress.Report(new("Extract", Interlocked.Increment(ref completed), total, Path.GetFileName(path))); return; }
                }
                using var lease = await load.EnterAsync(pageToken);
                var flags = Array.Empty<string>(); var method = "text";
                if (Path.GetExtension(snapshot) == ".pdf" && (text.Count(char.IsLetter) < 5 || text.Contains('\ufffd')))
                {
                    RagBookPreparation.AtomicJson(Path.Combine(root, $"raw-page-{number:D6}.json"), new { Text = text });
                    progress.Report(new("Ocr", Volatile.Read(ref completed), total, Path.GetFileName(path)));
                    try { (text, flags) = await RagBookOcr.ReadAsync(snapshot, number, root, pageToken); }
                    catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not (IOException or UnauthorizedAccessException))
                    { flags = ["ocr_failed:" + error.GetType().Name]; }
                    method = "tesseract";
                }
                // No silent normalization: offsets refer to the original extracted page, including line boundaries.
                RagBookPreparation.AtomicJson(page, new RagBookPage(hash, number, text,
                    string.IsNullOrWhiteSpace(text) && flags.Length == 0 ? ["empty_content"] : flags, method));
                using (var stored = File.OpenRead(page)) RagBookPreparation.AtomicJson(page + ".sha256", Convert.ToHexStringLower(SHA256.HashData(stored)));
                pages[number] = page; progress.Report(new("Extract", Interlocked.Increment(ref completed), total, Path.GetFileName(path)));
            }
            try
            {
                switch (Path.GetExtension(snapshot))
                {
                    case ".txt":
                        await Add(1, File.ReadAllText(snapshot, new UTF8Encoding(false, true)), 1, token); break;
                    case ".html": case ".htm":
                        var html = new HtmlParser().ParseDocument(File.ReadAllText(snapshot, new UTF8Encoding(false, true)));
                        foreach (var node in html.QuerySelectorAll("script,style,nav,header,footer,form")) node.Remove();
                        foreach (var node in html.QuerySelectorAll("p,div,h1,h2,h3,h4,li,br,pre,tr")) node.Prepend(html.CreateTextNode("\n"));
                        await Add(1, html.Body?.TextContent ?? html.DocumentElement.TextContent, 1, token); break;
                    case ".pdf":
                        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(snapshot))
                        {
                            // The iterator is read serially by Parallel.ForEachAsync. PdfPig is never shared by page workers.
                            IEnumerable<(int Number, string Text)> Extract()
                            {
                                foreach (var page in pdf.GetPages())
                                {
                                    token.ThrowIfCancellationRequested();
                                    yield return (page.Number, UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
                                }
                            }
                            await Parallel.ForEachAsync(Extract(), new ParallelOptions { MaxDegreeOfParallelism = load.Maximum, CancellationToken = token },
                                async (page, pageToken) => await Add(page.Number, page.Text, pdf.NumberOfPages, pageToken));
                        }
                        break;
                    default: warnings.Add("unsupported_format"); break;
                }
            }
            catch (Exception error) when (error is not (OperationCanceledException or IOException or UnauthorizedAccessException))
            { warnings.Add("source_extraction_failed:" + error.GetType().Name); }
            var entry = new RagBookSource(hash, Path.GetFileName(path), hash, snapshot, pages.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), warnings.ToArray());
            RagBookPreparation.AtomicJson(metadata, entry); result.Add(entry);
        }
        return result;
    }
}
