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
    internal static readonly PinnedTreeFile PriorMedia = new("song_clip_montage_media.py", 3741,
        "4132875023B58DDEB1EE5A8DF1775129964BEBBEAF193F92E6F2225EDEAE815A");
    internal const string PriorWorkerSha = "925C49C1E5DBD34DC4C77B642EBF1E8181E5DAD8DFC85B651D63D449B0987F1C";
    private static readonly PinnedTreeFile[] PriorAdapters = [
        new("song_clip_montage.py", 33301, PriorWorkerSha),
        new("song_clip_montage_state.py", 9410, "69E1BD20BD7868DA3CF0AA852D054B52F6EC534E402D227A15079CC1CAB7D0A1"),
        PriorMedia,
        new("song_clip_motion.py", 39928, "14BD9498C755B2AEE2CE84EA2047BFA210F21B5D0D54FF74B8C0BC385F443F28")];
    internal static PinnedTreeFile[] AdapterFiles() => Scripts.Select(item => {
        using var source = typeof(H3MontageBinding).Assembly.GetManifestResourceStream(item.Resource)
            ?? throw new InvalidDataException("Missing H3 montage adapter.");
        return new PinnedTreeFile(item.File, source.Length, Convert.ToHexString(SHA256.HashData(source)));
    }).ToArray();

    internal static async Task EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        var song = await HashAsync(store.Resolve(attempt.ProjectId, attempt.Song), token);
        var references = await SongClipReferences.HashAsync(attempt, store, token);
        var adapters = AdapterFiles();
        Binding Identity(PinnedTreeFile[] scripts) => new(1, SongClipModes.Montage,
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            attempt, song, references, adapters = scripts,
            pins = cards.OrderBy(c => c.ModelArtifactId, StringComparer.Ordinal).Select(c => new {
                c.ModelArtifactId, c.Revision, files = c.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                    .Select(f => new { f.RelativePath, f.SizeBytes, f.Sha256 }) })
        }))));
        var expected = Identity(adapters);
        var path = Path.Combine(job, "montage-input-binding.json");
        if (File.Exists(path)) {
            var recorded = JsonSerializer.Deserialize<Binding>(await File.ReadAllTextAsync(path, token));
            if (recorded != expected && recorded != Identity(WithAdapters(adapters, PriorAdapters)))
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            return;
        }
        if (new[] { "montage-analysis.json", "request.json", "motion-state.json", "montage-state.json", "worker-result.json", "clip.mp4" }
            .Any(name => File.Exists(Path.Combine(job, name)))) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        token.ThrowIfCancellationRequested(); SongClipStore.Atomic(path, expected);
    }
    internal static PinnedTreeFile[] WithAdapters(PinnedTreeFile[] current, PinnedTreeFile[] prior) =>
        current.Select(file => prior.FirstOrDefault(item => item.Path == file.Path) ?? file).ToArray();
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }
}
