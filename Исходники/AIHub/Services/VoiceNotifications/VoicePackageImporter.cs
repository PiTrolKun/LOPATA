using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.VoiceNotifications;

internal sealed record VoiceImportPart(string ManifestEntry, string Language, VoiceAudioItem[] Items);
internal sealed record VoiceImportPlan(string Archive, string Revision, VoiceImportPart[] Parts, Dictionary<string, string> Translations)
{
    internal int Count => Parts.Sum(part => part.Items.Length);
}

/// <summary>A ZIP is data. Validate before publishing a revision; never execute or overwrite originals.</summary>
internal static class VoicePackageImporter
{
    internal static VoiceImportPlan Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is 0 or > 24000 || zip.Entries.Sum(item => item.Length) > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!names.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            if (entry.FullName.EndsWith('/')) continue;
            _ = VoiceNotificationPackage.SafePath(Path.GetTempPath(), entry.FullName, rejectLinks: false);
            if (!new[] { ".json", ".opus", ".mp3", ".flac", ".wav", ".md", ".txt" }.Contains(Path.GetExtension(entry.FullName).ToLowerInvariant()))
                throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        }
        var parts = new List<VoiceImportPart>(); var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(entry => Path.GetFileName(entry.FullName) == "manifest.json"))
        {
            if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            using var input = entry.Open(); var manifest = JsonSerializer.Deserialize<VoiceAudioManifest>(input);
            if (manifest is not { Schema: 1 or 2, Items.Length: > 0 and <= 20000 }) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            var languages = manifest.Items.Select(item => item.Language).Distinct().ToArray();
            if (languages.Length != 1 || !VoiceNotificationPackage.IsLanguage(languages[0])) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            var clips = new HashSet<string>(StringComparer.Ordinal); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.ClipId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(item.ClipId, "^[a-zA-Z0-9_.-]{1,128}$") ||
                    string.IsNullOrWhiteSpace(item.Sha256)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
                _ = VoiceNotificationPackage.SafePath(Path.GetTempPath(), item.Name, rejectLinks: false);
                if (!new[] { ".opus", ".mp3", ".flac", ".wav" }.Contains(Path.GetExtension(item.Name).ToLowerInvariant()))
                    throw new InvalidDataException("VoiceNotifications.InvalidPackage");
                var audio = zip.GetEntry(Prefix(entry.FullName) + item.Name);
                if (!clips.Add(item.ClipId) || !files.Add(item.Name) || audio is null || audio.Length is <= 0 or > 64 * 1024 * 1024 ||
                    !double.IsFinite(item.Seconds) || item.Seconds is <= 0 or > 600 || string.IsNullOrWhiteSpace(item.SourceText) ||
                    item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            }
            if (parts.Any(part => part.Language == languages[0])) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            parts.Add(new(entry.FullName, languages[0], manifest.Items));
        }
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith("Localization/", StringComparison.Ordinal) &&
            entry.FullName.Count(c => c == '/') == 1 && entry.FullName.EndsWith(".json", StringComparison.Ordinal)))
        {
            var language = Path.GetFileNameWithoutExtension(entry.FullName);
            if (!VoiceNotificationPackage.IsLanguage(language) || entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8, true); var text = reader.ReadToEnd();
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(text) is null) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            translations.Add(language, text);
        }
        if (parts.Count == 0 && translations.Count == 0) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        using var archive = File.OpenRead(path);
        return new(path, Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), parts.ToArray(), translations);
    }
    private static string Prefix(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    internal static async Task<string> InstallPartAsync(VoiceImportPlan plan, VoiceImportPart part, string parent, CancellationToken token)
    {
        var final = Path.Combine(parent, plan.Revision);
        if (System.IO.Directory.Exists(final)) { await VoiceNotificationPackage.LoadAsync(final, token); return final; }
        System.IO.Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".import-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            using var zip = ZipFile.OpenRead(plan.Archive);
            foreach (var item in part.Items)
            {
                token.ThrowIfCancellationRequested();
                var destination = VoiceNotificationPackage.SafePath(staging, item.Name);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var input = zip.GetEntry(Prefix(part.ManifestEntry) + item.Name)!.Open())
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, token);
                if (!await VoiceNotificationPackage.HasHash(destination, item.Sha256, token)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
                if (!string.IsNullOrEmpty(item.TakeId)) ValidateMarker(destination, item);
            }
            var manifestPath = Path.Combine(staging, "manifest.json");
            await using (var input = zip.GetEntry(part.ManifestEntry)!.Open())
            await using (var output = File.Create(manifestPath)) await input.CopyToAsync(output, token);
            var identity = new VoicePackageIdentity(part.Language, plan.Revision,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, token))).ToLowerInvariant());
            await File.WriteAllTextAsync(Path.Combine(staging, "package.json"), JsonSerializer.Serialize(identity), token);
            token.ThrowIfCancellationRequested();
            System.IO.Directory.Move(staging, final); ComponentAudit.Invalidate(final);
            return final;
        }
        finally
        {
            // Staging was created here and every child path was confined by SafePath.
            var absolute = Path.GetFullPath(staging);
            var expected = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(absolute)) System.IO.Directory.Delete(absolute, true);
        }
    }
    private static void ValidateMarker(string path, VoiceAudioItem item)
    {
        var tags = MusicLyricsTimingMetadata.Tags(File.ReadAllBytes(path));
        if (!tags.TryGetValue("LOPATA_SPEECH_ASSET", out var text)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        using var marker = JsonDocument.Parse(text); var root = marker.RootElement;
        foreach (var (key, expected) in new[] { ("ClipId", item.ClipId), ("TakeId", item.TakeId), ("Language", item.Language),
            ("SourceText", item.SourceText), ("SynthesisText", item.SynthesisText) })
            if (root.GetProperty(key).GetString() != expected) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
    }
}
