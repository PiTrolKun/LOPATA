using System.IO;

namespace AIHub.Services;

internal static class MusicStemRecovery
{
    internal static async Task<MusicGenerationVariant> RestoreAsync(MusicGenerationVariant variant,
        Action<MusicGenerationVariant> save, Action<string>? log, CancellationToken token)
    {
        if (variant.Stems?.Receipt is not { } receipt) return variant;
        try {
            await MusicOutputPublisher.MatchHashAsync(receipt.VoicePcm, receipt.VoiceHash, token);
            await MusicOutputPublisher.MatchHashAsync(receipt.MusicPcm, receipt.MusicHash, token);
            return variant;
        }
        catch (IOException) when (!variant.Completed && !variant.Stems.Published) {
            // Only a reproducible private stage may be rebuilt; never mask damage to its original source.
            await MusicOutputPublisher.MatchHashAsync(variant.AudioFile!, variant.AudioHash!, token);
            var files = new[] { (variant.ResultPath, variant.ResultHash), (variant.AdditionalPath, variant.AdditionalHash),
                (MusicStemBundle.MusicPath(variant), variant.Stems.MusicHash),
                (variant.AdditionalPath is null ? null : MusicStemBundle.MusicPath(variant, true), variant.Stems.AdditionalMusicHash) }
                .Where(x => x.Item1 is not null && File.Exists(x.Item1)).ToArray();
            foreach (var (path, hash) in files) {
                if (hash is null) throw new IOException("An unrelated file occupies a partial song bundle.");
                await MusicOutputPublisher.MatchHashAsync(path!, hash, token);
            }
            var quarantine = Path.Combine(Path.GetDirectoryName(variant.AudioFile!)!, "stems-" + Guid.NewGuid().ToString("N"), "previous-public");
            Directory.CreateDirectory(quarantine);
            foreach (var (path, _) in files) File.Move(path!, Path.Combine(quarantine, Path.GetFileName(path!)), false);
            variant = variant with { ResultHash = null, AdditionalHash = null, Stems = new() };
            save(variant); log?.Invoke("[Stems] Restoring an incomplete private stage from verified original PCM.");
            return variant;
        }
    }
}
