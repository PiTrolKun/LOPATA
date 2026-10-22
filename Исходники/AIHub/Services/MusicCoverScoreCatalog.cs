using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class MusicCoverScoreCatalog
{
    public const string LicenseId = "music.cover-score";
    public const string Revision = "e630f2b8f6aedf8254c3186438633c1b5eeaf80a";
    public static IReadOnlyList<ComponentCatalogEntry> Entries { get; } = [
        Entry("music.cover-sheetsage", "SheetSage2-Q8_0.gguf", 105629856, "95d8fa8761753b4833782a52b68d5c0afa3bd3cfbc03d3dcbbf8ded0c13f611e"),
        Entry("music.cover-mert", "MERT-v2-FullSong-Q8_0.gguf", 902283712, "35a6032ec7e8123c02becdefc7bfe10a3adb33faa8a1951dc0af864b4494b8ef")];
    private static ComponentCatalogEntry Entry(string id, string file, long bytes, string hash) => new() {
        Id = id, Name = file, Version = Revision, Description = "YuE2 cover score transcription; optional Q8 model.",
        DeliveryKind = ComponentDeliveryKinds.File, FileName = file, DownloadSizeBytes = bytes, InstalledSizeBytes = bytes,
        DownloadUrl = "https://huggingface.co/Serveurperso/YuE2-GGUF/resolve/" + Revision + "/" + file,
        Source = "https://huggingface.co/Serveurperso/YuE2-GGUF/tree/" + Revision, Sha256 = hash,
        HealthCheckRelativePath = file, License = "CC BY-NC 4.0; original model terms", LicenseIds = [LicenseId] };
    public static string CacheDirectory => Path.Combine(AppDataPaths.BaseDirectory, "Music", "ScoreModels", Revision[..12]);
    public static async Task PrepareAsync(bool download, IProgress<ComponentDownloadProgress>? progress, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync([LicenseId], token); var manager = new ComponentManager();
        foreach (var entry in Entries) {
            if (download) await manager.DownloadAndInstallAsync(entry.Id, progress, token);
            var source = Path.Combine(manager.GetInstallDirectory(entry), entry.FileName);
            var pins = new PinnedTreeFile[] { new(entry.FileName, entry.DownloadSizeBytes, entry.Sha256) };
            await RuntimeFileAudit.VerifyAsync(entry.Id, Path.GetDirectoryName(source)!, Revision, pins, token);
            Directory.CreateDirectory(CacheDirectory); var target = Path.Combine(CacheDirectory, entry.FileName);
            if (File.Exists(target))
            {
                try { await RuntimeFileAudit.VerifyAsync(entry.Id + ".cache", CacheDirectory, Revision, pins, token); continue; }
                catch (IOException) { /* Repair the private copy from the separately audited installation. */ }
            }
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                await using (var input = File.OpenRead(source)) await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    await input.CopyToAsync(output, token); output.Flush(true);
                }
                if (!string.Equals(await MusicAudioTaskFiles.HashAsync(temporary, token), entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music.Transform.InputChanged");
                File.Move(temporary, target, true); ComponentAudit.Invalidate(CacheDirectory);
                await RuntimeFileAudit.VerifyAsync(entry.Id + ".cache", CacheDirectory, Revision, pins, token);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
