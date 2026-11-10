using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.VoiceNotifications;

internal sealed record VoiceAudioItem(string ClipId, string Name, string Sha256, string Language,
    string SourceText, string SynthesisText, double Seconds, string? TakeId = null);
internal sealed record VoiceAudioManifest(int Schema, VoiceAudioItem[] Items);
internal sealed record VoicePackageIdentity(string Language, string Revision, string ManifestSha256);

internal sealed class VoiceNotificationPackage(string directory, VoicePackageIdentity identity, VoiceAudioItem[] items)
{
    internal string Directory { get; } = directory;
    internal string Language => identity.Language;
    internal string Revision => identity.Revision;
    internal IReadOnlyDictionary<string, VoiceAudioItem> Items { get; } = items.ToDictionary(item => item.ClipId, StringComparer.Ordinal);
    internal string Resolve(VoiceAudioItem item) => SafePath(Directory, item.Name);
    internal static bool IsLanguage(string value) => Regex.IsMatch(value, "^[a-z]{2,8}(-[a-z0-9]{2,8}){0,3}$", RegexOptions.CultureInvariant);
    internal static string SafePath(string root, string relative, bool rejectLinks = true)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.StartsWith('/') || relative.Split('/').Any(part => part is ".." or "." or "" || part.EndsWith('.') || part.EndsWith(' ') ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        if (rejectLinks) Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        return path;
    }
    internal static async Task<VoiceNotificationPackage> LoadAsync(string directory, CancellationToken token)
    {
        var identityPath = Path.Combine(directory, "package.json");
        var identity = JsonSerializer.Deserialize<VoicePackageIdentity>(await File.ReadAllTextAsync(identityPath, token))
            ?? throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        if (!IsLanguage(identity.Language)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<VoiceAudioManifest>(await File.ReadAllTextAsync(manifestPath, token))
            ?? throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        if (manifest.Schema is not (1 or 2) || manifest.Items.Length == 0 || manifest.Items.Length > 20000)
            throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        var package = new VoiceNotificationPackage(directory, identity, manifest.Items);
        Task Presence(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("VoiceNotifications.Missing");
            foreach (var item in manifest.Items)
                if (item.Language != identity.Language || !File.Exists(package.Resolve(item))) throw new FileNotFoundException("VoiceNotifications.Missing");
            return Task.CompletedTask;
        }
        await ComponentAudit.RunAsync("VoiceNotifications." + identity.Language, directory, identity.Revision, Presence, async ct =>
        {
            await Presence(ct);
            if (!await HasHash(manifestPath, identity.ManifestSha256, ct)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            foreach (var item in manifest.Items)
                if (!await HasHash(package.Resolve(item), item.Sha256, ct)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        }, token);
        return package;
    }
    internal static async Task<bool> HasHash(string path, string expected, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
