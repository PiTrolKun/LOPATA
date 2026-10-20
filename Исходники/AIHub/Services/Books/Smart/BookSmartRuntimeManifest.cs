using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.Books.Smart;

/// <summary>Trusted upstream archive pins and an independently reconstructed expected installed tree.</summary>
internal static class BookSmartRuntimeManifest
{
    internal const string LockDigest = "8cfa4652beed5c928835fd7f07c961a4d48cd620ca512e273dfedd04b78c8891";
    internal const string PythonPathConfiguration = "python312.zip\n.\nLib/site-packages\nimport site\n";
    internal static string LockPath => Path.Combine(AppContext.BaseDirectory, "Tools", "BookSmart", "windows-cp312-wheel-lock.json");

    internal static IReadOnlyList<PinnedPythonArtifact> Artifacts(BookSmartSelection selection)
    {
        if (selection.OcrId is not ("paddle" or "ovis")) throw new ArgumentException("Unknown OCR selection.", nameof(selection));
        var bytes = File.ReadAllBytes(LockPath);
        if (bytes.Length > 512 * 1024 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(LockDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Book runtime wheel lock differs from its trusted pin.");
        using var document = JsonDocument.Parse(bytes);
        var wheels = document.RootElement.EnumerateArray().Select(row => new PinnedPythonArtifact(
            row.GetProperty("name").GetString()!, row.GetProperty("version").GetString()!, row.GetProperty("file").GetString()!,
            row.GetProperty("size").GetInt64(), row.GetProperty("sha256").GetString()!, new(row.GetProperty("url").GetString()!, UriKind.Absolute))).ToArray();
        foreach (var wheel in wheels)
        {
            if (wheel.Bytes <= 0 || wheel.Bytes > 2L * 1024 * 1024 * 1024 || wheel.Sha256.Length != 64
                || !wheel.Sha256.All(Uri.IsHexDigit) || wheel.FileName != Path.GetFileName(wheel.FileName)
                || wheel.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || !wheel.FileName.EndsWith(".whl", StringComparison.Ordinal)
                || wheel.Source.Scheme != "https" || wheel.Source.Host != "files.pythonhosted.org" || !wheel.Source.IsDefaultPort
                || !string.IsNullOrEmpty(wheel.Source.UserInfo) || !string.IsNullOrEmpty(wheel.Source.Query) || !string.IsNullOrEmpty(wheel.Source.Fragment)
                || Uri.UnescapeDataString(wheel.Source.Segments.Last()) != wheel.FileName)
                throw new InvalidDataException("Unsafe book runtime wheel pin.");
        }
        if (wheels.Length != 101 || wheels.Select(wheel => wheel.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != wheels.Length
            || !wheels.Any(wheel => wheel.Name == "paddlepaddle" && wheel.Version == "3.2.1")
            || !wheels.Any(wheel => wheel.Name == "paddleocr" && wheel.Version == "3.6.0")
            || !wheels.Any(wheel => wheel.Name == "paddlex" && wheel.Version == "3.6.1"))
            throw new InvalidDataException("Incomplete book runtime dependency set.");
        return selection.OcrId == "ovis"
            ? [PythonBootstrapArtifacts.Python, wheels.Single(wheel => wheel.Name == "pillow" && wheel.Version == "12.1.1")]
            : [PythonBootstrapArtifacts.Python, .. wheels];
    }

    internal static async Task<bool> MatchesAsync(string path, PinnedPythonArtifact artifact, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return file.Length == artifact.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<IReadOnlyList<PinnedTreeFile>> ExpectedFilesAsync(IReadOnlyList<PinnedPythonArtifact> artifacts,
        string cache, string? msvc, CancellationToken token)
    {
        var files = new Dictionary<string, PinnedTreeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            var path = Path.Combine(cache, artifact.FileName);
            if (!await MatchesAsync(path, artifact, token)) throw new InvalidDataException("Missing or modified book runtime archive: " + artifact.FileName);
            await using var source = File.OpenRead(path);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                var relative = PythonWheelExtractor.RelativeDestination(entry.FullName, artifact != PythonBootstrapArtifacts.Python);
                await using var input = entry.Open();
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (!files.TryAdd(relative, new(relative, entry.Length, digest)))
                    throw new InvalidDataException("Overlapping runtime archive entry: " + relative);
            }
        }
        AddBytes("python312._pth", Encoding.UTF8.GetBytes(PythonPathConfiguration));
        if (artifacts.Count > 1) AddBytes("Notices/wheel-lock.json", await File.ReadAllBytesAsync(LockPath, token));
        if (msvc is not null)
            foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                AddBytes(name, await File.ReadAllBytesAsync(Path.Combine(msvc, name), token));
        return files.Values.ToArray();

        void AddBytes(string relative, byte[] bytes) => files[relative] = new(relative, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
