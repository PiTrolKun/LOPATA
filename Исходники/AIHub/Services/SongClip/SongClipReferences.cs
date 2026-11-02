using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.SongClip;

/// <summary>User-authored meaning of one owned image; never an inferred visual caption.</summary>
internal sealed record SongClipReference(string File, string Kind, string Description);

internal static class SongClipReferences
{
    internal const int MaximumCount = 9, MaximumDescription = 2_000;
    internal const string Invalid = "SongClip.ReferenceRoleRequired";

    internal static SongClipReference[] For(SongClipProject project) => Effective(project.Reference, project.References);
    internal static SongClipReference[] For(SongClipAttempt attempt) => Effective(attempt.Reference, attempt.References);
    private static SongClipReference[] Effective(string legacy, SongClipReference[]? references) => references
        ?? (string.IsNullOrEmpty(legacy) ? [] : [new(legacy, "", "")]);
    internal static bool Same(SongClipReference[]? left, SongClipReference[]? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right);

    internal static void ValidateDraft(string legacy, SongClipReference[]? references)
    {
        if (references is null) return;
        if (references.Length > MaximumCount || references.Any(reference => reference is null
            || string.IsNullOrWhiteSpace(reference.File) || reference.File.Length > 512
            || reference.Kind is not ("" or "performer" or "world" or "object")
            || reference.Description is null || reference.Description.Length > MaximumDescription
            || reference.Description.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            || references.Select(reference => reference.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Length
            || legacy != references.FirstOrDefault()?.File && !(references.Length == 0 && legacy.Length == 0))
            throw new InvalidDataException(Invalid);
    }

    internal static void ValidateNewAttempt(SongClipAttempt attempt)
    {
        // Old attempts must retain their original inputs and workflow.
        if (attempt.References is null) return;
        if (attempt.PlanningVersion != SongClipVisualProtocol.Version) throw new InvalidDataException(Invalid);
        ValidateDraft(attempt.Reference, attempt.References);
        if (attempt.References.Any(reference => reference.Kind.Length == 0 || string.IsNullOrWhiteSpace(reference.Description)))
            throw new InvalidDataException(Invalid);
    }

    internal static async Task<string> HashAsync(SongClipAttempt attempt, SongClipStore store, CancellationToken token)
    {
        if (attempt.References is null) return attempt.Reference.Length == 0 ? ""
            : await FileHashAsync(store.Resolve(attempt.ProjectId, attempt.Reference), token);
        ValidateNewAttempt(attempt);
        var records = new List<object>();
        foreach (var reference in attempt.References)
            records.Add(new { reference.File, reference.Kind, reference.Description,
                sha256 = await FileHashAsync(store.Resolve(attempt.ProjectId, reference.File), token) });
        return records.Count == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records)));
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var input = System.IO.File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }
}
