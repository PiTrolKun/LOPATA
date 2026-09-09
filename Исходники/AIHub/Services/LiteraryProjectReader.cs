using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiterarySource(string Id, string Number, string Title, string FileName, bool Finished);

/// <summary>Captured on the UI thread; all later reads use this immutable request identity.</summary>
public sealed record LiteraryEditorSnapshot(string ProjectId, string Directory, string Transaction,
    string ActiveId, string Text, bool Unsaved, IReadOnlyList<LiterarySource> Sources)
{
    public LiterarySource Active => Sources.Single(p => p.Id == ActiveId);
    public string Revision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        ProjectId + "\n" + Directory + "\n" + ActiveId + "\n" + Active.FileName + "\n" + Text)));
    public static LiteraryEditorSnapshot Capture(string projectId, string directory, LiteraryChapterIndex index, string text, bool unsaved) =>
        new(projectId, Path.GetFullPath(directory), index.Transaction, index.ActiveId, text, unsaved,
            Array.AsReadOnly(index.Parts.OrderBy(p => p.Chapter).ThenBy(p => p.Part).Select(p =>
                new LiterarySource(p.Id, $"{p.Chapter:000}" + (p.Part == 1 ? "" : "." + p.Part), p.Title, p.FileName, p.Finished)).ToArray()));
}

public sealed record LiteraryReadAction(string Action, string Number = "", int Offset = 0, string Query = "");
public sealed record LiteraryReadResult(string Key, string Json);
public sealed class LiterarySourceException(string message, Exception inner) : IOException(message, inner);

/// <summary>Read-only capabilities. Never opens/migrates the store or accepts model-supplied paths.</summary>
public sealed class LiteraryProjectReader(LiteraryEditorSnapshot snapshot)
{
    public const int PageSize = 12, FragmentCharacters = 2400, SearchPageSize = 32;
    private const int MaxFileBytes = 1024 * 1024;
    public LiteraryEditorSnapshot Snapshot { get; } = snapshot;
    private static string Json(object value) => JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public object Anchor => new { projectId = Snapshot.ProjectId, projectDirectory = Snapshot.Directory,
        activeNumber = Snapshot.Active.Number, file = Snapshot.Active.FileName, revision = Snapshot.Revision,
        state = "working_draft", unsaved = Snapshot.Unsaved, fullTextIncluded = true };

    public LiteraryReadResult Execute(LiteraryReadAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (action.Offset < 0) throw new InvalidDataException("Negative source offset.");
        try { CheckIndex(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or KeyNotFoundException or InvalidOperationException)
        { throw new LiterarySourceException("The project catalog is unavailable or changed. Reopen the project before reading.", ex); }
        var key = Json(action);
        try
        {
            return new(key, action.Action switch
            {
                "list" => List(action.Offset),
                "read" => Read(action.Number, action.Offset),
                "search" => Search(action.Query, action.Offset, token),
                _ => Json(new { error = "Unsupported read action." })
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return new(key, Json(new { error = ex.Message, number = action.Number, query = action.Query })); }
    }

    public string List(int offset) => Json(new { kind = "catalog", total = Snapshot.Sources.Count, offset,
        nextOffset = offset + PageSize < Snapshot.Sources.Count ? (int?)(offset + PageSize) : null,
        parts = Snapshot.Sources.Skip(offset).Take(PageSize).Select(p => new { number = p.Number, title = p.Title,
            state = p.Id == Snapshot.ActiveId ? "working_draft" : "history", chapterFinished = p.Finished }) });

    private string Read(string number, int offset)
    {
        var source = Snapshot.Sources.SingleOrDefault(p => p.Number == number);
        if (source is null) return Json(new { error = "Unknown registered part number.", number });
        var text = ReadText(source);
        if (offset > text.Length) return Json(new { error = "Offset exceeds text length.", number, length = text.Length });
        var end = Math.Min(text.Length, offset + FragmentCharacters);
        if (end < text.Length && end > offset && char.IsHighSurrogate(text[end - 1])) end--;
        return Json(new { kind = "fragment", number, source.FileName,
            state = source.Id == Snapshot.ActiveId ? "working_draft" : "history",
            revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            offset, nextOffset = end < text.Length ? (int?)end : null, totalCharacters = text.Length, text = text[offset..end] });
    }

    private string Search(string query, int offset, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 120) return Json(new { error = "Search query must contain 1..120 characters." });
        var matches = new List<object>(); var errors = new List<object>(); var cursor = Math.Min(offset, Snapshot.Sources.Count);
        var prefix = SearchPrefix(query);
        for (; cursor < Snapshot.Sources.Count && cursor - offset < SearchPageSize && matches.Count < 8; cursor++)
        {
            token.ThrowIfCancellationRequested();
            var source = Snapshot.Sources[cursor];
            try
            {
                var text = ReadText(source); var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                var approximate = false;
                if (at < 0 && prefix != query) { at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase); approximate = at >= 0; }
                if (at < 0 && !source.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var start = Math.Max(0, at - 60);
                matches.Add(new { number = source.Number, title = source.Title, offset = start,
                    state = source.Id == Snapshot.ActiveId ? "working_draft" : "history",
                    approximate, excerpt = text.Substring(start, Math.Min(160, text.Length - start)) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { errors.Add(new { number = source.Number, error = ex.Message }); }
        }
        return Json(new { kind = "search", query, offset, searchedParts = Math.Max(0, cursor - offset),
            nextOffset = cursor < Snapshot.Sources.Count ? (int?)cursor : null, matches, errors });
    }

    private static string SearchPrefix(string query)
    {
        // Small transparent fallback for Russian inflections, not semantic/RAG search.
        if (query.Length < 6 || !query.All(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё')) return query;
        foreach (var ending in new[] { "ого", "ему", "ами", "ями", "ом", "ем", "ов", "ев", "ей", "а", "я", "ь", "и", "ы", "е", "у", "ю" })
            if (query.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) return query[..^ending.Length];
        return query;
    }

    private string ReadText(LiterarySource source)
    {
        // Even when the active part has been autosaved, the editor snapshot is authoritative.
        if (source.Id == Snapshot.ActiveId) return Snapshot.Text;
        CheckIndex();
        if (Path.GetFileName(source.FileName) != source.FileName || !source.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid registered source name.");
        return ReadBounded(Path.Combine(Snapshot.Directory, "chapters", source.FileName));
    }

    private void CheckIndex()
    {
        var path = Path.Combine(Snapshot.Directory, "chapters", "index.json");
        using var state = JsonDocument.Parse(ReadBounded(path));
        if (state.RootElement.GetProperty("Transaction").GetString() != Snapshot.Transaction
            || state.RootElement.GetProperty("ActiveId").GetString() != Snapshot.ActiveId)
            throw new IOException("Project structure changed after the request. Send a new request.");
        var parts = state.RootElement.GetProperty("Parts").EnumerateArray().ToArray();
        if (parts.Length != Snapshot.Sources.Count || Snapshot.Sources.Any(source => !parts.Any(p =>
                p.GetProperty("Id").GetString() == source.Id && p.GetProperty("FileName").GetString() == source.FileName)))
            throw new IOException("Project source catalog changed. Reopen the project.");
    }

    private static string ReadBounded(string path)
    {
        // Reject junctions/symlinks, including parent directories; never follow them outside the project.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked project sources are not available to the model.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxFileBytes) throw new IOException("Source exceeds the reading size limit.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var skip = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return LiteraryChapterFiles.Utf8.GetString(bytes, skip, bytes.Length - skip);
    }
}
