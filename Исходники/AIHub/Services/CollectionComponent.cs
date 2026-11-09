using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Lopata.Updates;
using Lopata.Collection;

namespace AIHub.Services;

internal sealed record CollectionPayloadFile(string Path, long Size, string Sha256);
internal sealed record CollectionPayload(int Schema, string Revision, string Native, string Worker, CollectionPayloadFile[] Files,
    string SourceRevision = "", string RuntimeIdentifier = "");

internal static class CollectionComponent
{
    internal static async Task<(string Native, string Worker)> PrepareAsync(CancellationToken token = default,
        string? componentRoot = null, string? expectedWorker = null)
    {
        var root = Path.GetFullPath(componentRoot ?? Path.Combine(AppContext.BaseDirectory, "Collection"));
        var manifestPath = Path.Combine(root, "current.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 256 * 1024)
            throw new FileNotFoundException("The collection component manifest is missing.");
        var manifest = JsonSerializer.Deserialize<CollectionPayload>(await File.ReadAllTextAsync(manifestPath, token),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing collection manifest.");
        static bool Digest(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
        if (manifest.Schema != 1 || !Digest(manifest.Revision) || !Digest(manifest.SourceRevision)
            || manifest.RuntimeIdentifier is not ("win-x64" or "win-arm64")
            || string.IsNullOrWhiteSpace(manifest.Native) || string.IsNullOrWhiteSpace(manifest.Worker)
            || manifest.Files is not { Length: > 0 and <= 32 }
            || manifest.Files.Any(f => f is null || string.IsNullOrWhiteSpace(f.Path) || f.Size < 0 || !Digest(f.Sha256))
            || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
            throw new InvalidDataException("Invalid collection component manifest.");
        var requiredRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Unsupported Explorer architecture.")
        };
        if (manifest.RuntimeIdentifier != requiredRuntime)
            throw new InvalidDataException("The collection component must match the Windows Explorer architecture.");
        var revisionLines = new[] { "collection-payload-v2|" + manifest.RuntimeIdentifier + "|" + manifest.SourceRevision }
            .Concat(manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Path + "|"
                + f.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + f.Sha256));
        if (Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', revisionLines)))) != manifest.Revision)
            throw new InvalidDataException("The collection manifest revision does not match its files.");
        var generation = manifest.Native.Split('/')[0];
        if (manifest.Native != generation + "/LOPATA.CollectionShell.dll" || manifest.Worker != generation + "/LOPATA.CollectionWorker.exe"
            || manifest.Files.Any(f => !f.Path.StartsWith(generation + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("The collection manifest mixes component generations.");
        string Resolve(string relative)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(relative,
                "^[a-f0-9]{24}(-[a-f0-9]{8})?/[A-Za-z0-9][A-Za-z0-9._-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid collection component path.");
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Collection component path escapes its installation.");
            SafeUpdatePath.RejectLinks(full);
            return full;
        }
        if (!manifest.Files.Any(f => f.Path == manifest.Native) || !manifest.Files.Any(f => f.Path == manifest.Worker))
            throw new InvalidDataException("The collection entry points are not in the manifest.");
        // A previous process can observe current.json before the application's expected-path
        // switch. Validate the manifest first, but never audit a different generation for it.
        foreach (var file in manifest.Files) _ = Resolve(file.Path);
        var worker = Resolve(manifest.Worker);
        if (expectedWorker is not null)
        {
            if (!Path.IsPathFullyQualified(expectedWorker)) throw new InvalidDataException("Invalid expected collection worker path.");
            if (!worker.Equals(Path.GetFullPath(expectedWorker), StringComparison.OrdinalIgnoreCase))
                throw new CollectionWorkerSupersededException();
        }
        async Task Presence(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var entry in manifest.Files)
            {
                var file = new FileInfo(Resolve(entry.Path));
                if (!file.Exists || entry.Size < 0 || entry.Sha256.Length != 64)
                    throw new InvalidDataException("Incomplete collection installation.");
            }
            await Task.CompletedTask;
        }
        async Task Full(CancellationToken cancellation)
        {
            await Presence(cancellation);
            foreach (var entry in manifest.Files)
            {
                await using var stream = File.OpenRead(Resolve(entry.Path));
                var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
                if (stream.Length != entry.Size || digest != entry.Sha256)
                    throw new InvalidDataException("The collection component failed verification.");
            }
        }
        await ComponentAudit.RunAsync("explorer-collection", root, manifest.Revision, Presence, Full, token);
        return (Resolve(manifest.Native), worker);
    }
}
