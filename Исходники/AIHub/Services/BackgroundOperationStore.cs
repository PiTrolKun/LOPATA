using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Keep the last stable checkpoint if a write or validation fails.</summary>
public sealed class BackgroundOperationStore(string path, bool archiveResults = true)
{
    internal DateTime LastWriteUtc => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    internal BackgroundOperationStore Operation(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid operation ID.");
        return new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Jobs", id + ".json"), false);
    }
    internal FileStream? TryOwnOperation()
    {
        var lease = Path.GetFullPath(path) + ".lease";
        Lopata.Updates.SafeUpdatePath.RejectLinks(lease);
        Directory.CreateDirectory(Path.GetDirectoryName(lease)!);
        try
        {
            return new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }
    internal IReadOnlyList<BackgroundOperationState> LoadOperations()
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Jobs");
        Lopata.Updates.SafeUpdatePath.RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var result = new List<BackgroundOperationState>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (new BackgroundOperationStore(file, false).Load() is not { } state) continue;
            if (Path.GetFileNameWithoutExtension(file) != state.Id) throw new InvalidDataException("Background job identity mismatch.");
            result.Add(state);
        }
        return result;
    }
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BackgroundOperationState? Load()
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var state = JsonSerializer.Deserialize<BackgroundOperationState>(file, Options)
                ?? throw new InvalidDataException("Empty background operation checkpoint.");
            Validate(state);
            return state;
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    public void Save(BackgroundOperationState state)
    {
        Validate(state);
        // Private notifications survive in memory; inheritance by a later public task must not archive them.
        var publicNotices = state.Notices.Where(n => !n.Private).ToArray();
        state = state with { Notices = publicNotices,
            Notice = state.Notice?.Private == true ? publicNotices.LastOrDefault() : state.Notice,
            NeedsAttention = publicNotices.Length > 0 || state.NeedsAttention && state.Notice?.Private != true };
        if (state.Private && state.Phase is BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled)
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            // Keep unread PUBLIC results across restart without persisting the private run's fact or path.
            foreach (var notice in publicNotices.Reverse())
            {
                var previous = LoadResult(notice.Id);
                if (previous is null || previous.Private) continue;
                SaveAtomic(path, previous with { Notices = publicNotices, Notice = notice, NeedsAttention = true });
                return;
            }
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (archiveResults && state.Phase == BackgroundOperationPhase.Completed)
            SaveAtomic(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Results", state.Id + ".json"), state);
        SaveAtomic(path, state);
    }

    public BackgroundOperationState? LoadResult(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid result ID.");
        return new BackgroundOperationStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Results", id + ".json")).Load();
    }

    internal static void SaveAtomic<T>(string destination, T state, bool tolerateBriefFileLock = false)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            for (var attempt = 0; ; attempt++)
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
                try { File.Move(temporary, destination, overwrite: true); break; }
                catch (Exception error) when (tolerateBriefFileLock && attempt < 3 &&
                    error is IOException or UnauthorizedAccessException &&
                    (error.HResult & 0xffff) is 5 or 32 or 33 && File.Exists(destination))
                {
                    // A reader without delete sharing can briefly block replacement on Windows.
                    Thread.Sleep(25 << attempt);
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(BackgroundOperationState state)
    {
        if (state.Schema != 1 || !Guid.TryParseExact(state.Id, "N", out _) || string.IsNullOrWhiteSpace(state.Kind)
            || string.IsNullOrWhiteSpace(state.Title) || state.Input.ValueKind == JsonValueKind.Undefined
            || !Enum.IsDefined(state.Phase) || !double.IsFinite(state.ElapsedSeconds) || state.ElapsedSeconds < 0)
            throw new InvalidDataException("Invalid background operation checkpoint; original retained.");
        if (state.Notices is null || state.Notices.Any(n => n is null || !Guid.TryParseExact(n.Id, "N", out _)
            || string.IsNullOrWhiteSpace(n.Kind) || string.IsNullOrWhiteSpace(n.Title))
            || state.Notices.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != state.Notices.Count)
            throw new InvalidDataException("Invalid background result notice; original retained.");
    }
}
