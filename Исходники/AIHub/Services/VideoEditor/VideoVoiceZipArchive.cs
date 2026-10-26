using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

/// <summary>Project pinned upstream ZIP contents without executing installation scripts.</summary>
internal static class VideoVoiceZipArchive
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal static async Task<IReadOnlyList<string>> ProcessAsync(ManagedModelArtifactFile artifact, string source,
        string root, bool extract, bool stripRoot, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(source);
        await using var input = File.OpenRead(source);
        if (input.Length != artifact.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Voice ZIP integrity failure.");
        input.Position = 0; using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        if (zip.Entries.Count is < 1 or > 10000 || zip.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Voice ZIP exceeds extraction limits.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? prefix = null;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/'); if (name.Length == 0) continue;
            _ = PinnedZipExpansionVerifier.ResolveArchivePath(root, name);
            if (entry.FullName.EndsWith('/')) continue;
            if (stripRoot)
            {
                var slash = name.IndexOf('/'); if (slash < 1) throw new InvalidDataException("Missing voice source root.");
                prefix ??= name[..slash]; if (prefix != name[..slash]) throw new InvalidDataException("Ambiguous voice source roots.");
                name = name[(slash + 1)..];
            }
            var package = stripRoot ? artifact.RelativePath switch {
                "cosyvoice-074ca6dc.zip" => "cosyvoice", "matcha-dd9105b3.zip" => "matcha", _ => null } : null;
            // Training examples contain upstream symlinks. Project only inference packages and notices;
            // no links are created or followed, including links within the selected packages.
            if (package is not null && !name.StartsWith(package + "/", StringComparison.Ordinal)
                && name is not ("LICENSE" or "LICENSE.txt" or "README.md" or "NOTICE")) continue;
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Voice ZIP links are not allowed.");
            var path = PinnedZipExpansionVerifier.ResolveArchivePath(root, name);
            if (!paths.Add(path)) throw new InvalidDataException("Duplicate voice ZIP entry.");
            await using var original = entry.Open();
            if (extract)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                await original.CopyToAsync(output, token);
            }
            else
            {
                await using var actual = File.OpenRead(path);
                var originalHash = await SHA256.HashDataAsync(original, token); var actualHash = await SHA256.HashDataAsync(actual, token);
                if (actual.Length != entry.Length || !CryptographicOperations.FixedTimeEquals(originalHash, actualHash))
                    throw new InvalidDataException("Voice ZIP payload integrity failure.");
            }
        }
        return paths.ToArray();
    }

    internal static async Task<string> PrepareVoskAsync(ManagedModelArtifactCard card, CancellationToken token, bool reinstall = false)
    {
        await Gate.WaitAsync(token);
        try {
        var root = Path.Combine(card.InstallDirectory, "Expanded"); var file = card.Files.Single();
        var source = Path.Combine(card.InstallDirectory, file.RelativePath);
        if (!Directory.Exists(root) || reinstall)
        {
            var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
            Lopata.Updates.SafeUpdatePath.RejectLinks(staging); Directory.CreateDirectory(staging);
            try
            {
                await ProcessAsync(file, source, staging, true, false, token);
                token.ThrowIfCancellationRequested(); Lopata.Updates.SafeUpdatePath.RejectLinks(root);
                var previous = root + ".previous-" + Guid.NewGuid().ToString("N"); Lopata.Updates.SafeUpdatePath.RejectLinks(previous);
                if (Directory.Exists(root)) Directory.Move(root, previous);
                try { Directory.Move(staging, root); ComponentAudit.Invalidate(root); }
                catch { if (Directory.Exists(previous)) Directory.Move(previous, root); throw; }
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        await PinnedZipExpansionVerifier.VerifyAsync(source, file, root, token);
        return Path.Combine(root, "vosk-model-tts-ru-0.10-multi");
        } finally { Gate.Release(); }
    }
}
