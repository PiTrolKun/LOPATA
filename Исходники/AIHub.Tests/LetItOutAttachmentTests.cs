using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutAttachmentTests
{
    [TestMethod]
    public async Task WordAndPdfUseTextLayerAndEmptyPdfDoesNotPretendToBeRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-attachment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "chat.db")); var id = Guid.NewGuid().ToString("N"); store.Create(id);
            var word = Path.Combine(root, "note.docx");
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(word, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var part = doc.AddMainDocumentPart(); part.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Обсудить отпуск в декабре."))))); part.Document.Save();
            }
            var a = await store.ImportAttachmentAsync(id, word, default); StringAssert.Contains(a.Preview, "отпуск в декабре");
            using var pdf = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); var page = pdf.AddPage(600, 800);
            var font = pdf.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            page.AddText("Appointment at 18:30", 12, new UglyToad.PdfPig.Core.PdfPoint(30, 700), font);
            var path = Path.Combine(root, "note.pdf"); File.WriteAllBytes(path, pdf.Build());
            var b = await store.ImportAttachmentAsync(id, path, default); StringAssert.Contains(b.Preview, "18:30");
            using var empty = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); empty.AddPage(600, 800);
            var scan = Path.Combine(root, "no-text.pdf"); File.WriteAllBytes(scan, empty.Build());
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAttachmentAsync(id, scan, default));
            Assert.AreEqual("LetItOut.Attachments.NoText", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void VisionModuleKeepsGpuReserveAndBudgetsItsCpuMemorySeparately()
    {
        const long gib = 1024L * 1024 * 1024;
        var policy = new LetItOutMemoryPolicy(12 * gib, gib, [2 * gib, 2 * gib]) { ExtraHostBytes = 3 * gib };
        var without = policy with { ExtraHostBytes = 0 };
        foreach (var gpu in new RuntimeDevice?[] { new("Vulkan1", "AMD fixture", 24 * gib, 20 * gib), new("Vulkan1", "AMD fixture", 12 * gib, 10 * gib), null })
        {
            var a = policy.Choose(gpu); var b = without.Choose(gpu);
            Assert.AreEqual(b.Mode, a.Mode); Assert.AreEqual(b.GpuBytes, a.GpuBytes); Assert.AreEqual(b.HostBytes + 3 * gib, a.HostBytes);
            if (gpu is not null && a.Mode != "Cpu") Assert.IsTrue(a.GpuBytes + 2 * gib <= gpu.FreeBytes);
        }
        var card = ManagedModelCatalog.CreateLetItOutVision(Path.GetTempPath());
        Assert.AreEqual(ManagedModelCatalog.LetItOutRevision, card.Revision); Assert.AreEqual(1_193_058_784, card.Files.Single().SizeBytes);
        Assert.IsTrue(new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(Path.GetTempPath(), "unused-license-receipt.json")).Entries.Any(e => e.Id == card.ModelArtifactId));
    }

    [TestMethod]
    public async Task CopiesSurviveExternalMoveAreScopedAndRemainBoundToOriginalPairs()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-attachment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "db", "chat.db")); var id = Guid.NewGuid().ToString("N"); store.Create(id);
            var source = Path.Combine(root, "Личная заметка.txt"); var text = "Я злюсь из-за сверхурочной работы, но хочу сохранить время для музыки.";
            await File.WriteAllTextAsync(source, text);
            var a = await store.ImportAttachmentAsync(id, source, default); var original = await File.ReadAllBytesAsync(source);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(store.AttachmentPath(id, a)));
            store.SaveDraftAttachments(id, [a.Id]); File.Move(source, source + ".moved");
            var reopened = new LetItOutConversationStore(store.DatabasePath);
            Assert.AreEqual(a.Id, reopened.Load(id)!.DraftAttachments!.Single());
            using var page = JsonDocument.Parse(reopened.ReadAttachmentPage(id, a.Id, 0, 300, default));
            Assert.AreEqual(text, page.RootElement.GetProperty("content").GetString());
            var history = new[] { new LetItOutSavedMessage("user", "Посмотри мою заметку", Attachments: [a.Id]), new("assistant", "Что в этом беспокоит сильнее?") };
            store.SaveContent(id, history, "Следующий вопрос"); store.SaveDraftAttachments(id, []);
            Assert.AreEqual(a.Id, store.MemoryPairs(id).Single().Attachments!.Single().Id);
            StringAssert.Contains(store.ReadMemoryIndex(id), "attachment://" + id + "/" + a.Id);
            Assert.ThrowsExactly<InvalidDataException>(() => store.SaveContent(id, [history[0] with { Attachments = [] }, history[1]], ""));
            var other = Guid.NewGuid().ToString("N"); store.Create(other);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Attachment(other, a.Id));
            Assert.ThrowsExactly<InvalidDataException>(() => store.SaveContent(other, history, ""));
            Assert.ThrowsExactly<InvalidDataException>(() => store.Attachment(id, "../../outside"));
            Assert.IsTrue(store.Delete(id)); Assert.IsFalse(File.Exists(store.AttachmentPath(id, a))); Assert.IsTrue(File.Exists(source + ".moved"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LargeMaterialIsPagedSearchableAndUnsupportedOrEmptyFilesAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-attachment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "chat.db")); var id = Guid.NewGuid().ToString("N"); store.Create(id);
            var path = Path.Combine(root, "События.md");
            await File.WriteAllTextAsync(path, new string('я', 28000) + "\nШкафчик: код ZETA-4827.\n" + new string('я', 22000));
            var a = await store.ImportAttachmentAsync(id, path, default);
            using var first = JsonDocument.Parse(store.ReadAttachmentPage(id, a.Id, 0, 1000, default));
            Assert.IsTrue(first.RootElement.GetProperty("has_more").GetBoolean());
            Assert.IsFalse(first.RootElement.GetProperty("content").GetString()!.Contains("ZETA"));
            using var found = JsonDocument.Parse(store.SearchAttachment(id, a.Id, "Шкафчик", default));
            StringAssert.Contains(found.RootElement.GetProperty("content").GetString()!, "ZETA-4827");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => store.ImportAttachmentAsync(id, path, canceled.Token));
            await File.WriteAllTextAsync(Path.Combine(root, "bad.exe"), "text");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAttachmentAsync(id, Path.Combine(root, "bad.exe"), default));
            await File.WriteAllTextAsync(Path.Combine(root, "empty.txt"), "");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAttachmentAsync(id, Path.Combine(root, "empty.txt"), default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SnapshotFreezesAttachmentIdsAndImageBodiesNeverEnterCheckpoint()
    {
        var ids = new List<string> { Guid.NewGuid().ToString("N") };
        var request = new LetItOutChatRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "C:\\test.gguf",
            [new("user", "", ids, ["data:image/png;base64,private"])], "ru");
        var frozen = LetItOutChatWork.Snapshot(request); ids.Clear();
        Assert.HasCount(1, frozen.Messages[0].Attachments!); Assert.IsNull(frozen.Messages[0].ImageInputs);
        Assert.IsFalse(JsonSerializer.Serialize(request).Contains("base64"));
        var payload = LetItOutChatRuntime.BuildPayload(request);
        using var parsed = JsonDocument.Parse(payload); Assert.AreEqual(1088, LlamaServerRuntimeService.ImageTokenReserve(parsed.RootElement));
        Assert.IsTrue(payload.Contains("image_url"));
    }
}
