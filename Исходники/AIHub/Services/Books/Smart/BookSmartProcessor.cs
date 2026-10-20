using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace AIHub.Services.Books.Smart;

internal sealed record BookSmartBlock(int Id, string Text, string Kind = "paragraph", bool Questionable = false, string? Html = null, double[]? Bounds = null);
internal sealed record BookSmartAsset(string Key, byte[] Bytes, int BlockId);
internal sealed record BookSmartPage(BookSmartBlock[] Blocks, string[] Issues)
{ public BookSmartAsset[] Assets { get; init; } = []; }
internal sealed record BookSmartStructure(int Id, string Kind, bool Questionable = false);
internal sealed record BookSmartProcessed(BookContent Content, OutputModelUse[] Models, BookReviewMark[] Marks);
internal interface IBookSmartProcessor
{ Task<BookSmartProcessed> ProcessAsync(BookContent content, string source, CancellationToken token); }
internal interface IBookSmartModels : IDisposable
{
    OutputModelUse OcrIdentity { get; }
    OutputModelUse[] OcrDependencies => [];
    OutputModelUse VisionIdentity { get; }
    OutputModelUse StructureIdentity { get; }
    Task<BookSmartPage> RecognizeAsync(string image, CancellationToken token);
    Task<string[]> InspectAsync(string image, CancellationToken token);
    Task<BookSmartStructure[]> StructureAsync(BookSmartBlock[] blocks, CancellationToken token);
}

/// <summary>Model classification changes structure, never the author's text. One model stage at a time for the whole book.</summary>
internal sealed class BookSmartProcessor(IBookSmartModels models, string cacheRoot,
    IProgress<BookConversionProgress>? progress = null) : IBookSmartProcessor, IDisposable
{
    private sealed record ImageJob(int Part, IElement Image, string Path, string Digest, bool ImageOnly);
    private sealed record Concern(bool Questionable, string Reason);
    private sealed record VirtualBlock(BookSmartBlock Block, IElement Element, bool Whole);
    private sealed record CacheReceipt(string Schema, string Identity, string Hash, string Payload);
    private const string Schema = "book-smart/1";
    private const string LeafSelector = "p,h1,h2,h3,h4,h5,h6,pre,li,td,th,blockquote,div,section,article";

    public async Task<BookSmartProcessed> ProcessAsync(BookContent content, string source, CancellationToken token)
    {
        var used = new HashSet<OutputModelUse>(); var marks = new List<BookReviewMark>();
        var cache = Path.Combine(cacheRoot, Convert.ToHexString(await HashAsync(source, token))); Directory.CreateDirectory(cache);
        var documents = content.Parts.Select(part => BookMarkup.Parse(part.Html)).ToArray();
        var images = new List<ImageJob>(); var concerns = new Dictionary<IElement, Concern>();
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index]; var imageOnly = string.IsNullOrWhiteSpace(document.Body?.TextContent);
            foreach (var image in document.QuerySelectorAll("img[src],svg").Where(element => element.LocalName != "svg" || element.ParentElement?.NamespaceUri != "http://www.w3.org/2000/svg"))
            {
                token.ThrowIfCancellationRequested(); var key = image.GetAttribute("src") ?? "inline.svg";
                byte[] bytes;
                if (image.LocalName == "svg") bytes = Encoding.UTF8.GetBytes(BookConversionWriter.ToXml(image, "http://www.w3.org/2000/svg")?.ToString()
                    ?? throw new InvalidDataException("BookConverter.Error.InvalidImage"));
                else if (!content.Assets.TryGetValue(key, out bytes!)) throw new InvalidDataException("BookConverter.Error.MissingImage");
                var digest = Convert.ToHexString(SHA256.HashData(bytes));
                var normalized = await CachedAsync(cache, "image-normalization/1/" + digest,
                    () => BookSmartImage.NormalizeAsync(bytes, Path.GetExtension(key), token), token);
                var expected = Convert.ToHexString(SHA256.HashData(normalized));
                var path = Path.Combine(cache, digest + "-analysis.png");
                if (!File.Exists(path) || Convert.ToHexString(await HashAsync(path, token)) != expected) await AtomicBytesAsync(path, normalized, token);
                images.Add(new(index, image, path, digest, imageOnly));
            }
        }
        // All visual analysis precedes OCR: avoid unloading/reloading two large models for every page.
        var issuesByImage = new Dictionary<ImageJob, string[]>();
        for (var imageIndex = 0; imageIndex < images.Count; imageIndex++)
        {
            var image = images[imageIndex];
            token.ThrowIfCancellationRequested(); progress?.Report(new("vision", 0, 0, source, Message: "BookConverter.Smart.VisionProgress")
                { Section = image.Part + 1, SectionCount = documents.Length, Item = imageIndex + 1, ItemCount = images.Count });
            var issues = await CachedAsync(cache, Identity("vision", models.VisionIdentity, image.Digest), () => models.InspectAsync(image.Path, token), token);
            if (issues.Length > 50 || issues.Any(issue => issue is null || issue.Length > 2000)) throw new InvalidDataException("BookConverter.Smart.InvalidVision");
            issuesByImage[image] = issues; used.Add(models.VisionIdentity);
            if (!image.ImageOnly && issues.Length > 0) content.Warnings.Add("BookConverter.Smart.ImageQuestions");
        }
        var scans = images.Where(image => image.ImageOnly).ToArray();
        for (var imageIndex = 0; imageIndex < scans.Length; imageIndex++)
        {
            var image = scans[imageIndex];
            token.ThrowIfCancellationRequested(); progress?.Report(new("ocr", 0, 0, source, Message: "BookConverter.Smart.OcrProgress")
                { Section = image.Part + 1, SectionCount = documents.Length, Item = imageIndex + 1, ItemCount = scans.Length });
            var page = await CachedAsync(cache, Identity("ocr", models.OcrIdentity, image.Digest) + JsonSerializer.Serialize(models.OcrDependencies),
                () => models.RecognizeAsync(image.Path, token), token);
            page = BookSmartPdfText.Preserve(content, image.Part, page);
            if (page.Blocks.Length == 0) page = page with { Blocks = [new(0, "⟦…⟧", Questionable: true)], Issues = [.. page.Issues, "No readable text; original page retained."] };
            ValidatePage(page); content.Warnings.Add("BookConverter.Smart.OcrNotice"); used.Add(models.OcrIdentity); foreach (var dependency in models.OcrDependencies) used.Add(dependency);
            var document = documents[image.Part]; var region = document.CreateElement("div");
            foreach (var block in page.Blocks)
            {
                var container = document.CreateElement("div"); container.InnerHtml = Render(block);
                region.AppendChild(container);
                var allIssues = page.Issues.Concat(issuesByImage[image]).ToArray();
                concerns[container] = new(block.Questionable || issuesByImage[image].Length > 0, string.Join("; ", allIssues));
                foreach (var asset in page.Assets.Where(asset => asset.BlockId == block.Id))
                {
                    var key = "ocr-" + Convert.ToHexString(SHA256.HashData(asset.Bytes)) + Path.GetExtension(asset.Key).ToLowerInvariant();
                    content.Assets[key] = asset.Bytes;
                    var picture = document.CreateElement("img"); picture.SetAttribute("src", key); container.AppendChild(picture);
                }
            }
            // Keep the complete original scan. Insert block markup outside a paragraph to avoid invalid nested paragraphs.
            var anchor = image.Image.ParentElement?.LocalName == "p" && string.IsNullOrWhiteSpace(image.Image.ParentElement.TextContent)
                && image.Image.ParentElement.QuerySelectorAll("img").Length == 1 ? image.Image.ParentElement : image.Image;
            anchor.Parent!.InsertBefore(region, anchor);
            if (concerns.Values.Any(value => value.Questionable)) content.Warnings.Add("BookConverter.Smart.OcrQuestions");
        }
        for (var partIndex = 0; partIndex < documents.Length; partIndex++)
        {
            var document = documents[partIndex];
            var elements = document.QuerySelectorAll(LeafSelector).Where(element => !element.QuerySelectorAll(LeafSelector).Any())
                .Where(element => !string.IsNullOrWhiteSpace(element.TextContent)).ToArray();
            var virtuals = new List<VirtualBlock>();
            foreach (var element in elements)
            {
                var chunks = Split(element.TextContent).ToArray();
                foreach (var chunk in chunks) virtuals.Add(new(new(virtuals.Count, chunk, element.LocalName), element, chunks.Length == 1));
            }
            var batches = Batches(virtuals.Select(value => value.Block).ToArray()).ToArray();
            for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
            {
                var batch = batches[batchIndex];
                token.ThrowIfCancellationRequested(); progress?.Report(new("structure", 0, 0, source, Message: "BookConverter.Smart.StructureProgress")
                    { Section = partIndex + 1, SectionCount = documents.Length, Item = batchIndex + 1, ItemCount = batches.Length });
                var response = await CachedAsync(cache, Identity("structure", models.StructureIdentity, JsonSerializer.Serialize(batch)),
                    () => models.StructureAsync(batch, token), token);
                ValidateStructure(batch, response); used.Add(models.StructureIdentity);
                foreach (var decision in response)
                {
                    var item = virtuals[decision.Id]; var element = item.Element;
                    Concern? concern = null;
                    for (var ancestor = element; ancestor is not null; ancestor = ancestor.ParentElement)
                        if (concerns.TryGetValue(ancestor, out concern)) break;
                    if (item.Whole && element.LocalName == "p" && decision.Kind is "heading1" or "heading2" or "heading3")
                    {
                        var replacement = document.CreateElement("h" + decision.Kind[^1]);
                        foreach (var attribute in element.Attributes) replacement.SetAttribute(attribute.Name, attribute.Value);
                        replacement.InnerHtml = element.InnerHtml; element.Replace(replacement);
                    }
                    var questionable = decision.Questionable || concern?.Questionable == true;
                    marks.Add(new(item.Block.Text, questionable ? BookReviewMarkKind.Questionable : BookReviewMarkKind.AiProcessed,
                        source, concern?.Reason ?? "structure", partIndex + 1));
                }
            }
            content.Parts[partIndex] = content.Parts[partIndex] with { Html = Serialize(document) };
        }
        // Exact repeated passages cannot be distinguished from text alone. The target binder may safely re-anchor them;
        // until then leave them unresolved, rather than moving a red warning onto a different occurrence.
        var repeated = marks.GroupBy(mark => Normalize(mark.Text)).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return new(content, used.ToArray(), marks.Select(mark => repeated.Contains(Normalize(mark.Text)) ? mark with { Occurrence = int.MaxValue } : mark).ToArray());
    }

    private static string Serialize(IHtmlDocument document) => string.Join("", document.Head?.QuerySelectorAll("style").Select(style => style.OuterHtml) ?? []) + (document.Body?.InnerHtml ?? "");
    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Identity(string stage, OutputModelUse model, string input) => Schema + "/" + stage + "/" + JsonSerializer.Serialize(model) + "/" + input;
    private static IEnumerable<string> Split(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var count = Math.Min(8000, text.Length - start);
            if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1])) count--;
            yield return text.Substring(start, count); start += count;
        }
    }
    internal static IEnumerable<BookSmartBlock[]> Batches(BookSmartBlock[] blocks)
    {
        var batch = new List<BookSmartBlock>(); var size = 0;
        foreach (var block in blocks)
        {
            if (batch.Count >= 32 || batch.Count > 0 && size + block.Text.Length > 10000) { yield return batch.ToArray(); batch.Clear(); size = 0; }
            batch.Add(block); size += block.Text.Length;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
    internal static void ValidateStructure(BookSmartBlock[] input, BookSmartStructure[] output)
    {
        var ids = input.Select(block => block.Id).ToHashSet();
        if (output.Length != ids.Count || output.Select(block => block.Id).Distinct().Count() != ids.Count
            || output.Any(block => !ids.Contains(block.Id) || block.Kind is not ("paragraph" or "heading1" or "heading2" or "heading3" or "footnote" or "table" or "keep")))
            throw new InvalidDataException("BookConverter.Smart.InvalidStructure");
    }
    internal static void ValidatePage(BookSmartPage page)
    {
        if (page.Blocks.Length == 0 || page.Blocks.Length > 5000 || page.Blocks.Any(block => string.IsNullOrWhiteSpace(block.Text) && block.Kind is not ("image" or "chart"))
            || page.Blocks.Sum(block => (long)block.Text.Length) > 2_000_000 || page.Blocks.Select(block => block.Id).Distinct().Count() != page.Blocks.Length
            || page.Issues.Length > 500 || page.Issues.Any(issue => issue is null || issue.Length > 10000)
            || page.Assets.Length > 5000 || page.Assets.Sum(asset => (long)asset.Bytes.Length) > 128L * 1024 * 1024
            || page.Assets.Any(asset => asset.Bytes.Length == 0 || !page.Blocks.Any(block => block.Id == asset.BlockId)
                || Path.GetExtension(asset.Key).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp")))
            throw new InvalidDataException("BookConverter.Smart.InvalidOcr");
    }
    private static string Render(BookSmartBlock block)
    {
        if (block.Kind is "image" or "chart" && string.IsNullOrWhiteSpace(block.Text)) return "";
        if (block.Html is not null)
        {
            var document = BookMarkup.Parse(block.Html); BookMarkup.Sanitize(document);
            foreach (var unsafeElement in document.QuerySelectorAll("img,svg,style,link,meta,audio,video,source,math")) unsafeElement.Remove();
            foreach (var element in document.All)
                foreach (var attribute in element.Attributes.ToArray())
                    if (!(attribute.Name is "colspan" or "rowspan" && int.TryParse(attribute.Value, out var span) && span is > 0 and < 100)) element.RemoveAttribute(attribute.Name);
            if (Normalize(document.Body?.TextContent ?? "") == Normalize(block.Text)) return document.Body!.InnerHtml;
        }
        var tag = block.Kind is "table" or "formula" ? "pre" : block.Kind == "heading" ? "h2" : "p";
        return "<" + tag + ">" + BookMarkup.Escape(block.Text) + "</" + tag + ">";
    }
    internal static async Task<T> CachedAsync<T>(string root, string identity, Func<Task<T>> create, CancellationToken token)
    {
        var path = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Schema + identity))) + ".json");
        if (File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<CacheReceipt>(await File.ReadAllBytesAsync(path, token));
            if (saved is null || saved.Schema != Schema || saved.Identity != identity || saved.Hash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saved.Payload))))
                throw new InvalidDataException("BookConverter.Smart.InvalidWork");
            return JsonSerializer.Deserialize<T>(saved.Payload) ?? throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        }
        var value = await create(); token.ThrowIfCancellationRequested(); var payload = JsonSerializer.Serialize(value);
        await AtomicBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(new CacheReceipt(Schema, identity, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), payload)), token);
        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Stage = "book-smart", Receipt = Path.GetFileName(path) });
        return value;
    }
    private static async Task AtomicBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await stream.WriteAsync(bytes, token); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(pending, path, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private static async Task<byte[]> HashAsync(string path, CancellationToken token)
    { using var file = File.OpenRead(path); return await SHA256.HashDataAsync(file, token); }
    public void Dispose() => models.Dispose();
}
