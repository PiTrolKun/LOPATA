using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipValidationTests
{
    private static readonly SongClipSettings Settings = new() { Width = 640, Height = 360, Fps = 24 };

    [TestMethod]
    public void FullSongWithExpectedStreamsAndModelHistoryIsAccepted()
    {
        Assert.AreEqual(8d, GlitchframeRunner.ValidateResult(Output(), Source(), Settings), .000001);
    }

    [TestMethod]
    [DataRow(4d, 8d)]
    [DataRow(8d, 4d)]
    public void TruncatedVideoOrAudioIsRejectedEvenWhenContainerDurationIsComplete(double videoSeconds, double audioSeconds)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            GlitchframeRunner.ValidateResult(Output(videoSeconds, audioSeconds), Source(), Settings));
    }

    [TestMethod]
    public void NullProvenanceIsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            GlitchframeRunner.ValidateResult(Output(provenance: "null"), Source(), Settings));
    }

    [TestMethod]
    public void ProvenanceFromAnotherScenarioIsRejected()
    {
        var facts = Provenance() with { Scenario = "another-scenario" };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            GlitchframeRunner.ValidateResult(Output(provenance: facts.Json()), Source(), Settings));
    }

    [TestMethod]
    public void IncorrectOutputSizeIsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            GlitchframeRunner.ValidateResult(Output(width: 1280, height: 720), Source(), Settings));
    }

    private static JsonElement Source() => JsonSerializer.SerializeToElement(new
    {
        streams = new[] { new { codec_type = "audio", duration = "8.0" } },
        format = new { duration = "8.0" }
    });

    private static JsonElement Output(double videoSeconds = 8, double audioSeconds = 8,
        int width = 640, int height = 360, string? provenance = null) => JsonSerializer.SerializeToElement(new
    {
        streams = new object[]
        {
            new { codec_type = "video", width, height, duration = videoSeconds.ToString(CultureInfo.InvariantCulture) },
            new { codec_type = "audio", duration = audioSeconds.ToString(CultureInfo.InvariantCulture) }
        },
        format = new
        {
            duration = "8.0",
            tags = new Dictionary<string, string> { ["LOPATA_VIDEO"] = provenance ?? Provenance().Json() }
        }
    });

    private static OutputProvenance Provenance() => new()
    {
        Scenario = "song-to-clip/glitchframe",
        GenerationSeconds = 42,
        ModelHistory = "recorded-inference",
        Models =
        [
            new("stabilityai/stable-diffusion-xl-base-1.0", "SDXL 1.0 FP16",
                "462165984030d82259a11f4367a4eed129e94a7b", "background-image-generation"),
            new("OlaProeis/Glitchframe", "Glitchframe",
                "a9660508fdbbfbc6243f570d71bbd2af8ee5b111", "music-video-composition")
        ]
    };
}

[TestClass]
public sealed class SongClipInstallTests
{
    [TestMethod]
    public async Task MissingDerivedFileNeedsExplicitRepairAndPinnedArchiveIsPreserved()
    {
        using var files = new VideoPreparationTests.TestFiles();
        using var audit = ComponentAudit.UseTestStore(files.File("audit"), enabled: true);
        var archiveDirectory = files.File("downloaded");
        Directory.CreateDirectory(archiveDirectory);
        var archivePath = Path.Combine(archiveDirectory, "source.zip");
        byte[] module = Encoding.UTF8.GetBytes("VALUE = 'pinned source'\n");
        const string moduleRelativePath = "Glitchframe/config.py";
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using var entry = archive.CreateEntry(moduleRelativePath).Open();
            await entry.WriteAsync(module);
        }
        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        var archiveHash = Convert.ToHexString(SHA256.HashData(archiveBytes));
        var card = new ManagedModelArtifactCard
        {
            ModelArtifactId = "song-clip-test-source",
            Revision = "test-pinned-revision",
            InstallDirectory = archiveDirectory,
            Files =
            [
                new()
                {
                    RelativePath = "source.zip", SourceUrl = "https://example.invalid/source.zip",
                    SizeBytes = archiveBytes.Length, Sha256 = archiveHash
                }
            ]
        };
        PinnedTreeFile[] inventory =
        [
            new(moduleRelativePath, module.Length, Convert.ToHexString(SHA256.HashData(module)))
        ];
        var target = files.File("derived");
        var requiredFile = Path.Combine(target, moduleRelativePath);

        var installed = await GlitchframeComponents.InstallTreeAsync(card, inventory, target,
            wheels: false, CancellationToken.None, repair: false);

        Assert.AreEqual(Path.GetFullPath(target), installed);
        CollectionAssert.AreEqual(module, await File.ReadAllBytesAsync(requiredFile));
        Assert.AreEqual(0, Directory.GetDirectories(files.Root, "derived.quarantine-*").Length);
        File.Delete(requiredFile);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            GlitchframeComponents.InstallTreeAsync(card, inventory, target,
                wheels: false, CancellationToken.None, repair: false));

        Assert.IsTrue(Directory.Exists(target));
        Assert.IsFalse(File.Exists(requiredFile));
        Assert.AreEqual(0, Directory.GetDirectories(files.Root, "derived.quarantine-*").Length);

        var repaired = await GlitchframeComponents.InstallTreeAsync(card, inventory, target,
            wheels: false, CancellationToken.None, repair: true);

        Assert.AreEqual(Path.GetFullPath(target), repaired);
        CollectionAssert.AreEqual(module, await File.ReadAllBytesAsync(requiredFile));
        var quarantine = Directory.GetDirectories(files.Root, "derived.quarantine-*").Single();
        Assert.IsTrue(Directory.Exists(Path.Combine(quarantine, "Glitchframe")));
        Assert.IsFalse(File.Exists(Path.Combine(quarantine, moduleRelativePath)));
        var retainedArchive = await File.ReadAllBytesAsync(archivePath);
        Assert.AreEqual(archiveHash, Convert.ToHexString(SHA256.HashData(retainedArchive)));
        CollectionAssert.AreEqual(archiveBytes, retainedArchive);
    }
}
