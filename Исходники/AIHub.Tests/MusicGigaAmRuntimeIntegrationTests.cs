using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

/// <summary>Explicit isolated opt-in: real installer, immutable CPU profile and real song copy.</summary>
[TestClass, DoNotParallelize]
public sealed class MusicGigaAmRuntimeIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RealManagedCpuAlignsSongAndCancellationReleasesItsWorker()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_GIGAAM_RUNTIME_TEST") != "1" || AppDataPaths.ProjectRoot is not null)
            Assert.Inconclusive("Requires an explicitly enabled isolated UPDATE_STAND with prepared CPU libraries.");
        var audio = Environment.GetEnvironmentVariable("AIHUB_GIGAAM_AUDIO_COPY")!;
        var lyricsPath = Environment.GetEnvironmentVariable("AIHUB_GIGAAM_LYRICS_COPY")!;
        Assert.IsTrue(File.Exists(audio) && File.Exists(lyricsPath));
        Assert.IsTrue(Path.GetFullPath(audio).StartsWith(AppDataPaths.BaseDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
            "Only the stand's copy may be processed.");
        var lyrics = await File.ReadAllTextAsync(lyricsPath);
        string before;
        using (var beforeStream = File.OpenRead(audio)) before = Convert.ToHexString(await SHA256.HashDataAsync(beforeStream));
        var prior = ComponentLicenseGate.ConfirmAsync;
        var acknowledgements = new HashSet<string>();
        try
        {
            // Fixture policy only. Never writes the user's licence receipts or substitutes the desktop dialog.
            ComponentLicenseGate.ConfirmAsync = (ids, token) => {
                token.ThrowIfCancellationRequested(); foreach (var id in ids) acknowledgements.Add(id);
                return Task.CompletedTask;
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var manager = new ComponentManager();
            await MusicLyricsComponentCatalog.PrepareAsync(true, null, timeout.Token);
            await MusicLyricsComponentCatalog.VerifyAsync(manager, timeout.Token);
            Assert.IsTrue(MusicLyricsComponentCatalog.IsAvailable(manager));
            foreach (var id in MusicLyricsComponentCatalog.LicenseIds) Assert.IsTrue(acknowledgements.Contains(id),id);
            var service = new MusicLyricsAlignmentService(manager);
            var messages = new List<string>();
            service.Log += message => { lock(messages) messages.Add(message); };
            var result = await service.AlignAsync(audio,lyrics,Path.Combine(AppDataPaths.BaseDirectory,"successful-analysis"),timeout.Token,"ru");
            Assert.AreEqual(MusicLyricsAlignmentService.Provider,result.Provider);
            Assert.AreEqual("complete",result.State);
            Assert.AreEqual(MusicLyricsCtcProtocol.SourceRows(lyrics).Length,result.Lines.Length);
            Assert.IsTrue(messages.Any(m=>m.Contains("CPU",StringComparison.Ordinal)));
            TestContext.WriteLine(JsonSerializer.Serialize(result));
            await File.WriteAllTextAsync(Path.Combine(AppDataPaths.BaseDirectory,"validated-result.json"),JsonSerializer.Serialize(result));

            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellable = new MusicLyricsAlignmentService(manager);
            cancellable.Log += message => {
                if(message.StartsWith("[Lyrics] Acoustic frames:",StringComparison.Ordinal)) cancel.Cancel();
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancellable.AlignAsync(audio,lyrics,
                Path.Combine(AppDataPaths.BaseDirectory,"cancelled-analysis"),cancel.Token,"ru"));
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Music.LyricsAlignment"),
                "Cancellation returned while the owned acoustic worker was still running.");
            Assert.IsFalse(Directory.GetFiles(Path.Combine(AppDataPaths.BaseDirectory,"cancelled-analysis"),"alignment.json",SearchOption.AllDirectories).Any());
            using var afterStream = File.OpenRead(audio);
            Assert.AreEqual(before,Convert.ToHexString(await SHA256.HashDataAsync(afterStream)));
        }
        finally { ComponentLicenseGate.ConfirmAsync = prior; }
    }
}
