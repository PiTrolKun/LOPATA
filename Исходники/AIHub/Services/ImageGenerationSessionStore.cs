using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageGenerationSessionStore
{
    private static readonly object Gate = new();
    public static string Create(string resultsRoot)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(resultsRoot, "ImageGeneration", DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") + "_" + id[..8]);
        Directory.CreateDirectory(directory);
        Save(directory, new(id, []));
        return directory;
    }
    public static ImageGenerationSession Load(string directory) => JsonSerializer.Deserialize<ImageGenerationSession>(
        File.ReadAllBytes(Path.Combine(directory, "session.json"))) ?? throw new InvalidDataException("Invalid image generation session.");
    public static void Save(string directory, ImageGenerationSession session)
    {
        var path = Path.Combine(directory, "session.json");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(session)); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void AddTurn(ImageGenerationRequest request)
    {
        lock (Gate)
        {
        var session = Load(request.SessionDirectory);
        if (session.Turns.Any(t => t.Request.Id == request.Id)) return;
        Save(request.SessionDirectory, session with { Turns = [.. session.Turns, new(request, [])] });
        }
    }
    public static void PutResult(ImageGenerationRequest request, ImageGenerationResult result)
    {
        lock (Gate)
        {
        var session = Load(request.SessionDirectory);
        Save(request.SessionDirectory, session with { Turns = session.Turns.Select(t => t.Request.Id != request.Id ? t :
            t with { Results = t.Results.Where(r => r.Index != result.Index).Append(result).OrderBy(r => r.Index).ToArray() }).ToArray() });
        }
    }
    public static string ResultPath(ImageGenerationRequest request, int index) => Path.Combine(request.SessionDirectory, request.Id + "_" + index + ".png");
    public static ImageGenerationResult UpdateResult(ImageGenerationRequest request, int index, Func<ImageGenerationResult, ImageGenerationResult> update)
    {
        lock (Gate)
        {
            var current = Load(request.SessionDirectory).Turns.Single(t => t.Request.Id == request.Id).Results.Single(r => r.Index == index);
            var result = update(current); PutResult(request, result); return result;
        }
    }
}
