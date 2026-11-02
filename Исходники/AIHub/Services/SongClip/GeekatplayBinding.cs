using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal static class GeekatplayBinding
{
    internal static readonly (string File, string Resource)[] Scripts = [
        ("song_clip_geek.py", "AIHub.SongClip.GeekWorker"), ("song_clip_geek_native.py", "AIHub.SongClip.GeekNative"),
        ("song_clip_geek_state.py", "AIHub.SongClip.GeekState"), ("song_clip_motion_state.py", "AIHub.SongClip.MotionState"),
        ("song_clip_montage_media.py", "AIHub.SongClip.MontageMedia"), ("h3_memory.py", "AIHub.H3Memory")];
    internal static PinnedTreeFile[] AdapterFiles() => Scripts.Select(item => {
        using var stream = typeof(GeekatplayBinding).Assembly.GetManifestResourceStream(item.Resource)
            ?? throw new InvalidDataException("Missing Geekatplay adapter resource.");
        return new PinnedTreeFile(item.File, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }).ToArray();
    internal static async Task EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        var identity = JsonSerializer.SerializeToElement(new { schema = 1, attempt,
            song = await H3MontageBinding.HashAsync(store.Resolve(attempt.ProjectId, attempt.Song), token),
            references = await SongClipReferences.HashAsync(attempt, store, token), scripts = AdapterFiles(),
            // Verification receipts change after acquisition; only immutable content belongs here.
            pins = cards.Select(c => new { c.ModelArtifactId, c.Revision,
                files = c.Files.Select(f => new { f.RelativePath, f.SizeBytes, f.Sha256, f.IsRequired }) }) });
        var path = Path.Combine(job, "geek-input-binding.json");
        if (File.Exists(path)) {
            using var old = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            if (!JsonElement.DeepEquals(old.RootElement, identity)) throw new InvalidDataException("SongClip.VisualPlanMismatch");
        }
        else {
            if (new[] { "geek-analysis.json", "request.json", "geek-state.json", "worker-result.json", "clip.mp4" }
                .Any(name => File.Exists(Path.Combine(job, name)))) throw new InvalidDataException("SongClip.VisualPlanMismatch");
            token.ThrowIfCancellationRequested(); SongClipStore.Atomic(path, identity);
        }
    }
}
