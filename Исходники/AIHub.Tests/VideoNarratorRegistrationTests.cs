using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoNarratorRegistrationTests
{
    [TestMethod]
    public async Task ExplicitMaintenanceRegistersPreparedNarratorsAfterVerifiedGpuTrials()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_VOICE_MODELS_ROOT");
        var evidence = Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_OUTPUT");
        var backup = Environment.GetEnvironmentVariable("LOPATA_VOICE_LIBRARY_BACKUP");
        if (Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_REGISTER") != "1" || string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(backup))
        { Assert.Inconclusive("Explicit maintenance invocation and backed-up library are required."); return; }
        Assert.IsTrue(File.Exists(backup));
        using var verified = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "verification.json")));
        Assert.AreEqual(10, verified.RootElement.GetProperty("models").GetArrayLength());
        Assert.IsTrue(verified.RootElement.GetProperty("metadataVerified").GetBoolean());
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            // Normal live ComponentAudit store: test receipts do not certify a user installation.
            using var preparation = new VideoModelPreparation();
            var voices = VideoNarratorCatalog.Models.Select(m => m.Id).ToArray();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            Assert.IsTrue(await preparation.PrepareAsync(root, new("speech", voices), false, null, deadline.Token));
            var store = new ManagedModelLibraryStore();
            foreach (var card in VideoModelPreparation.Cards(root, new("speech", voices)))
            {
                Assert.IsTrue(VideoNarratorCatalog.MatchesPin(store.Load(card.ModelArtifactId), card));
                Assert.AreEqual(AIHub.Models.ManagedModelStatuses.Installed, store.Load(card.ModelArtifactId)!.Status);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "registered.json"), JsonSerializer.Serialize(new { voices, revision = VideoNarratorRuntime.Revision, verifiedWithLiveAudit = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
}
