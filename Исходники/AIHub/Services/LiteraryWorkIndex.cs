using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryWorkManifest(string PartId, string Revision, string Collection, int Points, string ModelRevision);

public sealed class LiteraryWorkIndex(LiteraryProjectLayout layout, ILiterarySourceEmbedding? embedding = null)
{
    public static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Folder(LiteraryProjectLayout layout, string id) => Path.Combine(layout.Rag, "Work", Guid.Parse(id).ToString("N"));
    public static LiteraryWorkManifest? Current(LiteraryProjectLayout layout, LiterarySource source, string text)
    {
        var path = Path.Combine(Folder(layout, source.Id), "manifest.json");
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<LiteraryWorkManifest>(LiteraryChapterFiles.Read(path));
        return value is not null && value.PartId == source.Id && value.Revision == Revision(text)
            && value.ModelRevision == GigaEmbeddingInstallation.Revision ? value : null;
    }
    public async Task PrepareAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        layout.EnsurePresent();
        var store = new LiteraryChapterStore(layout.Root); store.Open();
        var snapshot = LiteraryEditorSnapshot.Capture(layout.ProjectId, layout.Root, store.Index, store.Load(), false);
        var runtime = layout.CreateRuntime();
        try
        {
            foreach (var source in snapshot.Sources.Where(s => s.Id != snapshot.ActiveId))
            {
                ct.ThrowIfCancellationRequested(); layout.EnsurePresent();
                var path = Path.Combine(layout.Root, "chapters", source.FileName);
                var text = LiteraryChapterFiles.Read(path);
                if (string.IsNullOrWhiteSpace(text) || Current(layout, source, text) is not null) continue;
                var revision = Revision(text);
                var folder = layout.EnsureFolder(Path.Combine("Rag", "Work", source.Id));
                var staging = Path.Combine(folder, revision); Directory.CreateDirectory(staging);
                var input = Path.Combine(staging, "text.json"); var output = Path.Combine(staging, "vectors.jsonl");
                LiteraryChapterFiles.Write(input, JsonSerializer.Serialize(new[] { new { source = source.Id, section = source.Number, text, kind = "project" } }));
                LiteraryChapterFiles.Write(Path.Combine(folder, "pending.json"), JsonSerializer.Serialize(new { revision, partId = source.Id }));
                await (embedding ?? new GigaSourceEmbedding()).EmbedAsync(input, output, progress, ct);
                layout.EnsurePresent();
                if (Revision(LiteraryChapterFiles.Read(path)) != revision) throw new IOException("The fixed text changed during indexing.");
                var collection = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id + revision)))[..32].ToLowerInvariant();
                var points = await LiteraryRagImport.ReplaceAsync(runtime, collection, output, progress, ct);
                // A manifest is the commit point. Old versions remain unavailable unless their hash matches.
                var manifestPath = Path.Combine(folder, "manifest.json");
                var old = File.Exists(manifestPath) ? JsonSerializer.Deserialize<LiteraryWorkManifest>(LiteraryChapterFiles.Read(manifestPath)) : null;
                layout.EnsurePresent();
                LiteraryChapterFiles.Write(manifestPath, JsonSerializer.Serialize(new LiteraryWorkManifest(source.Id, revision, collection, points, GigaEmbeddingInstallation.Revision)));
                File.Delete(Path.Combine(folder, "pending.json"));
                if (old is not null && old.Collection != collection) await runtime.DeleteLiteraryIndexAsync(old.Collection, ct);
            }
        }
        finally { await runtime.StopAsync(); }
        progress.Report(new("Ready", 100, ""));
    }
}
