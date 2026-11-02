using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

/// <summary>No connected checkpoint or output may be adopted into a montage attempt.</summary>
internal static class H3MontageBinding
{
    internal static readonly (string File, string Resource)[] Scripts = [
        ("song_clip_montage.py", "AIHub.SongClip.MontageWorker"),
        ("song_clip_montage_state.py", "AIHub.SongClip.MontageState"),
        ("song_clip_montage_media.py", "AIHub.SongClip.MontageMedia"),
        ("song_clip_motion.py", "AIHub.SongClip.MotionWorker"),
        ("song_clip_motion_state.py", "AIHub.SongClip.MotionState"), ("h3_memory.py", "AIHub.H3Memory")];
    private sealed record Binding(int Schema, string Mode, string Hash);
    internal static PinnedTreeFile[] AdapterFiles() => Scripts.Select(item => {
        using var source = typeof(H3MontageBinding).Assembly.GetManifestResourceStream(item.Resource)
            ?? throw new InvalidDataException("Missing H3 montage adapter.");
        return new PinnedTreeFile(item.File, source.Length, Convert.ToHexString(SHA256.HashData(source)));
    }).ToArray();

    internal static async Task EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            attempt, song = await HashAsync(store.Resolve(attempt.ProjectId, attempt.Song), token),
            references = await SongClipReferences.HashAsync(attempt, store, token), adapters = AdapterFiles(),
            pins = cards.OrderBy(c => c.ModelArtifactId, StringComparer.Ordinal).Select(c => new {
                c.ModelArtifactId, c.Revision, files = c.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                    .Select(f => new { f.RelativePath, f.SizeBytes, f.Sha256 }) })
        })));
        var expected = new Binding(1, SongClipModes.Montage, hash);
        var path = Path.Combine(job, "montage-input-binding.json");
        if (File.Exists(path)) {
            if (JsonSerializer.Deserialize<Binding>(await File.ReadAllTextAsync(path, token)) != expected)
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            return;
        }
        if (new[] { "montage-analysis.json", "request.json", "motion-state.json", "montage-state.json", "worker-result.json", "clip.mp4" }
            .Any(name => File.Exists(Path.Combine(job, name)))) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        token.ThrowIfCancellationRequested(); SongClipStore.Atomic(path, expected);
    }
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }
}
