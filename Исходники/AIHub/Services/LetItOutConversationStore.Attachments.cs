using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    internal const int AttachmentLimit = 16;
    internal const long AttachmentBytes = SessionFileToolService.MaximumStructuredFileBytes;
    private static readonly HashSet<string> AttachmentImages = new([".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AttachmentDocuments = new([".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".log", ".docx", ".pdf"], StringComparer.OrdinalIgnoreCase);
    internal static bool SupportsAttachment(string path) => AttachmentImages.Contains(Path.GetExtension(path)) || AttachmentDocuments.Contains(Path.GetExtension(path)) || IsAudioAttachment(path);

    private static void InitializeAttachments(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS attachment(
              conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
              id TEXT NOT NULL, metadata TEXT NOT NULL, PRIMARY KEY(conversation_id,id));
            CREATE TABLE IF NOT EXISTS attachment_draft(
              conversation_id TEXT PRIMARY KEY REFERENCES conversation(id) ON DELETE CASCADE, ids TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        InitializeAudioAttachments(connection);
    }
    internal string AttachmentPath(string conversation, LetItOutAttachment attachment)
    {
        Id(conversation); Id(attachment.Id);
        if (!SupportsAttachment("file" + attachment.Extension) || attachment.Extension != Path.GetExtension("file" + attachment.Extension))
            throw new InvalidDataException("LetItOut.Attachments.Invalid");
        var path = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Attachments", conversation, attachment.Id, "original" + attachment.Extension);
        Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path;
    }
    public LetItOutAttachment Attachment(string conversation, string id)
    {
        Id(conversation); Id(id); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT metadata FROM attachment WHERE conversation_id=$c AND id=$id";
        cmd.Parameters.AddWithValue("$c", conversation); cmd.Parameters.AddWithValue("$id", id);
        return JsonSerializer.Deserialize<LetItOutAttachment>(cmd.ExecuteScalar() as string ?? throw new InvalidDataException("LetItOut.Attachments.Missing"))!;
    }
    internal IReadOnlyList<LetItOutAttachment> BoundAttachments(string conversation)
    {
        var saved = Load(conversation) ?? throw new InvalidDataException("LetItOut.Attachments.Missing");
        return saved.Messages.SelectMany(m => m.Attachments ?? []).Distinct().Select(id => Attachment(conversation, id)).ToArray();
    }
    internal async Task<LetItOutAttachment> ImportAttachmentAsync(string conversation, string source, CancellationToken token)
    {
        Id(conversation); if (!SupportsAttachment(source)) throw new InvalidDataException("LetItOut.Attachments.Unsupported");
        Lopata.Updates.SafeUpdatePath.RejectLinks(source);
        var info = new FileInfo(source);
        if (info.Length <= 0 || info.Length > AttachmentBytes) throw new InvalidDataException("LetItOut.Attachments.Size");
        var name = Path.GetFileName(source); if (name.Length > 200) name = name[..200];
        name = string.Concat(name.Select(c => char.IsControl(c) ? ' ' : c));
        var draft = new LetItOutAttachment(Guid.NewGuid().ToString("N"), name, info.Extension.ToLowerInvariant(),
            IsAudioAttachment(source) ? "audio" : AttachmentImages.Contains(info.Extension) ? "image" : "document", info.Length, "", "", DateTimeOffset.UtcNow);
        var path = AttachmentPath(conversation, draft); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await input.CopyToAsync(output, token); if (output.Length != draft.Bytes) throw new IOException("LetItOut.Attachments.Changed"); }
            using (var copy = File.OpenRead(path)) draft = draft with { Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(copy, token)) };
            if (draft.Kind == "document")
            {
                if (draft.Extension == ".pdf")
                {
                    using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
                    var hasText = false;
                    foreach (var pdfPage in pdf.GetPages())
                    { token.ThrowIfCancellationRequested(); if (!string.IsNullOrWhiteSpace(pdfPage.Text)) { hasText = true; break; } }
                    if (!hasText) throw new InvalidDataException("LetItOut.Attachments.NoText");
                }
                var page = ReadAttachmentFile(path, draft, 0, 600, token);
                using var parsed = JsonDocument.Parse(page); var text = parsed.RootElement.GetProperty("content").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("LetItOut.Attachments.NoText");
                if (text.Contains('\uFFFD')) throw new InvalidDataException("LetItOut.Attachments.Encoding");
                draft = draft with { Preview = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TruncateMemory(280) };
            }
            else if (draft.Kind == "image")
            {
                _ = await new VisionImagePayloadService().PrepareAsync(AttachmentReference(path, draft), token);
                draft = draft with { Preview = "Image / изображение: " + draft.Name };
            }
            else draft = draft with { Preview = "Аудио: требуется автоматическое распознавание речи, другие звуки не анализируются." };
            token.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO attachment VALUES($c,$id,$data)";
            cmd.Parameters.AddWithValue("$c", conversation); cmd.Parameters.AddWithValue("$id", draft.Id);
            cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(draft)); cmd.ExecuteNonQuery(); return draft;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    private static SessionFileReference AttachmentReference(string path, LetItOutAttachment a) => new() {
        Id = a.Id, SourcePath = path, DisplayName = a.Name, Extension = a.Extension,
        Category = a.Kind == "image" ? SessionFileCategories.Image : SessionFileCategories.Document, SizeBytes = a.Bytes, IsAvailable = true };
    private static string ReadAttachmentFile(string path, LetItOutAttachment a, int offset, int maximum, CancellationToken token) =>
        new SessionFileToolService().Read(new() { Intent = SessionFileIntentStatuses.Selected, Files = [AttachmentReference(path, a)] }, a.Id, offset, maximum, token);
    internal string ReadAttachmentPage(string conversation, string id, int offset, int maximum, CancellationToken token)
    {
        var a = Attachment(conversation, id);
        if (a.Kind == "audio") return ReadAudioTranscript(conversation, a, offset, maximum, token);
        if (a.Kind != "document") throw new InvalidDataException("LetItOut.Attachments.Invalid");
        return ReadAttachmentFile(AttachmentPath(conversation, a), a, offset, maximum, token);
    }
    internal string SearchAttachment(string conversation, string id, string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("Invalid attachment search.");
        for (var offset = 0; offset <= SessionFileToolService.MaximumOffset; offset += 12000)
        {
            using var page = JsonDocument.Parse(ReadAttachmentPage(conversation, id, offset, 12200, token));
            var value = page.RootElement; var content = value.GetProperty("content").GetString() ?? "";
            var match = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (match >= 0) return ReadAttachmentPage(conversation, id, Math.Max(0, offset + match - 1000), 6000, token);
            if (!value.GetProperty("has_more").GetBoolean()) return JsonSerializer.Serialize(new { found = false, searched_through = offset + content.Length });
        }
        return JsonSerializer.Serialize(new { found = false, search_limit = SessionFileToolService.MaximumOffset });
    }
    private IReadOnlyList<string> DraftAttachmentIds(string conversation)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT ids FROM attachment_draft WHERE conversation_id=$c";
        cmd.Parameters.AddWithValue("$c", conversation); return JsonSerializer.Deserialize<string[]>(cmd.ExecuteScalar() as string ?? "[]")!;
    }
    internal void SaveDraftAttachments(string conversation, IReadOnlyList<string> ids)
    {
        ValidateAttachments(conversation, ids); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO attachment_draft VALUES($c,$ids) ON CONFLICT(conversation_id) DO UPDATE SET ids=$ids";
        cmd.Parameters.AddWithValue("$c", conversation); cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids)); cmd.ExecuteNonQuery();
    }
    private void ValidateAttachments(string conversation, IReadOnlyList<string>? ids)
    {
        if (ids is null) return;
        if (ids.Count > AttachmentLimit || ids.Distinct().Count() != ids.Count) throw new InvalidDataException("LetItOut.Attachments.Count");
        foreach (var id in ids) _ = Attachment(conversation, id);
    }
    internal string AttachmentIndex(string conversation, IEnumerable<string>? ids) => string.Join("\n", (ids ?? []).Select(id => {
        var a = Attachment(conversation, id); return $"attachment://{conversation}/{a.Id} · {a.Name} · {a.Kind} · {a.Preview}";
    }));
    private void DeleteAttachmentCopies(string conversation)
    {
        Id(conversation);
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Attachments"));
        var target = Path.GetFullPath(Path.Combine(root, conversation));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid attachment directory.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(target);
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
