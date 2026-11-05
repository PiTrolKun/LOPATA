using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AIHub.Models;
using AIHub.Services.MusicLora;

namespace AIHub.Services.ImageLora;

internal sealed class ImageLoraSearch : IDisposable
{
    private readonly MusicLoraHubClient _hub;
    internal ImageLoraSearch(System.Net.Http.HttpMessageHandler? handler = null) => _hub = new(handler);
    internal static string Base(string model) => model switch { "z-image" => "Tongyi-MAI/Z-Image-Turbo", "krea" => "krea/Krea-2-Turbo", _ => throw new InvalidDataException("Generation.Lora.Incompatible") };
    internal static string[] Strings(JsonElement e) => e.ValueKind == JsonValueKind.String ? [e.GetString()!] : e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().SelectMany(Strings).ToArray() : [];
    private static string S(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    internal static bool ExactBase(JsonElement info, string model) => info.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object
        && card.TryGetProperty("base_model", out var b) && Strings(b).Contains(Base(model), StringComparer.Ordinal);
    internal async Task SearchAsync(string model, string query, IProgress<ImageLoraDefinition> items, IProgress<string> status, CancellationToken token)
    {
        var exact = MusicLoraHubClient.ExactRepository(query.Trim());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<string>(32); var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        async Task Add(string repo) { if (MusicLoraHubClient.Repository(repo) && seen.TryAdd(repo, 0)) await queue.Writer.WriteAsync(repo, cancel.Token); }
        async Task Discover() {
            try {
                if (exact is not null) { await Add(exact); return; }
                var routes = new[] { "?filter=" + Uri.EscapeDataString("base_model:adapter:" + Base(model)),
                    "?search=" + Uri.EscapeDataString(model == "krea" ? "krea" : "z-image") };
                await Parallel.ForEachAsync(routes, cancel.Token, async (route, ct) => {
                    await foreach (var page in _hub.PagesAsync(route + "&limit=50&full=true&cardData=true&sort=lastModified&direction=-1", ct))
                        foreach (var d in page.EnumerateArray()) if (ExactBase(d, model)) await Add(S(d, "id"));
                });
            } catch { cancel.Cancel(); throw; }
            finally { queue.Writer.TryComplete(); }
        }
        async Task Inspect() {
            try {
                await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(cancel.Token), new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancel.Token },
                    async (repo, ct) => {
                        try {
                            await InspectAsync(repo, model, d => { if (exact is not null || Relevant(d, query)) items.Report(d); }, status, ct, exact is null ? query : null);
                        } catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) {
                            status.Report(repo + " · " + e.Message);
                        }
                    });
            } catch { cancel.Cancel(); throw; }
        }
        await Task.WhenAll(Discover(), Inspect());
    }
    internal static string[] Terms(string query)
    {
        var q = query.ToLowerInvariant().Trim();
        if (q.Length == 0) return [];
        var aliases = new Dictionary<string, string[]> {
            ["реализм"] = ["realism", "realistic", "photo"], ["живопись"] = ["painting", "paint", "watercolor", "brush", "ink"],
            ["персонажи"] = ["character", "identity"], ["пиксельная графика"] = ["pixel", "pixelart"],
            ["меньше цензуры"] = ["uncensored", "refusal", "censor", "nsfw", "unrestricted"],
            ["улучшение отрисовки тела"] = ["anatomy", "hands", "body", "pose", "proportion"] };
        var english = new Dictionary<string,string> { ["realism"]="реализм", ["painting"]="живопись", ["characters"]="персонажи",
            ["pixel art"]="пиксельная графика", ["less censorship"]="меньше цензуры", ["body and anatomy"]="улучшение отрисовки тела" };
        if (english.TryGetValue(q, out var russian)) q=russian;
        return aliases.TryGetValue(q, out var terms) ? terms : q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
    internal static bool Relevant(ImageLoraDefinition d, string query)
    {
        var terms = Terms(query); var text = d.Name + " " + d.Repository + " " + d.AuthorText + " " + string.Join(" ", d.Tags);
        return terms.Length == 0 || terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
    internal async Task InspectAsync(string repo, string model, Action<ImageLoraDefinition> publish, IProgress<string> status, CancellationToken token, string? query = null)
    {
        var detail = await _hub.JsonAsync("/" + repo + "?blobs=true", token); var revision = S(detail, "sha");
        if (!Regex.IsMatch(revision, @"\A[a-f0-9]{40,64}\z")) throw new InvalidDataException("Generation.Lora.Invalid");
        detail = await _hub.JsonAsync("/" + repo + "/revision/" + revision + "?blobs=true", token);
        if (S(detail, "sha") != revision || !ExactBase(detail, model)) throw new InvalidDataException("Generation.Lora.Incompatible");
        var card = detail.GetProperty("cardData"); var license = S(card, "license");
        if (license.Length == 0) throw new InvalidDataException("Generation.Lora.NoLicense");
        var author = await _hub.TextAsync(repo, revision, "README.md", token);
        var termsToFind=Terms(query ?? "");
        if(termsToFind.Length>0 && !termsToFind.Any(t=>(repo+" "+author).Contains(t,StringComparison.OrdinalIgnoreCase))) return;
        var terms = "Source: https://huggingface.co/" + repo + "/tree/" + revision + "\nDeclared license: " + license + "\n\n" + author;
        var files = detail.GetProperty("siblings").EnumerateArray().ToArray();
        var documents=files.Where(f => S(f, "rfilename") is "LICENSE" or "LICENSE.md" or "LICENSE.txt" or "COPYING" or "LICENSE.pdf").Take(2).ToArray();
        if(license=="other" && documents.Length==0) throw new InvalidDataException("Generation.Lora.NoLicense");
        foreach (var f in documents) {
            var name=S(f,"rfilename");
            if(name.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase)) {
                var pdfBytes=await _hub.FileAsync(MusicLoraHubClient.Resolve(repo,revision,name)+"?download=true",token,512000);
                var extracted=await Task.Run(()=>{
                    using var pdf=UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
                    if(pdf.NumberOfPages>50) throw new InvalidDataException("Generation.Lora.Invalid");
                    return string.Join("\n\n",pdf.GetPages().Select(p=>p.Text));
                },token);
                if(string.IsNullOrWhiteSpace(extracted)) throw new InvalidDataException("Generation.Lora.NoLicense");
                terms+="\n\n"+name+"\n"+extracted;
            } else terms += "\n\n" + await _hub.TextAsync(repo, revision, name, token);
        }
        if (terms.Length > 290000) throw new InvalidDataException("Generation.Lora.Invalid");
        foreach (var f in files.Where(f => S(f, "rfilename").EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))) {
            token.ThrowIfCancellationRequested(); var path = S(f, "rfilename");
            try {
                if (!f.TryGetProperty("size", out var n) || !n.TryGetInt64(out var size) || size is <= 8 or > 3000000000
                    || !f.TryGetProperty("lfs", out var lfs) || !Regex.IsMatch(S(lfs, "sha256"), @"\A[a-f0-9]{64}\z")) continue;
                var known = ImageLoraCatalog.Bundled.SingleOrDefault(d => d.ModelId == model && d.Supported
                    && d.Repository == repo && d.Revision == revision && d.Files.Any(file => file.Sha256 == S(lfs, "sha256")));
                if (known is not null) { publish(known); continue; }
                var url = MusicLoraHubClient.Resolve(repo, revision, path);
                var header = await _hub.HeaderAsync(url, size, token, validateMusic: false); ImageLoraTensorRules.Validate(header, model);
                var trigger = S(card, "trigger_phrase");
                if (trigger.Length == 0) {
                    var match = Regex.Match(author, @"(?im)^\s*[-*]?\s*\*?\*?Trigger (?:word|phrase)s?[:*\s]+`([^`\r\n]{1,200})`");
                    if (match.Success) trigger = match.Groups[1].Value;
                }
                var d = new ImageLoraDefinition(ImageLoraStore.Digest(repo + "@" + revision + ":" + path)[..32],
                    Path.GetFileNameWithoutExtension(path), "Описание автора: " + Plain(author), "Author information: " + Plain(author),
                    model, repo, revision, license, trigger, [], [new ManagedModelArtifactFile { RelativePath = "adapter.safetensors",
                        SizeBytes = size, Sha256 = S(lfs, "sha256"), SourceUrl = url, Purpose = "image_lora" }], "lora", true,
                    terms, ImageLoraDescriptionService.DescriptionFacts(author));
                ImageLoraCatalog.Validate(d); publish(d);
            } catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { status.Report(path + " · " + e.Message); }
        }
    }
    private static string Plain(string text)
    {
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)|<[^>]*>", "");
        return text[..Math.Min(text.Length, 1200)];
    }
    public void Dispose() => _hub.Dispose();
}
