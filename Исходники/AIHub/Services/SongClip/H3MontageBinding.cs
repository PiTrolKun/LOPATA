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
    private static readonly (string File, string Resource)[] DenoScripts = [
        (SongClipDeno.Worker, "AIHub.SongClip.DenoWorker"), (SongClipDeno.FinishWorker, "AIHub.SongClip.DenoFinishWorker")];
    internal static readonly PinnedTreeFile PriorMedia = new("song_clip_montage_media.py", 3741,
        "4132875023B58DDEB1EE5A8DF1775129964BEBBEAF193F92E6F2225EDEAE815A");
    internal const string PriorWorkerSha = "925C49C1E5DBD34DC4C77B642EBF1E8181E5DAD8DFC85B651D63D449B0987F1C";
    internal const string PreStorageWorkerSha = "2302A708198E9D7741FF7236647C3E17D2E6FEA2434FBDB89B84615DB25AF63A";
    private static readonly PinnedTreeFile[] PreStorageAdapters = [
        new("song_clip_montage.py", 36064, PreStorageWorkerSha),
        new("song_clip_motion.py", 40370, "278FB74F1F0CE80660B17F7443C685DB1580AC31FA5A92B876F962021D47887D")];
    private static readonly PinnedTreeFile PriorDenoFinisher = new(SongClipDeno.FinishWorker, 6025,
        "14230B421A173E166F5D146EFBF16A98EA4DB7A8FD8921CDE5036448E3B6CAB5");
    private static readonly PinnedTreeFile WorkshopDenoFinisher = new(SongClipDeno.FinishWorker, 6332,
        "394F893A5E0325347F561E1771A2C192093E0D844974EABE54B0514CC6FAC9BC");
    private static readonly PinnedTreeFile[] PriorAdapters = [
        new("song_clip_montage.py", 33301, PriorWorkerSha),
        new("song_clip_montage_state.py", 9410, "69E1BD20BD7868DA3CF0AA852D054B52F6EC534E402D227A15079CC1CAB7D0A1"),
        PriorMedia,
        new("song_clip_motion.py", 39928, "14BD9498C755B2AEE2CE84EA2047BFA210F21B5D0D54FF74B8C0BC385F443F28")];
    internal static PinnedTreeFile[] AdapterFiles(bool deno = false, bool accelerated = false)
    {
        var scripts = deno ? Scripts.Concat(DenoScripts) : Scripts;
        if (deno && accelerated)
            scripts = scripts.Append((SongClipDeno.AcceleratedWorker, "AIHub.SongClip.DenoSolWorker"));
        return scripts.Select(item => {
            using var source = typeof(H3MontageBinding).Assembly.GetManifestResourceStream(item.Resource)
                ?? throw new InvalidDataException("Missing H3 montage adapter.");
            return new PinnedTreeFile(item.File, source.Length, Convert.ToHexString(SHA256.HashData(source)));
        }).ToArray();
    }

    internal static async Task EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        var song = await HashAsync(store.Resolve(attempt.ProjectId, attempt.Song), token);
        var references = await SongClipReferences.HashAsync(attempt, store, token);
        var deno = SongClipModes.IsDeno(attempt);
        var adapters = AdapterFiles(deno, SongClipDeno.Accelerated(attempt));
        Binding Identity(PinnedTreeFile[] scripts) => new(1, deno ? SongClipDeno.Mode : SongClipModes.Montage,
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
            PinnedTreeFile[][] compatible = deno
                ? [PreStorageAdapters, [.. PreStorageAdapters, PriorDenoFinisher], [.. PreStorageAdapters, WorkshopDenoFinisher]]
                : [PreStorageAdapters, PriorAdapters];
            if (recorded != expected && !compatible.Any(previous => recorded == Identity(WithAdapters(adapters, previous))))
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
