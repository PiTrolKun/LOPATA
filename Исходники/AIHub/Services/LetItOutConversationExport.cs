using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record LetItOutExportRequest(string ConversationId, string Destination, string Language);

internal static class LetItOutConversationExport
{
    internal const string Kind = "chat.let_it_out.export";
    internal static async Task RunAsync(LetItOutConversationStore store, LetItOutExportRequest request,
        CancellationToken token, BackgroundOperationState? restored = null)
    {
        var state = restored ?? new BackgroundOperationState { Kind = Kind, Project = request.ConversationId,
            Title = "LOPATA · " + (request.Language == "ru" ? "Экспорт темы" : "Export conversation"), Private = true,
            Input = JsonSerializer.SerializeToElement(request) };
        async Task<bool> Execute(CancellationToken attempt)
        {
            try { await WriteAsync(store, request, attempt); return true; }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new BackgroundOperationFailedException("LetItOut.Export.Failed", error); }
        }
        if (ApplicationBackgroundOperations.Current is { } controller)
            await controller.RunAsync(state, Execute, () => Task.CompletedTask, token);
        else await Execute(token);
    }

    internal static async Task WriteAsync(LetItOutConversationStore store, LetItOutExportRequest request, CancellationToken token)
    {
        var destination = Path.GetFullPath(request.Destination);
        if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP export required.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var root = Path.GetFullPath(Path.GetDirectoryName(store.DatabasePath)!);
        if (destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export cannot replace private conversation storage.");
        var conversation = store.Load(request.ConversationId) ?? throw new InvalidDataException("Conversation was removed.");
        var pairs = store.MemoryPairs(request.ConversationId); var summary = store.MemorySnapshot(request.ConversationId);
        var map = store.Tree(request.ConversationId).Document;
        var files = conversation.Messages.SelectMany(m => m.Attachments ?? []).Distinct().Select(id => store.Attachment(request.ConversationId, id)).ToArray();
        // Historical messages do not carry per-answer model identities. Never replace that gap with the current selected model.
        var recorded = new List<OutputModelUse>();
        if (map is not null) recorded.Add(new(map.Model, map.Model, "unknown", "topic_map"));
        if (summary is not null && store.RecordedSummaryModel(request.ConversationId, summary.ThroughPair) is { } summaryModel)
            recorded.Add(new(summaryModel, summaryModel, "unknown", "conversation_summary"));
        var provenance = new OutputProvenance { Scenario = "let_it_out/export", CreatedUtc = conversation.Summary.CreatedUtc,
            ModelHistory = "unknown-per-message; exported summaries/map retain recorded identities when available",
            ModelScope = "original conversation; exporter invokes no model", Models = recorded.ToArray() };
        _ = provenance.Json();
        var manifest = new { provenance, operation = Kind, conversation = new { conversation.Summary.Id, conversation.Summary.Title,
                conversation.Summary.CreatedUtc, conversation.Summary.UpdatedUtc, messages = conversation.Messages },
            summary = new { automatic = true, available = summary is not null, value = summary },
            map = new { automatic = true, available = map is not null, value = map },
            // Source links are public references, not the unrelated conversations' private contents.
            memories = conversation.Messages.Select((m, i) => new { ordinal = i, sources = store.RecallLinks(request.ConversationId, i)
                .Select(s => new { topic = s.Title, conversation_id = s.ConversationId, pair = s.Number }) }).Where(m => m.sources.Any()),
            attachments = files.Select(f => new { metadata = f, path = ArchivePath(f),
                transcript = f.Kind == "audio" ? store.ArchivedAudioTranscript(request.ConversationId, f.Id) : null }) };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var html = Html(conversation, pairs, summary, map, files, provenance, request.Language);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8);
                await Text(zip, "conversation.json", json, token); await Text(zip, "conversation.html", html, token);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var path = store.AttachmentPath(request.ConversationId, file);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var entry = zip.CreateEntry(ArchivePath(file), CompressionLevel.NoCompression);
                    await using var copy = entry.Open(); var buffer = new byte[65536]; long length = 0; int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); length += read; await copy.WriteAsync(buffer.AsMemory(0, read), token); }
                    if (length != file.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Attachment changed during export.");
                }
            }
            token.ThrowIfCancellationRequested();
            if (store.IsDeleted(request.ConversationId)) throw new InvalidDataException("Conversation was removed.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(destination); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ArchivePath(LetItOutAttachment f) => "Attachments/" + f.Id + "/original" + f.Extension;
    private static async Task Text(ZipArchive zip, string path, string text, CancellationToken token)
    { await using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open(); await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token); }

    private static string Html(LetItOutConversation conversation, IReadOnlyList<LetItOutMemoryPair> pairs,
        LetItOutMemorySnapshot? summary, LetItOutTreeDocument? map, LetItOutAttachment[] files, OutputProvenance provenance, string language)
    {
        var ru = language == "ru"; string T(string a, string b) => ru ? a : b;
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var title = string.IsNullOrWhiteSpace(conversation.Summary.Title) ? T("Без названия", "Untitled") : conversation.Summary.Title;
        var b = new StringBuilder("<!doctype html><html lang=\"" + (ru ? "ru" : "en") + "\"><meta charset=\"utf-8\">")
            .Append("<meta name=\"LOPATA.Provenance\" content=\"").Append(E(provenance.Json())).Append("\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>").Append(E(title)).Append(" · LOPATA</title><style>body{font:17px system-ui;max-width:1000px;margin:36px auto;padding:0 24px;color:#222}article{padding:16px;border-bottom:1px solid #ccc}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit}aside{background:#f1f3f5;padding:16px}small{color:#555}details{margin:12px 0 12px 20px}a{color:#155ac0}</style><body>")
            .Append("<h1>").Append(E(title)).Append("</h1><small>LOPATA ").Append(E(provenance.ProgramVersion))
            .Append(" · let_it_out/export · ").Append(provenance.ExportedUtc.ToString("O")).Append("</small><p>")
            .Append(T("Авторство реплик пользователя сохранено. Модельная история отдельных ответов не записана. Экспорт не запускал модель.",
                "User authorship is preserved. Per-answer model history was not recorded. Export invoked no model.")).Append("</p>")
            .Append("<h2>").Append(T("Сохранённый итог", "Saved summary")).Append("</h2><aside><p>")
            .Append(T("Автоматический пересказ, не первоисточник. Это сохранённая сводка, а не новый итоговый совет.",
                "Automatic retelling, not an original source. This is the saved summary, not newly generated advice.")).Append("</p><pre>")
            .Append(E(summary is null ? T("Сводка ещё не создана.", "No summary has been created.") : summary.Short + "\n\n" + summary.Expanded))
            .Append("</pre>");
        if (summary is not null) b.Append("<small>").Append(T("До пары ", "Through pair ")).Append(summary.ThroughPair).Append("</small>");
        b.Append("</aside><h2>").Append(T("Карта темы", "Topic map")).Append("</h2>");
        if (map is null) b.Append("<p>").Append(T("Карта ещё не создана.", "No map has been created.")).Append("</p>");
        else
        {
            b.Append("<p>").Append(T("Автоматическая карта · ревизия ", "Automatic map · revision ")).Append(map.Revision)
                .Append(" · ").Append(E(map.Model)).Append("</p>");
            var visited = new HashSet<string>();
            void Node(LetItOutTreeNode node)
            {
                if (!visited.Add(node.Id)) return;
                b.Append("<details open><summary>").Append(E(node.Title)).Append("</summary><small>").Append(E(node.Status))
                    .Append("</small><pre>").Append(E(node.Detail)).Append("</pre>");
                foreach (var n in node.Sources)
                    if (pairs.FirstOrDefault(p => p.Number == n) is { } pair)
                        b.Append("<a href=\"#m").Append(pair.UserOrdinal).Append("\">#").Append(n).Append("</a> ");
                foreach (var child in map.Nodes.Where(n => n.Parent == node.Id)) Node(child);
                b.Append("</details>");
            }
            foreach (var node in map.Nodes.Where(n => n.Parent == "")) Node(node);
        }
        b.Append("<h2>").Append(T("Исходная переписка", "Original conversation")).Append("</h2>");
        for (var i = 0; i < conversation.Messages.Count; i++)
        {
            var message = conversation.Messages[i]; b.Append("<article id=\"m").Append(i).Append("\"><b>")
                .Append(message.Role == "user" ? T("Вы", "You") : "LOPATA").Append("</b> <small>")
                .Append(E(message.RecordedUtc?.ToString("O"))).Append(message.Complete ? "" : T(" · не завершено", " · incomplete"))
                .Append("</small><pre>").Append(E(message.Content)).Append("</pre>");
            foreach (var id in message.Attachments ?? [])
                if (files.FirstOrDefault(f => f.Id == id) is { } file)
                    b.Append("<p><a download href=\"").Append(E(ArchivePath(file))).Append("\">").Append(E(file.Name)).Append("</a></p>");
            b.Append("</article>");
        }
        return b.Append("</body></html>").ToString();
    }
}
