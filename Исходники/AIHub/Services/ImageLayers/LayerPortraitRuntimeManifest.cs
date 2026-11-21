using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

internal static class LayerPortraitRuntimeManifest
{
    private sealed record Wheel(string Name, string Version, string File, long Size, string Sha256, Uri Url);
    private static readonly Lazy<(string Identity, PinnedPythonArtifact[] Files)> Manifest = new(() =>
    {
        using var stream = typeof(LayerPortraitRuntimeManifest).Assembly.GetManifestResourceStream("AIHub.ImageLayers.PortraitWheels")
            ?? throw new InvalidDataException("ImageLayers.Components");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var content = bytes.ToArray();
        var rows = JsonSerializer.Deserialize<Wheel[]>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("ImageLayers.Components");
        if (rows.Length == 0 || rows.Select(row => row.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Length)
            throw new InvalidDataException("ImageLayers.Components");
        var artifacts = rows.Select(row =>
        {
            if (row.Size <= 0 || row.Size > 256 * 1024 * 1024 || !LayerDocument.ValidDigest(row.Sha256)
                || Path.GetFileName(row.File) != row.File || !row.File.EndsWith(".whl", StringComparison.Ordinal)
                || row.Url.Scheme != "https" || row.Url.Host != "files.pythonhosted.org" || !row.Url.IsDefaultPort
                || row.Url.UserInfo.Length > 0 || row.Url.Query.Length > 0 || row.Url.Fragment.Length > 0
                || Uri.UnescapeDataString(row.Url.Segments[^1]) != row.File)
                throw new InvalidDataException("ImageLayers.Components");
            return new PinnedPythonArtifact(row.Name, row.Version, row.File, row.Size, row.Sha256, row.Url);
        }).ToArray();
        return (Convert.ToHexStringLower(SHA256.HashData(content)), artifacts);
    });

    internal static string Identity => Manifest.Value.Identity;
    internal static IReadOnlyList<PinnedPythonArtifact> Wheels => Manifest.Value.Files;
}
