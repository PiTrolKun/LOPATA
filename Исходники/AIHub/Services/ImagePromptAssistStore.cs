using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>The completed prompt is committed before the background operation completes.</summary>
public static class ImagePromptAssistStore
{
    private sealed record Receipt(ImagePromptAssistRequest Request, string Text);
    public static ImagePromptAssistRequest ReadRequest(string sessionDirectory, string id)
    {
        var saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(PathFor(sessionDirectory, id)));
        if (saved?.Request.Id != id || saved.Request.SessionDirectory != sessionDirectory || string.IsNullOrWhiteSpace(saved.Text))
            throw new InvalidDataException("Generation.PromptInvalidReceipt");
        return saved.Request;
    }
    public static string? Read(ImagePromptAssistRequest request)
    {
        var path = PathFor(request);
        if (!File.Exists(path)) return null;
        var saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (saved?.Request != request || string.IsNullOrWhiteSpace(saved.Text))
            throw new InvalidDataException("Generation.PromptInvalidReceipt");
        return saved.Text;
    }
    public static void Save(ImagePromptAssistRequest request, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Generation.PromptEmptyReply");
        var path = PathFor(request); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Receipt(request, text)), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string PathFor(ImagePromptAssistRequest request)
        => PathFor(request.SessionDirectory, request.Id);
    private static string PathFor(string sessionDirectory, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Generation.PromptInvalidReceipt");
        return Path.Combine(sessionDirectory, "prompt-assistance", id + ".json");
    }
}
