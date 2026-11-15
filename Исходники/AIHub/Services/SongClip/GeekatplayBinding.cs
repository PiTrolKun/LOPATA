using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal static class GeekatplayBinding
{
    private static readonly PinnedTreeFile[] PriorMedia = [H3MontageBinding.PriorMedia,
        new("song_clip_geek.py", 17996, "19C3D4306E49040D4CDB260248284EF5A2D469DA7C651F805C2123F640BA074C"),
        new("song_clip_geek_native.py", 9773, "9D05867EA43978783D2390852E4B06539CA4788B55828541B635955E53EA303F")];
    private static readonly PinnedTreeFile[] PriorAdapters = [H3MontageBinding.PriorMedia,
        new("song_clip_geek.py", 16989, "77C44AF452B7B7F60B4F8BED14703AD87735A984874D3C7F7179318FCA5023F1"),
        new("song_clip_geek_native.py", 9550, "EFBB219A256A134256672867CE8B01591066714535CF6CC355C244F8DAEC2C91")];

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
        var song = await H3MontageBinding.HashAsync(store.Resolve(attempt.ProjectId, attempt.Song), token);
        var references = await SongClipReferences.HashAsync(attempt, store, token);
        var adapters = AdapterFiles();
        JsonElement Identity(PinnedTreeFile[] scripts) => JsonSerializer.SerializeToElement(new { schema = 1, attempt,
            song, references, scripts,
            // Verification receipts change after acquisition; only immutable content belongs here.
            pins = cards.Select(c => new { c.ModelArtifactId, c.Revision,
                files = c.Files.Select(f => new { f.RelativePath, f.SizeBytes, f.Sha256, f.IsRequired }) }) });
        var identity = Identity(adapters);
        var path = Path.Combine(job, "geek-input-binding.json");
        if (File.Exists(path)) {
            using var old = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            if (!JsonElement.DeepEquals(old.RootElement, identity)
                && !JsonElement.DeepEquals(old.RootElement, Identity(H3MontageBinding.WithAdapters(adapters, PriorMedia)))
                && !JsonElement.DeepEquals(old.RootElement, Identity(H3MontageBinding.WithAdapters(adapters, PriorAdapters))))
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
        }
        else {
            if (new[] { "geek-analysis.json", "request.json", "geek-state.json", "worker-result.json", "clip.mp4" }
                .Any(name => File.Exists(Path.Combine(job, name)))) throw new InvalidDataException("SongClip.VisualPlanMismatch");
            token.ThrowIfCancellationRequested(); SongClipStore.Atomic(path, identity);
        }
    }
}
