using System.IO;
using System.Text.Json;

namespace AIHub.Services.VectorGraphics;

public sealed record VectorRequest(string Id, string Prompt, string OutputDirectory, string? Author, DateTimeOffset CreatedUtc,
    string ModelId = VectorModels.Ridge, int Seed = 0, string? SessionId = null, AIHub.Models.VectorGenerationOptions? Options = null,
    AIHub.Services.VectorEditor.VectorEditorContext? Editor = null);
public sealed record VectorResult(VectorRequest Request, string Phase = "queued", string? Error = null, OutputProvenance? Provenance = null, string? ExportPath = null, int SyntaxCorrections = 0);

/// <summary>Per-request durable files, independent of the visible page. No old outputs are overwritten.</summary>
public static class VectorStore
{
    public const string BackgroundKind = "vector-graphics.generate";
    public static string Root => Path.Combine(AppDataPaths.BaseDirectory, "VectorGraphics", "Jobs");
    public static string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Vector.Error.Job");
        var folder = Path.Combine(Root, id); Lopata.Updates.SafeUpdatePath.RejectLinks(folder); return folder;
    }
    public static string FilePath(string id, string name) => Path.Combine(Folder(id), name);
    public static void Save(VectorResult state)
    {
        var folder = Folder(state.Request.Id); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "job.json"); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(state)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static VectorResult Load(string id)
    {
        var path = FilePath(id, "job.json");
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Vector.Error.Job");
        var state = JsonSerializer.Deserialize<VectorResult>(File.ReadAllText(path)) ?? throw new InvalidDataException("Vector.Error.Job");
        if (state.Request.Id != id) throw new InvalidDataException("Vector.Error.Job"); return state;
    }
    public static VectorResult[] History()
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<VectorResult>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try { result.Add(Load(Path.GetFileName(dir))); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("vector_history_unavailable", "Vector", detail: error.GetType().Name); }
        }
        return result.OrderBy(x => x.Request.CreatedUtc).ToArray();
    }
}
