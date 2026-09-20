using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public static class DeepSeekImportReader
{
    public const long MaxJsonBytes = 512L * 1024 * 1024, MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public static async Task<ImportInput> ReadAsync(ImportSession session, CancellationToken ct)
    {
        if (session.ReadLast<ImportInput>("normalized-v2") is { } cached) return cached;
        var result = new ImportInput([], [], [], []);
        long total = 0;
        if (session.State.SourceExtension == ".zip")
        {
            using var zip = ZipFile.OpenRead(session.Source);
            if (zip.Entries.Count > 10000) throw new InvalidDataException("Literary.Import.Limit");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                ValidateName(entry.FullName);
                if (!names.Add(entry.FullName.Replace('\\', '/').TrimEnd('/'))
                    || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Literary.Import.UnsafeArchive");
                if (entry.Length > MaxTotalBytes || (total += entry.Length) > MaxTotalBytes) throw new InvalidDataException("Literary.Import.Limit");
                if (entry.FullName.EndsWith('/')) continue;
                var json = entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                using var input = entry.Open();
                var (name, output) = session.BeginRaw();
                long written = 0;
                using (output)
                {
                    var buffer = new byte[65536]; int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        if ((written += read) > entry.Length || written > (json ? MaxJsonBytes : MaxTotalBytes))
                            throw new InvalidDataException("Literary.Import.Limit");
                        // Binary attachments stay in the unchanged original ZIP; do not duplicate them.
                        if (json) await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    output.Flush(true);
                }
                if (written != entry.Length) throw new InvalidDataException("Literary.Import.Corrupt");
                if (json)
                {
                    var artifact = session.Register("source-json", name, "complete");
                    var recognized = await ReadJsonAsync(session.ArtifactPath(artifact), result, ct);
                    result.Inventory.Add(new(entry.FullName, written, recognized ? "conversations" : "unprocessed-json"));
                }
                else
                {
                    File.Delete(Path.Combine(session.Root, name));
                    File.Delete(Path.Combine(session.Root, name + ".pending"));
                    result.Inventory.Add(new(entry.FullName, written, "attachment-not-read"));
                }
            }
        }
        else
        {
            if (new FileInfo(session.Source).Length > MaxJsonBytes) throw new InvalidDataException("Literary.Import.Limit");
            using var input = File.OpenRead(session.Source);
            var raw = session.BeginRaw();
            using (raw.Stream) { await input.CopyToAsync(raw.Stream, ct); raw.Stream.Flush(true); }
            session.Register("source-json", raw.Name, "complete");
            await ReadJsonAsync(session.Source, result, ct);
            result.Inventory.Add(new("source.json", input.Length, "conversations"));
        }
        if (result.Conversations.Count == 0 || result.Units.Count == 0) throw new InvalidDataException("Literary.Import.Unsupported");
        if (result.Conversations.Select(c => c.Id).Distinct().Count() != result.Conversations.Count)
            throw new InvalidDataException("Literary.Import.DuplicateId");
        session.AddJson("normalized-v2", result, session.State.Artifacts.Where(a => a.Step == "source-json").Select(a => a.Id).ToArray());
        session.State.Stage = "select"; session.Save(); return result;
    }
    public static void ValidateName(string name)
    {
        var parts = name.Replace('\\', '/').TrimEnd('/').Split('/');
        if (parts.Length == 0 || parts.Any(p => !LiteraryProjectStore.IsValidProjectName(p)))
            throw new InvalidDataException("Literary.Import.UnsafeArchive");
    }
    private static async Task<bool> ReadJsonAsync(string path, ImportInput output, CancellationToken ct)
    {
        using (var peek = new StreamReader(path))
        {
            int c; do { c = peek.Read(); } while (c >= 0 && char.IsWhiteSpace((char)c));
            if (c != '[') return false;
        }
        await ValidateTokensAsync(path, ct);
        await using var stream = File.OpenRead(path);
        await foreach (var conversation in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, ImportJson.Options, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!conversation.TryGetProperty("mapping", out var mapping) || mapping.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Literary.Import.Unsupported");
            var id = Required(conversation, "id"); var title = String(conversation, "title");
            var startCount = output.Units.Count;
            var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in mapping.EnumerateObject())
                if (!nodes.TryAdd(property.Name, property.Value)) throw new InvalidDataException("Literary.Import.DuplicateId");
            var visited = new HashSet<string>();
            foreach (var key in nodes.Keys)
            {
                var chain = new HashSet<string>(); var current = key;
                while (nodes.TryGetValue(current, out var node) && !visited.Contains(current))
                {
                    if (!chain.Add(current)) throw new InvalidDataException("Literary.Import.CyclicGraph");
                    current = String(node, "parent");
                }
                if (current.Length > 0 && !nodes.ContainsKey(current)) output.Warnings.Add("Missing parent: " + current);
                visited.UnionWith(chain);
            }
            // Export dictionaries have no chronological order. Traverse parents before children,
            // using message time to order siblings, retaining every branch.
            var ordered = new List<KeyValuePair<string, JsonElement>>(); var emitted = new HashSet<string>();
            void Emit(string key)
            {
                var chain = new Stack<string>(); var current = key;
                while (nodes.ContainsKey(current) && emitted.Add(current))
                { chain.Push(current); current = String(nodes[current], "parent"); }
                while (chain.TryPop(out var next)) ordered.Add(new(next, nodes[next]));
            }
            foreach (var pair in nodes.OrderBy(p => p.Value.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? String(m, "inserted_at") : "")) Emit(pair.Key);
            foreach (var pair in ordered)
            {
                var node = pair.Value;
                if (!node.TryGetProperty("message", out var message) || message.ValueKind == JsonValueKind.Null) continue;
                if (!message.TryGetProperty("fragments", out var fragments) || fragments.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Literary.Import.Unsupported");
                var index = 0;
                foreach (var fragment in fragments.EnumerateArray())
                {
                    var type = String(fragment, "type"); var text = String(fragment, "content");
                    var technical = type is "THINK" or "SEARCH" or "TOOL_SEARCH" or "TOOL_OPEN" or "FILE";
                    if (type is not ("THINK" or "SEARCH" or "TOOL_SEARCH" or "TOOL_OPEN" or "FILE" or "REQUEST" or "RESPONSE"))
                        output.Warnings.Add("Unknown fragment type: " + type);
                    var offset = 0;
                    foreach (var part in SplitSource(text))
                    {
                        var unitId = ImportSession.Hash(id + "\0" + pair.Key + "\0" + index + "\0" + offset)[..24];
                        output.Units.Add(new(unitId, id, pair.Key, String(node, "parent"), type, index, offset, part, technical));
                        offset += part.Length;
                    }
                    index++;
                }
            }
            output.Conversations.Add(new(id, title, output.Units.Count - startCount));
        }
        return true;
    }
    public static IEnumerable<string> SplitExact(string text, int limit)
    {
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit));
        for (var start = 0; start < text.Length;)
        {
            var count = Math.Min(limit, text.Length - start);
            if (count < text.Length - start)
            {
                var newline = text.LastIndexOf('\n', start + count - 1, count);
                if (newline >= start) count = newline - start + 1;
                else
                {
                    var space = text.LastIndexOf(' ', start + count - 1, count);
                    if (space >= start + count / 2) count = space - start + 1;
                    if (char.IsHighSurrogate(text[start + count - 1])) count--;
                }
            }
            yield return text.Substring(start, count); start += count;
        }
    }
    private static IEnumerable<string> SplitSource(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            var crlf = text.IndexOf("\r\n\r\n", start, StringComparison.Ordinal);
            if (crlf >= 0 && (end < 0 || crlf < end)) end = crlf + 4;
            else end = end < 0 ? text.Length : end + 2;
            while (end < text.Length && text[end] is '\r' or '\n') end++;
            foreach (var piece in SplitExact(text[start..end], 1800)) yield return piece;
            start = end;
        }
    }
    private static string String(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static string Required(JsonElement value, string key) => String(value, key) is { Length: > 0 } text ? text : throw new InvalidDataException("Literary.Import.Unsupported");
    private static async Task ValidateTokensAsync(string path, CancellationToken ct)
    {
        // Bound incomplete tokens before JsonSerializer buffers any conversation object.
        await using var file = File.OpenRead(path);
        var buffer = new byte[64 * 1024]; var used = 0; var state = new JsonReaderState(new() { MaxDepth = 128 });
        var first = true;
        while (true)
        {
            var read = await file.ReadAsync(buffer.AsMemory(used), ct); var final = read == 0; used += read;
            if (first && used >= 3 && buffer.AsSpan(0, 3).SequenceEqual(new byte[] { 239, 187, 191 }))
            { Buffer.BlockCopy(buffer, 3, buffer, 0, used - 3); used -= 3; }
            first = false;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, used), final, state);
            while (reader.Read()) { }
            var consumed = (int)reader.BytesConsumed; state = reader.CurrentState;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, used - consumed); used -= consumed;
            if (final) break;
            if (used == buffer.Length)
            {
                if (buffer.Length >= 8 * 1024 * 1024) throw new InvalidDataException("Literary.Import.Limit");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }
}
