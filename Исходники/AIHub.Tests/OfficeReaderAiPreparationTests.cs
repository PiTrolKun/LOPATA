using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderAiPreparationTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task TextWeightsUseSharedAcquisitionAuditAndCancellation(bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-preparation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), enabled);
            var library = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            byte[] content = [0x47, 0x47, 0x55, 0x46];
            var handler = new Bytes(content); using var http = new HttpClient(handler);
            var card = new ManagedModelArtifactCard { ModelArtifactId = "reader-test-weights", DisplayName = "Test weights", Role = ManagedModelRoles.Executor,
                Format = "GGUF", IsManaged = true, Revision = "test-pin", ModelsRoot = root, InstallDirectory = Path.Combine(root, "model"),
                Files = [new() { RelativePath = "model.gguf", SizeBytes = content.Length, Sha256 = Convert.ToHexString(SHA256.HashData(content)), SourceUrl = "https://example.invalid/model", Purpose = "main_model" }] };
            using var preparation = new ReaderTextPreparation(library, http);
            Assert.IsFalse(await preparation.PrepareAsync(card, false, null, default)); Assert.AreEqual(0, handler.Requests);
            Assert.IsTrue(await preparation.PrepareAsync(card, true, null, default)); Assert.IsTrue(handler.Requests > 0);
            Assert.IsTrue(await preparation.PrepareAsync(library.Load(card.ModelArtifactId)!, false, null, default));
            CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(card.InstallDirectory, "model.gguf")));
            File.Delete(Path.Combine(card.InstallDirectory, "model.gguf"));
            Assert.IsFalse(await preparation.PrepareAsync(library.Load(card.ModelArtifactId)!, false, null, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => preparation.PrepareAsync(card, true, null, cancelled.Token));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task FilesPreparedForLocalizerDoNotPretendThatRuntimeWasTested()
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-localizer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "model"));
        try
        {
            using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
            var library = new ManagedModelLibraryStore(Path.Combine(root, "library")); byte[] content = [1, 2];
            var card = new ManagedModelArtifactCard { ModelArtifactId = "future-localizer", Role = ManagedModelRoles.Localizer, IsManaged = true,
                ModelsRoot = root, InstallDirectory = Path.Combine(root, "model"), Files = [new() { RelativePath = "model.gguf", SizeBytes = 2, Sha256 = Convert.ToHexString(SHA256.HashData(content)) }] };
            File.WriteAllBytes(Path.Combine(card.InstallDirectory, "model.gguf"), content);
            using var preparation = new ReaderTextPreparation(library);
            Assert.IsTrue(await preparation.PrepareAsync(card, false, null, default));
            var stored = library.Load(card.ModelArtifactId)!; Assert.IsNull(stored.RuntimeVerifiedAt);
            Assert.AreEqual(ManagedModelStatuses.NeedsVerification, stored.Status);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Bytes(byte[] bytes) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }
}
