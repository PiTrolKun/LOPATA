using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Edits the extracted reference text, with a rebuilt index and a recoverable commit.</summary>
public sealed class LiteraryRagEditorStore(LiteraryProjectLayout layout)
{
    private static readonly string[] Files = ["text.json", "vectors.jsonl", "manifest.json"];
    private string Root => Path.Combine(layout.Rag,"Source");
    private string Marker => Path.Combine(Root,"editing.json");
    public IReadOnlyList<LiterarySourceSection> Read()
    {
        layout.EnsurePresent(); Recover();
        var path = Path.Combine(Root,"text.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<LiterarySourceSection>>(LiteraryChapterFiles.Read(path))
            ?? throw new InvalidDataException("Invalid source text.") : [];
    }
    public void Recover()
    {
        if (!File.Exists(Marker)) return;
        using var writeLock = new FileStream(Path.Combine(Root,"editing.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);
        RestoreInterrupted();
    }
    private void RestoreInterrupted()
    {
        if (!File.Exists(Marker)) return;
        var id = LiteraryChapterFiles.Read(Marker);
        if (!Guid.TryParseExact(id,"N",out _)) throw new InvalidDataException("Invalid RAG edit journal.");
        var backup = Path.Combine(Root,"Edits",id,"before"); LiteraryProjectLayout.CheckTreePath(backup);
        foreach (var name in Files) LiteraryChapterFiles.Write(Path.Combine(Root,name),LiteraryChapterFiles.Read(Path.Combine(backup,name)));
        File.Delete(Marker);
    }
    public async Task SaveAsync(IReadOnlyList<LiterarySourceSection> before, IReadOnlyList<LiterarySourceSection> after,
        IProgress<LiteraryPreparationProgress> progress, CancellationToken ct, ILiterarySourceEmbedding? embedding = null)
    {
        layout.EnsurePresent(); Recover();
        if (before.Count != after.Count || !before.Zip(after).All(x=>x.First.Source==x.Second.Source && x.First.Section==x.Second.Section)
            || after.Any(s=>string.IsNullOrWhiteSpace(s.Text)) || after.Sum(s=>(long)s.Text.Length)>LiterarySourceReader.MaxCharacters)
            throw new InvalidDataException("Invalid reference edit.");
        using var writeLock = new FileStream(Path.Combine(Root,"editing.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);
        if (ParagraphJson.Encode(Read()) != ParagraphJson.Encode(before)) throw new IOException("Reference changed in another window.");
        var id = Guid.NewGuid().ToString("N"); var staging = layout.EnsureFolder("Rag/Source/Edits/"+id);
        var backup = layout.EnsureFolder("Rag/Source/Edits/"+id+"/before");
        foreach (var name in Files) File.Copy(Path.Combine(Root,name),Path.Combine(backup,name));
        var input = Path.Combine(staging,"text.json"); var vectors = Path.Combine(staging,"vectors.jsonl");
        LiteraryChapterFiles.Write(input,JsonSerializer.Serialize(after));
        await ComponentLicenseGate.EnsureAsync([GigaEmbeddingInstallation.LicenseId,GigaEmbeddingInstallation.RuntimeLicenseId,QdrantOptions.LicenseId],ct);
        await (embedding ?? new GigaSourceEmbedding("cpu")).EmbedAsync(input,vectors,progress,ct);
        var runtime = layout.CreateRuntime();
        try
        {
            var count = await LiteraryRagImport.ReplaceAsync(runtime,id,vectors,progress,ct);
            var manifest = new LiterarySourceIndex.Manifest(id,GigaEmbeddingInstallation.Revision,1024,count,"reference");
            LiteraryChapterFiles.Write(Path.Combine(staging,"manifest.json"),JsonSerializer.Serialize(manifest));
            layout.EnsurePresent(); ct.ThrowIfCancellationRequested();
            if (ParagraphJson.Encode(Read()) != ParagraphJson.Encode(before)) throw new IOException("Reference changed while rebuilding.");
            LiteraryChapterFiles.Write(Marker,id);
            try
            {
                foreach (var name in Files) LiteraryChapterFiles.Write(Path.Combine(Root,name),LiteraryChapterFiles.Read(Path.Combine(staging,name)));
                File.Delete(Marker);
            }
            catch { RestoreInterrupted(); throw; }
        }
        finally { await runtime.StopAsync(); }
    }
}
