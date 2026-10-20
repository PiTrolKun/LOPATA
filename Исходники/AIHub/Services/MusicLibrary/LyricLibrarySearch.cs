using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.MusicLibrary;

public sealed record LyricIndexJob(string Directory, string ModelsRoot);

public sealed class LyricLibrarySearch(LyricLibraryStore store, string modelsRoot, bool sharedOperation = false)
{
    public const string BackgroundKind = "music.lyrics-library.index";
    private static readonly SemaphoreSlim IndexGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Fingerprint(LyricLibrarySnapshot snapshot) => LyricEntry.Hash(string.Join(";",
        snapshot.Entries.OrderBy(e => e.Id).Select(e => $"{e.Id}:{e.Revision}:{e.TextHash}")));
    public async Task<LyricExample[]> SearchAsync(string query, IProgress<RagBookProgress> progress, CancellationToken token,
        LyricArtistSelection? artistSelection = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new InvalidDataException("Music.Library.InvalidQuery");
        if (!sharedOperation && ApplicationBackgroundOperations.Current?.BlocksNewWork == true)
            throw new InvalidOperationException("Music.Poetry.OtherOperation");
        var snapshot = await Task.Run(store.Snapshot, token);
        token.ThrowIfCancellationRequested();
        if ((artistSelection ?? LyricArtistSelection.Resolve(snapshot.Entries, query)) is { } artist)
        {
            // Exact catalog metadata needs no embeddings and cannot leak unrelated performers.
            var examples = artist.Examples(query);
            token.ThrowIfCancellationRequested();
            LyricExampleContext.ValidateCurrent(store, examples);
            return examples;
        }
        if (snapshot.Entries.Length == 0) return [];
        await LyricLibraryPreparation.EnsureAsync(modelsRoot, token);
        await ComponentLicenseGate.EnsureAsync(RagBookModels.EmbeddingId, token);
        await EnsureIndexAsync(progress, token);
        snapshot = store.Snapshot(); var fingerprint = Fingerprint(snapshot);
        var work = Path.Combine(store.DirectoryPath, "queries", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "query.json"); var output = Path.Combine(work, "hits.json");
            RagBookPreparation.AtomicJson(input, new { query, fingerprint, maximumHits = 12 });
            try { await QueryAsync(); }
            catch (LiteraryEmbeddingException error) when (new[] { "Music.Library.IndexChanged", "Invalid lyric vector",
                "database disk image is malformed", "file is not a database", "no such table" }.Any(s => error.Message.Contains(s, StringComparison.Ordinal)))
            {
                // Quarantine only replaceable derived data. Canonical works.sqlite is never touched.
                var vectors = Path.Combine(store.DirectoryPath, "vectors.sqlite");
                File.Move(vectors, vectors + "." + Guid.NewGuid().ToString("N") + ".bad");
                File.Delete(Path.Combine(store.DirectoryPath, "index.ready.json"));
                await EnsureIndexAsync(progress, token); await QueryAsync();
            }
            async Task QueryAsync()
            {
                using var lease = !sharedOperation ? ApplicationBackgroundOperations.Current?.BeginAuxiliary() : null;
                await ExecuteAsync("query", input, output, progress, token);
            }
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output, token));
            var live = store.Snapshot(); if (Fingerprint(live) != fingerprint) throw new InvalidDataException("Music.Library.IndexChanged");
            var entries = live.Entries.ToDictionary(e => e.Id);
            return doc.RootElement.GetProperty("hits").EnumerateArray().Select(h => new LyricExample(
                h.GetProperty("entryId").GetString()!, h.GetProperty("revision").GetInt32(), h.GetProperty("title").GetString()!,
                h.GetProperty("role").GetString()!, h.GetProperty("text").GetString()!, h.GetProperty("score").GetDouble()))
                .Where(h => entries.TryGetValue(h.Id, out var entry) && entry.Revision == h.Revision)
                .Select(h => h with { Artist = entries[h.Id].Artist, Genre = entries[h.Id].Genre, Style = entries[h.Id].Style, Language = entries[h.Id].Language })
                .DistinctBy(h => (h.Id, h.Text)).Take(8).ToArray();
        }
        finally { Directory.Delete(work, true); }
    }
    private async Task EnsureIndexAsync(IProgress<RagBookProgress> progress, CancellationToken token)
    {
        var snapshot = store.Snapshot();
        if (IndexMatches(Fingerprint(snapshot))) return;
        if (ApplicationBackgroundOperations.Current is { IsRunning: false })
        {
            await BuildOperationAsync(new(store.DirectoryPath, modelsRoot), progress, token); return;
        }
        await BuildCoreAsync(progress, token);
    }
    public static Task<string> BuildOperationAsync(LyricIndexJob job, IProgress<RagBookProgress> progress,
        CancellationToken token, BackgroundOperationState? restored = null) =>
        ApplicationBackgroundOperations.RunAsync(BackgroundKind, OperationTitle(), job.Directory, job, async ct => {
            var search = new LyricLibrarySearch(new(job.Directory), job.ModelsRoot);
            await LyricLibraryPreparation.EnsureAsync(job.ModelsRoot, ct);
            await search.BuildCoreAsync(progress, ct); return job.Directory;
        }, token, restored ?? new BackgroundOperationState { Kind = BackgroundKind, Title = OperationTitle(),
            Project = job.Directory, Input = JsonSerializer.SerializeToElement(job), Private = true });
    private static string OperationTitle()
    {
        var localization = new LocalizationService(); localization.Load(new AppSettingsStore().LoadOrCreate().LanguageCode);
        return localization.T("Music.Library.Indexing");
    }
    private bool IndexMatches(string fingerprint)
    {
        var path = Path.Combine(store.DirectoryPath, "index.ready.json");
        if (!File.Exists(path) || !File.Exists(Path.Combine(store.DirectoryPath, "vectors.sqlite"))) return false;
        try { using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("fingerprint").GetString() == fingerprint
                && doc.RootElement.GetProperty("protocol").GetString() == "lopata-lyrics-sections-mean-l2-370-v2"
                && doc.RootElement.GetProperty("revision").GetString() == RagBookModels.Cards(modelsRoot)
                    .Single(c => c.ModelArtifactId == RagBookModels.EmbeddingId).Revision; }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException) { return false; }
    }
    private async Task BuildCoreAsync(IProgress<RagBookProgress> progress, CancellationToken token)
    {
        await IndexGate.WaitAsync(token);
        try
        {
            var snapshot = store.Snapshot(); var fingerprint = Fingerprint(snapshot);
            if (IndexMatches(fingerprint)) return;
            var input = Path.Combine(store.DirectoryPath, "index.input.json");
            var output = Path.Combine(store.DirectoryPath, "index.ready.json");
            var payload = new { generation = snapshot.Generation, fingerprint, sections = snapshot.Entries.SelectMany(LyricSections.Split) };
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(payload, Json), token);
            if (ApplicationBackgroundOperations.Current is { IsInOperationScope: true } background) background.SaveCheckpoint(new { fingerprint });
            await ExecuteAsync("build", input, output, progress, token);
            if (Fingerprint(store.Snapshot()) != fingerprint) throw new InvalidDataException("Music.Library.IndexChanged");
        }
        finally { IndexGate.Release(); }
    }
    private async Task ExecuteAsync(string mode, string input, string output, IProgress<RagBookProgress> progress, CancellationToken token)
    {
        var args = new[] { Path.Combine(AppContext.BaseDirectory, "Tools", "music_lyrics_library.py"), "--mode", mode,
            "--model", LyricLibraryPreparation.ModelDirectory(modelsRoot), "--database", Path.Combine(store.DirectoryPath, "vectors.sqlite"),
            "--input", input, "--output", output, "--device", "auto" };
        var device = "unknown"; var profile = "cpu";
        void Report(string line)
        {
            const string profilePrefix = "[Prepare] Python profile · ";
            if (line.StartsWith(profilePrefix, StringComparison.Ordinal))
            {
                var candidate = line[profilePrefix.Length..];
                if (candidate is "cpu" or "cu126" or "cu128" or "xpu" or "rocm721") profile = candidate;
                progress.Report(new("Verifying")); return;
            }
            try { using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                if (r.TryGetProperty("device", out var d)) device = d.GetString() ?? "unknown";
                if (r.TryGetProperty("stage", out var stage)) progress.Report(new(stage.GetString()!,
                    r.TryGetProperty("done", out var done) ? done.GetInt32() : 0,
                    r.TryGetProperty("total", out var total) ? total.GetInt32() : 0)); }
            catch (JsonException) { /* Human status is separate from private worker diagnostics. */ }
        }
        try { await RunWorkerAsync(); }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or InvalidDataException or LiteraryEmbeddingException
            && (new[] { "torch", "transformers", "numpy", "tokenizers", "safetensors" }
                    .Any(name => error.Message.Contains("No module named '" + name, StringComparison.Ordinal))
                || error.Message.Contains("DLL load failed", StringComparison.Ordinal)
                || error.Message.Contains("pinned file", StringComparison.OrdinalIgnoreCase)
                || !LyricLibraryPreparation.IsReady(modelsRoot)))
        {
            LyricLibraryPreparation.InvalidateRuntime(profile);
            await LyricLibraryPreparation.EnsureAsync(modelsRoot, token);
            await RunWorkerAsync(); // One repair, one continuation; never retry model generation.
        }
        async Task RunWorkerAsync()
        {
        try { await GigaEmbeddingInstallation.RunAsync(args, Report, token); }
        catch (LiteraryEmbeddingException error) when (GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", device, error, token.IsCancellationRequested))
        { args[^1] = "cpu"; await GigaEmbeddingInstallation.RunAsync(args, Report, token); }
        }
    }
}
