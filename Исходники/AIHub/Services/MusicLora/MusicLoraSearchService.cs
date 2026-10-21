using System.IO;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services.MusicLora;

internal sealed partial class MusicLoraSearchService
{
    private static readonly HashSet<string> Licenses = new(StringComparer.OrdinalIgnoreCase) {
        "mit", "apache-2.0", "cc0-1.0", "cc-by-4.0", "cc-by-sa-4.0", "cc-by-nc-4.0", "cc-by-nc-sa-4.0", "bsd-3-clause", "bsd-2-clause" };
    internal static bool Supported(string variation) => variation == MusicAceCatalog.Variation
        || variation == MusicStudioRuntime.Variation && EngineSetSelection.ActiveId == "002";
    private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static long? N(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static string[] Strings(JsonElement e) => e.ValueKind == JsonValueKind.String ? [e.GetString()!]
        : e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
    private static string Limit(string s, int n) => s[..Math.Min(s.Length, n)];
    private readonly Func<MusicLoraHubClient> _createHub;
    internal MusicLoraSearchService(Func<MusicLoraHubClient>? createHub = null) => _createHub = createHub ?? (() => new MusicLoraHubClient());
    private static bool RateLimited(Exception e) => e is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
    private static bool Skippable(Exception e) => e is InvalidDataException or JsonException or KeyNotFoundException
        or InvalidOperationException or FormatException or OverflowException or ArgumentException or HttpRequestException or IOException or TaskCanceledException;
    internal async Task<MusicLoraSearchResult> SearchAsync(string variation, MusicLoraSearchQuery query, IProgress<string> progress,
        CancellationToken token, IProgress<MusicLoraSearchItem>? items = null, MusicLoraSearchExclusions? excluded = null)
    {
        if (!Supported(variation)) throw new InvalidDataException("Music.Lora.Incompatible");
        if (query.Text.Length > 2000) throw new InvalidDataException("Music.Lora.Search.QueryTooLong");
        var exact = MusicLoraHubClient.ExactRepository(query.Name.Trim()); var now = DateTimeOffset.UtcNow;
        using var hub = _createHub(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var found = new ConcurrentDictionary<string, MusicLoraSearchItem>(); var notices = new ConcurrentQueue<string>();
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var families = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        int inspected = 0, checking = 0;
        void Publish(MusicLoraSearchItem item) {
            if (excluded?.Include(item.Definition) == false || !Relevant(item, query, exact)) return;
            var family = MusicLoraUsageHints.Family(item.Definition);
            if (family is not null && families.GetOrAdd(family, item.Definition.Id) != item.Definition.Id) return;
            found[item.Definition.Id] = item; items?.Report(item);
        }
        void Notice(string message) {
            // Bound diagnostic repetition, not results. Every exact-repository reason remains visible.
            if (exact is not null || notices.Count < 100) notices.Enqueue(message);
        }
        foreach (var d in MusicLoraCatalog.Bundled.Where(d => d.Variation == variation && (exact is null || d.Repository.Equals(exact, StringComparison.OrdinalIgnoreCase))))
            Publish(new(d, d.DescriptionRu + "\n" + d.DescriptionEn, "", d.Repository.Split('/')[0], null, null, now,
                d.Files.Select(f => f.SourceUrl).ToArray(), true));
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });
        async Task AddAsync(string repo) {
            if (MusicLoraHubClient.Repository(repo) && seen.TryAdd(repo, 0)) await queue.Writer.WriteAsync(repo, stop.Token);
        }
        async Task FetchAsync(string route) {
            try {
                await foreach (var page in hub.PagesAsync(route, stop.Token)) {
                    foreach (var item in page.EnumerateArray()) if (RepositoryCandidate(item, variation)) await AddAsync(S(item, "id"));
                }
            } catch (Exception e) when (!stop.IsCancellationRequested && Skippable(e)) {
                Notice(e.Message); if (RateLimited(e)) stop.Cancel();
            }
        }
        async Task DiscoverAsync() {
            try {
                if (exact is not null) { await AddAsync(exact); return; }
                progress.Report("Music.Lora.Search.Discovering");
                const string options = "&limit=50&full=true&cardData=true&sort=lastModified&direction=-1";
                var bases = variation == MusicAceCatalog.Variation ? new[] { "ACE-Step/acestep-v15-xl-turbo", "ACE-Step/acestep-v15-xl-turbo-diffusers" } : new[] { "m-a-p/YuE2-3B" };
                var routes = bases.Select(b => "?filter=" + Uri.EscapeDataString("base_model:adapter:" + b) + options).ToList();
                var terms = variation == MusicAceCatalog.Variation ? new[] { "acestep", "ace-step", "audio-sliders" } : new[] { "yue2" };
                if (query.Name.Trim().Length > 0) terms = [query.Name.Trim(), .. terms];
                routes.AddRange(terms.Select(term => "?search=" + Uri.EscapeDataString(term) + options));
                if (Regex.IsMatch(query.Name.Trim(), @"\A[A-Za-z0-9][A-Za-z0-9_-]{1,60}\z"))
                    routes.Add("?author=" + Uri.EscapeDataString(query.Name.Trim()) + options);
                await Parallel.ForEachAsync(routes, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = stop.Token },
                    async (route, _) => await FetchAsync(route));
            } finally { queue.Writer.TryComplete(); }
        }
        async Task InspectAsync() {
            await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(stop.Token),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop.Token }, async (repo, _) => {
                    progress.Report($"{Interlocked.Increment(ref checking)}/{seen.Count} · {repo}");
                    try {
                        var count = await InspectRepositoryAsync(repo, variation, exact, hub, Publish, Notice, stop.Token, excluded);
                        Interlocked.Add(ref inspected, count);
                    } catch (Exception e) when (!stop.IsCancellationRequested && RateLimited(e)) {
                        Notice(e.Message); stop.Cancel();
                    }
                });
        }
        async Task GuardAsync(Func<Task> run) {
            try { await run(); }
            catch { stop.Cancel(); throw; }
        }
        try { await Task.WhenAll(GuardAsync(DiscoverAsync), GuardAsync(InspectAsync)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (inspected == 0 && notices.Count > 0 && !stop.IsCancellationRequested && MusicLoraSearchStore.Cache(variation, query) is { } offline) {
            offline = offline with { Items = offline.Items.Where(i => excluded?.Include(i.Definition) != false).ToArray() };
            foreach (var item in offline.Items) items?.Report(item); return offline;
        }
        if (found.IsEmpty && exact is not null && notices.IsEmpty) Notice("Music.Lora.Search.NoCompatibleFiles");
        var result = new MusicLoraSearchResult(found.Values.Where(i => excluded?.Include(i.Definition) != false).OrderByDescending(i => i.Verified).ThenByDescending(i => Score(i, query))
            .ThenBy(i => i.Definition.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Definition.En, StringComparer.OrdinalIgnoreCase).ToArray(),
            notices.ToArray(), now, Stopped: stop.IsCancellationRequested);
        MusicLoraSearchStore.Cache(variation, query, result); return result;
    }
    private static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static bool RepositoryCandidate(JsonElement info, string variation, string? repository = null)
    {
        var repo = repository ?? S(info, "id");
        // A reviewed catalog repository may omit task/base metadata; its existing recipes remain usable.
        if (MusicLoraCatalog.Bundled.Any(d => d.Variation == variation && d.Repository.Equals(repo, StringComparison.OrdinalIgnoreCase))) return true;
        var task = S(info, "pipeline_tag");
        if (task.Length > 0 && task is not ("text-to-audio" or "audio-to-audio" or "unconditional-audio-generation")) return false;
        var bases = info.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object && card.TryGetProperty("base_model", out var b)
            ? Strings(b) : [];
        var compatible = variation == MusicAceCatalog.Variation
            ? bases.Any(b => b is "ACE-Step/acestep-v15-xl-turbo" or "ACE-Step/acestep-v15-xl-turbo-diffusers") : bases.Contains("m-a-p/YuE2-3B");
        if (bases.Length > 0) return compatible;
        // Sparse metadata must at least identify this music family, not a generic LoRA/image library.
        return task.Length > 0 || Regex.IsMatch(repo, variation == MusicAceCatalog.Variation ? "ace.?step|audio.sliders" : "yue2", RegexOptions.IgnoreCase);
    }
    private static int FilePriority(string path)
    {
        // Spend header requests on known layouts first, even in repositories containing many training intermediates.
        // This is an inspection budget rule, never a popularity or final result ranking rule.
        if (path.EndsWith("adapter_model.safetensors", StringComparison.Ordinal)) return 4;
        if (path.Contains("/native/", StringComparison.Ordinal) || path.StartsWith("native/", StringComparison.Ordinal)) return 3;
        if (path.Contains("ace-step-1.5-xl-turbo/", StringComparison.Ordinal) || path.StartsWith("yue2/", StringComparison.Ordinal)) return 2;
        if (Path.GetFileName(path).StartsWith("ar_lora", StringComparison.Ordinal)) return 1;
        return 0;
    }
    private static bool Has(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind is not JsonValueKind.Null
        && !(v.ValueKind == JsonValueKind.Object && !v.EnumerateObject().Any());
    private static IEnumerable<string> Tokens(string s)
    {
        var aliases = new Dictionary<string, string> { ["рок"] = "rock", ["поп"] = "pop", ["джаз"] = "jazz", ["рэп"] = "rap", ["груст"] = "sad",
            ["спокой"] = "calm", ["энерг"] = "energy", ["женск"] = "female", ["мужск"] = "male", ["гитар"] = "guitar", ["пианино"] = "piano", ["фолк"] = "folk", ["блюз"] = "blues" };
        foreach (Match m in Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]{3,40}")) {
            yield return m.Value; foreach (var a in aliases.Where(a => m.Value.StartsWith(a.Key))) yield return a.Value;
        }
    }
    private static string SearchText(MusicLoraSearchItem i) => i.Definition.En + " " + i.Definition.Ru + " " + i.Definition.Repository + " "
        + i.Definition.DescriptionEn + " " + i.Definition.DescriptionRu + " " + string.Join(" ", i.Definition.Tags);
    private static bool Relevant(MusicLoraSearchItem i, MusicLoraSearchQuery q, string? exact)
    {
        var text = SearchText(i); var names = Tokens(q.Name).ToArray(); var parameters = Tokens(q.Parameters).ToArray();
        return (exact is not null || names.Length == 0 || names.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            && (parameters.Length == 0 || parameters.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }
    // Popularity is deliberately absent from both eligibility and scoring.
    private static int Score(MusicLoraSearchItem i, MusicLoraSearchQuery q) => Tokens(q.Text).Distinct()
        .Count(t => SearchText(i).Contains(t, StringComparison.OrdinalIgnoreCase));
}
