using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Song-bearing sessions only. Atomic working drafts do not add history steps.</summary>
public sealed class MusicPoetrySessions(string directory)
{
    public static MusicPoetrySessions Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "PoetrySessions"));
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public sealed record Entry(string Id, string Title, DateTimeOffset UpdatedAt, bool Damaged);
    private string PathFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(directory, id + ".json")
        : throw new InvalidDataException("Invalid poetry session identity.");
    public IReadOnlyList<Entry> List()
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try { var s = Load(id); return new Entry(id, Title(s), s.UpdatedAt, false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { return new Entry(id, id, File.GetLastWriteTimeUtc(path), true); }
        }).OrderByDescending(e => e.UpdatedAt).ToArray();
    }
    public MusicPoetrySession Load(string id)
    {
        var path = PathFor(id);
        try { return Read(path, id); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            if (!File.Exists(path + ".bak")) throw;
            return Read(path + ".bak", id); // damaged primary remains untouched until a successful save
        }
    }
    public void Save(MusicPoetrySession session)
    {
        session.RememberText();
        if (!session.Persistent) return;
        Validate(session); Directory.CreateDirectory(directory);
        var path = PathFor(session.Id); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, Options);
        if (bytes.Length > 64 * 1024 * 1024) throw new IOException("Poetry session exceeds the storage limit.");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path))
            {
                // Never replace a known-good backup with a damaged primary.
                try { Read(path, session.Id); File.Replace(temporary, path, path + ".bak"); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                { File.Copy(path, path + ".damaged-" + Guid.NewGuid().ToString("N")); File.Move(temporary, path, true); }
            }
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete(string id)
    {
        var path = Path.GetFullPath(PathFor(id)); var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid session directory.");
        foreach (var owned in new[] { path, path + ".bak" }) if (File.Exists(owned)) File.Delete(owned);
    }
    private static MusicPoetrySession Read(string path, string id)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Poetry session is too large.");
        var s = JsonSerializer.Deserialize<MusicPoetrySession>(File.ReadAllBytes(path), Options)
            ?? throw new InvalidDataException("Empty poetry session.");
        Validate(s);
        if (s.Id != id || !s.Persistent) throw new InvalidDataException("Poetry session identity mismatch.");
        foreach (var thought in s.Thoughts.Where(t => t.State == MusicPoetryThought.Active))
        { thought.State = MusicPoetryThought.Interrupted; thought.Expanded = false; }
        foreach (var exchange in s.SearchExchanges.Where(e => e.State is "preparing" or "ready" or "sent" or "received"))
            exchange.State = "interrupted";
        return s;
    }
    internal static void Validate(MusicPoetrySession s)
    {
        if (s.LibraryUses is null || s.LibraryUses.Count > 200 || s.LibraryUses.Any(use => use is null
            || use.TurnId is null || use.Examples is null || use.Examples.Length > 8
            || use.Examples.Any(e => e is null || e.Text is null || e.Text.Length > 16000 || e.Title is null || e.Section is null)))
            throw new InvalidDataException("Invalid personal library context history.");
        if (s.Schema != 1 || !Guid.TryParseExact(s.Id, "N", out _) || s.Lyrics is null || s.Parameters is null
            || s.Parameters.Text is null || !MusicPoetryProtocol.Variations.Contains(s.Parameters.Variation)
            || s.Draft is null || s.ModelPath is null || s.ModelName is null || s.PartialReply is null || s.IncompleteReplies is null
            || s.IncompleteReplies.Any(p => p is null || p.Text is null)
            || s.ReplyAttempts is null || s.ReplyAttempts.Any(a => a is null || a.TurnId is null || a.Raw is null
                || a.Outcome is null || a.Attempt is < 1 or > 2 || a.Changes is null
                || a.Changes.Any(c => c is not ("song" or "parameters" or "variation")))
            || s.LyricsSteps is null || s.ParameterSteps is null || s.Messages is null || s.Thoughts is null
            || s.Thoughts.Any(t => t is null || !Guid.TryParseExact(t.Id, "N", out _) || t.Text is null || t.TurnId is null
                || t.Attempt is < 1 or > 2 || t.AnchorMessage < 0 || t.AnchorMessage >= (s.Messages?.Count ?? 0)
                || s.Messages![t.AnchorMessage]?.TurnId != t.TurnId
                || t.State is not (MusicPoetryThought.Active or MusicPoetryThought.Completed or MusicPoetryThought.Interrupted))
            || s.Thoughts.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != s.Thoughts.Count
            || s.LyricsSteps.Any(t => t is null) || s.ParameterSteps.Any(p => p is null || p.Text is null || !MusicPoetryProtocol.Variations.Contains(p.Variation))
            || s.LyricsCursor < -1 || s.LyricsCursor >= s.LyricsSteps.Count || s.ParameterCursor < -1 || s.ParameterCursor >= s.ParameterSteps.Count
            || s.Messages.Any(m => m is null || m.Role is not ("user" or "assistant" or "notice") || m.Text is null || m.TurnId is null))
            throw new InvalidDataException("Invalid poetry session.");
        if (s.DeepSeekChatUrl is null || s.AliceChatUrl is null || s.SearchExchanges is null || s.SearchExchanges.Any(e => e is null || !MusicCloudProviders.IsKnown(e.ProviderId) || e.TurnId is null
            || e.Question is null || e.Answer is null || e.Links is null || e.Links.Any(link => link is null)
            || e.State is not ("preparing" or "ready" or "sent" or "received" or "completed" or "interrupted" or "error"))
            || s.SearchExchanges.Select(e => e.TurnId).Distinct(StringComparer.Ordinal).Count() != s.SearchExchanges.Count)
            throw new InvalidDataException("Invalid poetry search exchange.");
    }
    public static string Title(MusicPoetrySession s) => (s.LyricsSteps.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? s.Lyrics)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => !t.StartsWith('['))?.Trim() is { Length: > 0 } line
        ? line[..Math.Min(70, line.Length)] : s.CreatedAt.ToLocalTime().ToString("g");
}
