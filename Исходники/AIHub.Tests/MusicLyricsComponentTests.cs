using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicLyricsComponentTests
{
    [TestMethod]
    public void AllSixGenerators_IncludeSharedAlignmentAndItsLicenseTree()
    {
        foreach (var variation in new[] { MusicComponentCatalog.ModelId, MusicModelVariants.Bf16,
            MusicStudioRuntime.Variation, MusicAceCatalog.Variation, MusicDiffRhythmCatalog.Variation,
            MusicHeartMuLaCatalog.Variation })
        {
            var components = MusicModelVariants.Components(variation);
            foreach (var id in MusicLyricsComponentCatalog.LicenseIds)
                Assert.IsTrue(components.Contains(id), $"{variation} omits {id}");
            Assert.IsTrue(components.Contains(MusicLyricsComponentCatalog.ModelId));
            Assert.IsTrue(components.Contains(MusicLyricsComponentCatalog.RuntimeId));
            Assert.IsFalse(components.Contains("model.whisper.small"), variation);
        }
    }

    [TestMethod]
    public void SharedAlignment_ResolvesCpuAndAllPinnedArchivesBeforeModel()
    {
        var entries = ComponentCatalog.ResolveDependencies([MusicLyricsComponentCatalog.ModelId]);
        Assert.AreEqual(entries.Count, entries.Select(x => x.Id).Distinct().Count());
        Assert.IsTrue(entries.Any(x => x.Id == HardwareRuntimeCatalog.PythonCpuId));
        Assert.AreEqual(MusicLyricsComponentCatalog.ModelId, entries[^1].Id);
        Assert.IsTrue(entries.FindIndex(x => x.Id == HardwareRuntimeCatalog.PythonCpuId)
            < entries.FindIndex(x => x.Id == MusicLyricsComponentCatalog.RuntimeId));
        foreach (var entry in MusicLyricsComponentCatalog.Entries)
        {
            Assert.AreEqual(64, entry.Sha256.Length, entry.Id);
            Assert.IsTrue(entry.DownloadSizeBytes > 0, entry.Id);
            Assert.IsTrue(Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https", entry.Id);
            Assert.IsTrue(ComponentSemanticPassportCatalog.HasPassport(entry.Id), entry.Id);
            Assert.IsTrue(entry.LicenseIds.Contains(entry.Id), entry.Id);
        }
    }

    [TestMethod]
    public void RuntimeArchives_HaveCompleteFileHashManifestsAndOriginalLicenses()
    {
        using var manifests = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "music-lyrics-components.json")));
        using var licenses = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Licenses", "catalog.json")));
        var terms = licenses.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("Id").GetString()!);
        foreach (var payload in manifests.RootElement.EnumerateArray())
        {
            var id = payload.GetProperty("id").GetString()!;
            var files = payload.GetProperty("files").EnumerateArray().ToArray();
            Assert.IsTrue(files.Length > 0, id);
            Assert.AreEqual(payload.GetProperty("installedSize").GetInt64(), files.Sum(x => x.GetProperty("size").GetInt64()), id);
            foreach (var file in files)
            {
                var path = file.GetProperty("path").GetString()!;
                Assert.IsFalse(Path.IsPathRooted(path) || path.Split('/').Contains(".."), path);
                Assert.AreEqual(64, file.GetProperty("sha256").GetString()!.Length, path);
            }
            Assert.IsTrue(terms.ContainsKey(id), id);
            var license = terms[id];
            Assert.IsFalse(license.GetProperty("Basic").GetBoolean(), id);
            Assert.IsTrue(license.GetProperty("Texts").GetArrayLength() > 0, id);
            foreach (var text in license.GetProperty("Texts").EnumerateArray())
                Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "Licenses", text.GetString()!)), text.GetString());
        }
        Assert.IsTrue(terms.ContainsKey(MusicLyricsComponentCatalog.ModelId));
    }
}

internal static class MusicLyricsComponentTestList
{
    internal static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    { for (var index = 0; index < list.Count; index++) if (predicate(list[index])) return index; return -1; }
}
