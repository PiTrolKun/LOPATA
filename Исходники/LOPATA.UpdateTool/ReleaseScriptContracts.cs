using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml.Linq;
using Lopata.Updates;

namespace Lopata.UpdateTool;

/// <summary>Root Tools/*.py items are shipped both as ordinary files and embedded resources.</summary>
public static class ReleaseScriptContracts
{
    public static async Task<Dictionary<string, (long Size, string Sha256)>> VerifyPublishedAsync(
        string appRoot, string projectPath, CancellationToken token = default)
    {
        var bindings = XDocument.Load(projectPath).Descendants("EmbeddedResource")
            .Select(item => (Path: ((string?)item.Attribute("Include"))?.Replace('\\', '/'),
                Name: (string?)item.Attribute("LogicalName") ?? (string?)item.Element("LogicalName")))
            .Where(item => item.Path is { } path && path.StartsWith("Tools/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && !path.AsSpan("Tools/".Length).Contains('/'))
            .ToArray();
        if (bindings.Length == 0 || bindings.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            throw new InvalidDataException("The application project must declare named embedded resources for bundled Python scripts.");

        var assembly = SafeUpdatePath.Resolve(appRoot, "AIHub.dll");
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var metadata = pe.GetMetadataReader();
        var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
        var resources = pe.GetSectionData(directory.RelativeVirtualAddress);
        if (directory.Size <= 0 || directory.Size > resources.Length)
            throw new InvalidDataException("Invalid application resource section.");
        var offsets = metadata.ManifestResources.Select(metadata.GetManifestResource)
            .Where(resource => resource.Implementation.IsNil)
            .ToDictionary(resource => metadata.GetString(resource.Name), resource => resource.Offset, StringComparer.Ordinal);
        var files = new Dictionary<string, (long Size, string Sha256)>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var target = SafeUpdatePath.Resolve(appRoot, binding.Path!);
            if (!offsets.TryGetValue(binding.Name!, out var offset) || offset < 0 || offset > directory.Size - sizeof(int))
                throw new InvalidDataException("Missing embedded script: " + binding.Path);

            var start = (int)offset;
            var size = BinaryPrimitives.ReadInt32LittleEndian(resources.GetContent(start, sizeof(int)).AsSpan());
            start += sizeof(int);
            if (size <= 0 || size > directory.Size - start)
                throw new InvalidDataException("Invalid embedded script size: " + binding.Path);
            var sha = Convert.ToHexStringLower(SHA256.HashData(resources.GetContent(start, size).AsSpan()));
            if (!await UpdatePlanner.MatchesAsync(target, size, sha, token))
                throw new InvalidDataException("Published script differs from AIHub.dll: " + binding.Path);
            files.Add("app/" + binding.Path, (size, sha));
        }
        stream.Position = 0;
        files.Add("app/AIHub.dll", (stream.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token))));
        return files;
    }

    public static void VerifyManifest(UpdateManifest manifest, IReadOnlyDictionary<string, (long Size, string Sha256)> published)
    {
        var files = manifest.Files.ToDictionary(file => file.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, expected) in published)
            if (!files.TryGetValue(key, out var actual) || actual.Size != expected.Size
                || !expected.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package differs from the verified application payload: " + key);
    }
}
