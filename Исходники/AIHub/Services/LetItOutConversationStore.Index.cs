using System.IO;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    internal string MemoryIndexPath(string id)
    {
        Id(id); return Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Memory", id, "index.md");
    }
    internal static string PairHeading(LetItOutMemoryPair pair) => pair.Heading ??
        "USER: " + string.Join(' ', pair.User.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TruncateMemory(120);

    public string ReadMemoryIndex(string id, int fromPair = 0)
    {
        Id(id); if (Load(id) is null) throw new InvalidDataException("Conversation was removed.");
        var pairs = MemoryPairs(id); var start = fromPair <= 0 ? Math.Max(1, pairs.Count - 23) : fromPair;
        if (start > pairs.Count + 1) throw new InvalidDataException("Invalid memory page.");
        var page = pairs.Where(p => p.Number >= start).Take(24).ToArray();
        var snapshot = MemorySnapshot(id);
        return $"INDEX {id}; pairs={pairs.Count}; page={start}..{page.LastOrDefault()?.Number ?? start}; older pages available by number.\n"
            + (snapshot is null ? "" : "SUMMARY (automatic): " + snapshot.Short + "\n")
            + string.Join('\n', page.Select(p => $"[{p.Number}] {PairHeading(p)}" + AttachmentIndexLine(id, p)));
    }

    internal void WriteMemoryIndex(string id)
    {
        var conversation = Load(id); if (conversation is null) return;
        var path = MemoryIndexPath(id); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var snapshot = MemorySnapshot(id);
        var text = "# LOPATA · let_it_out · index\nAutomatic summaries; original pairs are authoritative.\n"
            + (snapshot is null ? "" : snapshot.Short + "\n\n")
            + string.Join('\n', MemoryPairs(id).Select(p => $"[{p.Number}] {PairHeading(p)}" + AttachmentIndexLine(id, p)));
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteMemoryIndex(string id)
    {
        var path = MemoryIndexPath(id); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (File.Exists(path)) File.Delete(path);
    }
    private static string AttachmentIndexLine(string id, LetItOutMemoryPair p) => string.Concat((p.Attachments ?? [])
        .Select(a => $"\n  attachment://{id}/{a.Id} · {a.Kind} · {a.Name.Replace('\n', ' ')} · {a.Preview.TruncateMemory(100)}"));
}

internal static class LetItOutMemoryText
{
    internal static string TruncateMemory(this string text, int count)
    {
        var characters = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        return characters.Length <= count ? text : text[..characters[count]] + "…";
    }
}
