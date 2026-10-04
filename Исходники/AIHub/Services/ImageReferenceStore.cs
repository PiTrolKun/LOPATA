using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public static class ImageReferenceStore
{
    private sealed record Receipt(ImageReferenceRequest Request, string Text);
    public static async Task<ImageReferenceRequest> CreateAsync(string session, string draft, string language,
        ImageReferenceSelection selection, CancellationToken token)
    {
        var original = await new ImageAnalysisFileValidationService().ValidateAsync(selection.Path, token);
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(session, "references", id); Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, "reference" + original.Extension);
        await using (var input = File.OpenRead(selection.Path))
        await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(output, token);
        var saved = await new ImageAnalysisFileValidationService().ValidateAsync(copy, token);
        if (saved.Sha256 != original.Sha256) throw new InvalidDataException("Generation.ReferenceChanged");
        return new(id, draft, language, selection.Scope, copy, saved.Sha256, session);
    }
    public static string Append(string draft, string fragment) => draft.Length == 0 ? fragment : draft + "\n\n" + fragment;
    public static ImageReferenceRequest ReadRequest(string session, string id)
    {
        var saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(PathFor(session, id)));
        if (saved?.Request.Id != id || saved.Request.SessionDirectory != session || string.IsNullOrWhiteSpace(saved.Text))
            throw new InvalidDataException("Generation.ReferenceInvalidReceipt");
        return saved.Request;
    }
    public static string? Read(ImageReferenceRequest request)
    {
        var path = PathFor(request.SessionDirectory, request.Id); if (!File.Exists(path)) return null;
        var saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (saved?.Request != request || string.IsNullOrWhiteSpace(saved.Text))
            throw new InvalidDataException("Generation.ReferenceInvalidReceipt");
        return saved.Text;
    }
    public static void Save(ImageReferenceRequest request, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Generation.PromptEmptyReply");
        var path = PathFor(request.SessionDirectory, request.Id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Receipt(request, text)), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string PathFor(string session, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Generation.ReferenceInvalidReceipt");
        return Path.Combine(session, "references", id, "result.json");
    }
}
