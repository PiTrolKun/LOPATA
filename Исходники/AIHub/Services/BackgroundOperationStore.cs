using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Keep the last stable checkpoint if a write or validation fails.</summary>
public sealed class BackgroundOperationStore(string path)
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BackgroundOperationState? Load()
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<BackgroundOperationState>(File.ReadAllBytes(path), Options)
            ?? throw new InvalidDataException("Empty background operation checkpoint.");
        Validate(state);
        return state;
    }

    public void Save(BackgroundOperationState state)
    {
        Validate(state);
        if (state.Phase == BackgroundOperationPhase.Completed)
            SaveAtomic(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Results", state.Id + ".json"), state);
        SaveAtomic(path, state);
    }

    public BackgroundOperationState? LoadResult(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid result ID.");
        return new BackgroundOperationStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Results", id + ".json")).Load();
    }

    private static void SaveAtomic(string destination, BackgroundOperationState state)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            Lopata.Updates.SafeUpdatePath.RejectLinks(destination); File.Move(temporary, destination, true);
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
