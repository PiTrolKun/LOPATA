using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lopata.Updates;

namespace LOPATA.Updates.Tests;

internal sealed class UpdateFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "lopata-updates-" + Guid.NewGuid().ToString("N"));
    public string App => Path.Combine(Folder, "installed");
    public string Stage => Path.Combine(Folder, "stage");
    public UpdateRoots Roots => new(new Dictionary<string, string> { ["app"] = App });

    public UpdateFixture() => Directory.CreateDirectory(App);

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static UpdateFile Entry(string path, string text, string package = "files.zip") =>
        new("app", path, Encoding.UTF8.GetByteCount(text), Hash(text), package);

    public static UpdateManifest Manifest(string version, params UpdateFile[] files) => new()
    {
        Version = version, SourceCommit = new('a', 40), Files = files,
        Packages = files.Select(f => f.Package).Distinct().Select(id => new UpdatePackage(id,
            $"https://github.com/PiTrolKun/LOPATA/releases/download/v{version}/{id}", 1, new('b', 64))).ToArray(),
        Notes = [new(version, DateTimeOffset.UtcNow, "Fixture release")]
    };

    public void Put(string relative, string text)
    {
        var path = SafeUpdatePath.Resolve(App, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public (string Path, UpdateManifest Manifest) Zip(Dictionary<string, string> entries, UpdateManifest manifest)
    {
        var path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        var bytes = File.ReadAllBytes(path);
        return (path, manifest with { Packages = [manifest.Packages[0] with
            { Size = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }] });
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(Folder);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("lopata-updates-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe fixture cleanup.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
