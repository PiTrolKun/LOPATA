using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services;

internal sealed record PinnedPythonArtifact(string Name, string Version, string FileName, long Bytes, string Sha256, Uri Source);

/// <summary>Exact upstream artifacts; the first-launch installer must not resolve moving pip dependencies.</summary>
internal sealed partial class PinnedPythonWheelSet
{
    internal IReadOnlyList<PinnedPythonArtifact> Wheels { get; }
    internal long DownloadBytes => checked(Wheels.Sum(wheel => wheel.Bytes));
    internal const string PythonVersion = "3.12.10";
    internal const string TorchVersion = "2.10.0+cpu";
    internal const string TransformersVersion = "5.3.0";
    private PinnedPythonWheelSet(IReadOnlyList<PinnedPythonArtifact> wheels) { Wheels = wheels; }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    internal static PinnedPythonWheelSet Load(PythonRuntimeProfile profile = PythonRuntimeProfile.Cpu) => Read(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Tools", profile.LockFile())), profile);

    internal static PinnedPythonWheelSet Read(byte[] bytes, PythonRuntimeProfile profile = PythonRuntimeProfile.Cpu)
    {
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("Oversized Python artifact lock.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var expectedTorch = profile.TorchVersion();
        if (root.GetProperty("python").GetString() != PythonVersion || root.GetProperty("torch").GetString() != expectedTorch
            || root.GetProperty("transformers").GetString() != TransformersVersion)
            throw new InvalidDataException("Unexpected Python runtime profile.");
        var artifacts = root.GetProperty("wheels").EnumerateArray().Select(row =>
        {
            var name = row.GetProperty("name").GetString()!;
            var version = row.GetProperty("version").GetString()!;
            var file = row.GetProperty("file").GetString()!;
            var hash = row.GetProperty("sha256").GetString()!;
            var size = row.GetProperty("size").GetInt64();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || string.IsNullOrWhiteSpace(version) || version.Length > 100
                || string.IsNullOrWhiteSpace(file) || file.Length > 240 || file.IndexOfAny(['/', '\\', ':']) >= 0
                || file != Path.GetFileName(file) || (!file.EndsWith(".whl", StringComparison.Ordinal)
                    && !(profile == PythonRuntimeProfile.Rocm721 && name == "rocm" && version == "7.2.1" && file == "rocm-7.2.1.tar.gz"))
                || !DigestPattern().IsMatch(hash) || size <= 0 || size > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Invalid pinned Python artifact.");
            var source = new Uri(row.GetProperty("url").GetString()!, UriKind.Absolute);
            if (source.Scheme != "https" || !string.IsNullOrEmpty(source.UserInfo) || !source.IsDefaultPort
                || !string.IsNullOrEmpty(source.Query) || !string.IsNullOrEmpty(source.Fragment)
                || (source.Host is not ("files.pythonhosted.org" or "download.pytorch.org" or "download-r2.pytorch.org")
                    && !(profile == PythonRuntimeProfile.Rocm721 && source.Host == "repo.radeon.com"
                        && source.AbsolutePath.StartsWith("/rocm/windows/rocm-rel-7.2.1/", StringComparison.Ordinal)))
                || Uri.UnescapeDataString(source.Segments.Last()) != file)
                throw new InvalidDataException("Python artifact URL is outside the pinned upstream policy.");
            return new PinnedPythonArtifact(name, version, file, size, hash, source);
        }).ToArray();
        static string PackageKey(string name) => name.ToLowerInvariant().Replace('-', '_').Replace('.', '_');
        if (artifacts.Length is < 2 or > 128 || artifacts.Select(a => PackageKey(a.Name)).Distinct().Count() != artifacts.Length
            || artifacts.Select(a => a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Length
            || !artifacts.Any(a => a.Name == "torch" && a.Version == expectedTorch)
            || !artifacts.Any(a => a.Name == "transformers" && a.Version == TransformersVersion))
            throw new InvalidDataException("Incomplete or ambiguous pinned Python dependency set.");
        return new(artifacts);
    }
}
