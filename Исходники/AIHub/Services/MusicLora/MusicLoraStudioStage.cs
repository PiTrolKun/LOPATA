using System.IO;
using System.Text.Json;

namespace AIHub.Services.MusicLora;

/// <summary>Only private request copies reach Studio's adapter directory; the managed originals stay immutable.</summary>
internal sealed class MusicLoraStudioStage(IDisposable lease, List<string> files) : IDisposable
{
    public static async Task<MusicLoraStudioStage> CreateAsync(string root, string data, MusicLoraUse[] uses, CancellationToken token)
    {
        var lease = await MusicLoraAssets.AcquireAsync(root, uses, token); var files = new List<string>();
        var stage = new MusicLoraStudioStage(lease, files);
        try {
            foreach (var use in uses) {
                var definition = MusicLoraCatalog.Find(use.Id); var card = definition.Card(root);
                var directory = Path.Combine(data, "adapters", definition.Id);
                Lopata.Updates.SafeUpdatePath.RejectLinks(directory); Directory.CreateDirectory(directory);
                foreach (var file in card.Files) {
                    var target = Path.Combine(directory, file.RelativePath); Lopata.Updates.SafeUpdatePath.RejectLinks(target);
                    await using var input = File.OpenRead(Path.Combine(card.InstallDirectory, file.RelativePath));
                    await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    files.Add(target); await input.CopyToAsync(output, token); output.Flush(true);
                    output.Close();
                    await using var verify = File.OpenRead(target);
                    var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(verify, token));
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music.Lora.Missing");
                }
                var meta = Path.Combine(directory, "adapter.json"); Lopata.Updates.SafeUpdatePath.RejectLinks(meta); files.Add(meta);
                await File.WriteAllTextAsync(meta, JsonSerializer.Serialize(new { id = definition.Id, engine = "yue2-cpp",
                    name = definition.En, kind = "style", trigger = definition.Trigger.Length == 0 ? null : definition.Trigger,
                    slots = definition.Slots.Keys, scales = definition.Slots, origin = new { type = "imported" },
                    created_at = DateTimeOffset.UtcNow.ToString("o") }), token);
            }
            return stage;
        } catch { stage.Dispose(); throw; }
    }
    public void Dispose()
    {
        try { foreach (var file in files) {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                OwnedProcessRegistry.Log("music_lora_temporary_cleanup_failed", "Music.Studio", detail: error.GetType().Name);
            }
        } }
        finally { lease.Dispose(); }
    }
}
