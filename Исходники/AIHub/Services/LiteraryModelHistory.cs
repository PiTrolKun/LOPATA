using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIHub.Services.LiteraryImport;

namespace AIHub.Services;

/// <summary>Project-wide participation, not authorship of individual passages.</summary>
public static class LiteraryModelHistory
{
    private static readonly object Gate = new();
    private sealed record History(int Schema, OutputModelUse[] Models, bool EarlierHistoryUnknown = true);
    public static void InitializeNewProject(string root)
    {
        lock (Gate)
            if (!File.Exists(PathFor(root))) LiteraryChapterFiles.Write(PathFor(root), JsonSerializer.Serialize(new History(1, [], false)));
    }
    private static string PathFor(string root) => Path.Combine(root, "model-participation.json");
    public static void Record(string root, OutputModelUse model)
    {
        LiteraryProjectLayout.CheckTreePath(root);
        lock (Gate)
        {
            using var lease = new FileStream(Path.Combine(root, "model-participation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var before = ReadHistory(root);
            if (before.Models.Contains(model)) return;
            var models = before.Models.Append(model).ToArray();
            _ = new OutputProvenance { Models = models }.Json();
            if (models.Length > 128) throw new InvalidDataException("Too many model participation records.");
            LiteraryChapterFiles.Write(PathFor(root), JsonSerializer.Serialize(new History(1, models, before.EarlierHistoryUnknown)));
        }
    }
    public static OutputModelUse[] Read(string root) => ReadHistory(root).Models;
    private static History ReadHistory(string root)
    {
        lock (Gate)
        {
            var path = PathFor(root);
            if (!File.Exists(path)) return new(1, []);
            if (new FileInfo(path).Length > 64_000) throw new InvalidDataException("Invalid model participation history.");
            var value = JsonSerializer.Deserialize<History>(File.ReadAllText(path));
            if (value is null || value.Schema != 1 || value.Models is null || value.Models.Length > 128)
                throw new InvalidDataException("Invalid model participation history.");
            _ = new OutputProvenance { Models = value.Models }.Json();
            return value;
        }
    }
    public static OutputProvenance ForProject(string root)
    {
        var history = ReadHistory(root);
        return new() { Scenario = "literature/export", ModelScope = "project participation; not passage authorship",
            Models = history.Models, ModelHistory = history.EarlierHistoryUnknown ? "recorded-since-feature; earlier-history-unknown" : "recorded-project" };
    }
    public static OutputProvenance ForImport(ImportSession session)
    {
        var models = new List<OutputModelUse>(Read(session.Root));
        foreach (var artifact in session.State.Artifacts.Where(a => a.Step.EndsWith("/settings", StringComparison.Ordinal)))
        {
            var prefix = artifact.Step[..^"/settings".Length];
            var accepted = session.State.Artifacts.Where(a => a.Step == prefix + "/http" && a.Parents.Intersect(artifact.Parents).Any())
                .Any(a =>
                {
                    using var receipt = JsonDocument.Parse(File.ReadAllText(session.ArtifactPath(a)));
                    return receipt.RootElement.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) && code is >= 200 and < 300;
                });
            if (!accepted) continue; // Prepared settings alone do not prove that a model was used.
            using var data = JsonDocument.Parse(File.ReadAllText(session.ArtifactPath(artifact)));
            if (data.RootElement.TryGetProperty("model", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } model)
            {
                var revision = data.RootElement.TryGetProperty("modelSha256", out var sha) ? sha.GetString() ?? "" : "";
                models.Add(new(model, model, revision, "import generation request"));
            }
        }
        return new() { Scenario = "literature/import-audit", Models = models.Distinct().ToArray(), ModelScope = "import session", ModelHistory = "recorded-request-settings; external-source-models-unknown" };
    }
    public static void CopyImportFacts(ImportSession session, string root)
    {
        foreach (var model in ForImport(session).Models) Record(root, model);
    }
    public static OutputModelUse MainModel => new(LiteraryModelLocation.FileName, LiteraryModelLocation.DisplayName, LiteraryModelLocation.Sha256, "literary text generation request");
    public static OutputModelUse Embeddings => new("ai-sage/Giga-Embeddings-instruct-480M-0826", "Giga-Embeddings-instruct-480M-0826", GigaEmbeddingInstallation.Revision, "semantic indexing/search");

    internal sealed class Requests(Func<string?> root) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.Ordinal) == true
                && root() is { } project)
            {
                try { Record(project, MainModel); }
                catch { response.Dispose(); throw; }
            }
            return response;
        }
    }
}
