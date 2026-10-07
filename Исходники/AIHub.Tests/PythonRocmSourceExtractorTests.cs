using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PythonRocmSourceExtractorTests
{
    [TestMethod]
    [DataRow("rocm-7.2.1/../outside.py", false)]
    [DataRow("rocm-7.2.1/src/rocm_sdk/link.py", true)]
    [DataRow("other-root/file.py", false)]
    public async Task SourceTraversalAndLinksAreRejectedBeforeWriting(string name, bool link)
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rocm-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "source.tar.gz");
        await using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, name);
            if (link) entry.LinkName = "../../outside.py";
            else entry.DataStream = new MemoryStream([1]);
            tar.WriteEntry(entry);
        }
        var bytes = await File.ReadAllBytesAsync(archive);
        var artifact = new PinnedPythonArtifact("rocm", "7.2.1", "rocm-7.2.1.tar.gz", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), new Uri("https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz"));
        var stage = Path.Combine(root, "stage.installing"); Directory.CreateDirectory(stage);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            PythonRocmSourceExtractor.ExtractAsync(artifact, archive, stage, CancellationToken.None));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(stage).Any());
    }
}
