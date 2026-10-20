using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AngleSharp.Dom;
using Markdig;

namespace AIHub.Services.Books.Smart;

internal static class BookSmartOcrWorker
{
    internal static async Task<BookSmartPage> RecognizeAsync(BookSmartSelection selection, string root,
        Uri endpoint, string image, CancellationToken token)
    {
        var work = Path.Combine(Path.GetDirectoryName(image)!, "worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var request = Path.Combine(work, "request.json"); var output = Path.Combine(work, "response.json");
        try
        {
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
            {
                sourcePath = image, choice = selection.OcrId, serverUrl = new Uri(endpoint, "v1").ToString().TrimEnd('/'),
                layoutModelDirectory = BookSmartOcrCatalog.LayoutDirectory(root), cacheDirectory = Path.Combine(work, "cache")
            }), token);
            var start = new ProcessStartInfo(BookSmartOcrRuntime.Python(selection))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-I", "-B", BookSmartOcrRuntime.WorkerPath, "--input", request, "--output", output }) start.ArgumentList.Add(argument);
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            using var process = OwnedProcessRegistry.Shared.Start(start, "Books.Smart.OCR");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(20));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync(CancellationToken.None); throw; }
            var log = await stdout + "\n" + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "worker.log"), log, token);
            if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException("BookConverter.Smart.OcrFailed");
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output, token));
            using var inputImage = SkiaSharp.SKCodec.Create(image);
            if (inputImage is null) throw new InvalidDataException("BookConverter.Error.InvalidImage");
            var result = Parse(document.RootElement, inputImage.Info.Width, inputImage.Info.Height);
            if (document.RootElement.TryGetProperty("assets", out var assets))
            {
                var cropped = new List<BookSmartAsset>();
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetPath = Path.GetFullPath(asset.GetProperty("path").GetString()!);
                    if (!assetPath.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("BookConverter.Error.ResourceOutside");
                    var data = await File.ReadAllBytesAsync(assetPath, token); BookConversionResources.ValidateImage(data);
                    cropped.Add(new(Path.GetFileName(assetPath), data, asset.GetProperty("blockId").GetInt32()));
                }
                result = result with { Assets = cropped.ToArray() };
            }
            // Preserve raw structured recognition alongside checkpoints, for diagnostics/replay; never embed it in public metadata.
            File.Move(output, Path.Combine(Path.GetDirectoryName(image)!, Path.GetFileNameWithoutExtension(image) + ".ocr-raw.json"), true);
            return result;
        }
        finally
        {
            // Fresh private directory only; keep failed diagnostics for investigation.
            if (File.Exists(Path.Combine(work, "response.json")) && Directory.Exists(work))
                OwnedProcessRegistry.Log("book_ocr_diagnostics", "Books.Smart", detail: work);
        }
    }
    internal static BookSmartPage Parse(JsonElement result, double width = 1, double height = 1)
    {
        if (result.GetProperty("schemaVersion").GetInt32() != 1 || result.GetProperty("status").GetString() != "ok")
            throw new InvalidDataException("BookConverter.Smart.InvalidOcr");
        var issues = result.GetProperty("issues").Deserialize<string[]>() ?? [];
        var blocks = new List<BookSmartBlock>();
        foreach (var item in result.GetProperty("blocks").EnumerateArray())
        {
            var text = item.GetProperty("text").GetString() ?? "";
            var kind = item.GetProperty("kind").GetString() ?? "paragraph";
            var uncertain = item.TryGetProperty("questionable", out var flag) && flag.GetBoolean();
            if (string.IsNullOrWhiteSpace(text))
            {
                if (kind is not ("image" or "chart")) { text = "⟦…⟧"; uncertain = true; }
            }
            string? html = null;
            if (kind is "table" or "markdown")
            {
                html = SafeMarkup(kind == "markdown" ? Markdown.ToHtml(text, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()) : text);
                text = BookMarkup.Parse(html).Body!.TextContent;
                if (string.IsNullOrWhiteSpace(text)) kind = "image";
            }
            double[]? bounds = null;
            if (item.TryGetProperty("bbox", out var bbox) && bbox.ValueKind == JsonValueKind.Array && bbox.GetArrayLength() == 4)
            {
                var raw = bbox.EnumerateArray().Select(value => value.GetDouble()).ToArray();
                bounds = [raw[0] / width, raw[1] / height, raw[2] / width, raw[3] / height];
            }
            blocks.Add(new(item.GetProperty("id").GetInt32(), text, kind, uncertain || text.Contains('\ufffd'), html, bounds));
        }
        if (blocks.Count == 0) { blocks.Add(new(0, "⟦…⟧", Questionable: true)); issues = [.. issues, "No readable text returned; original image retained."]; }
        var page = new BookSmartPage(blocks.ToArray(), issues); BookSmartProcessor.ValidatePage(page); return page;
    }
    private static string SafeMarkup(string html)
    {
        var document = BookMarkup.Parse(html); BookMarkup.Sanitize(document);
        foreach (var unwanted in document.QuerySelectorAll("img,svg,style,link,meta,audio,video,source")) unwanted.Remove();
        foreach (var element in document.All)
            foreach (var attribute in element.Attributes.ToArray())
                if (!(attribute.Name is "colspan" or "rowspan" && int.TryParse(attribute.Value, out var span) && span is > 0 and < 100))
                    element.RemoveAttribute(attribute.Name);
        return document.Body!.InnerHtml;
    }
}
