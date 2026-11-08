using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeNativeSectionStorageTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [TestMethod]
    [DataRow(".epub")]
    [DataRow(".fb2")]
    [DataRow(".pptx")]
    [DataRow(".odp")]
    public async Task OrdinarySaveProjectSecondaryExportAndRecoveryKeepNativePayload(string extension)
    {
        using var files = new Files();
        var source = files.File("Первый" + extension);
        var original = OfficeNativeDocument.Create(extension, [new() { Title = "Оригинал", Body = "Текст оригинала" }]);
        await File.WriteAllBytesAsync(source, original);
        var target = files.File("Проект.LOPATAdoxAI");
        const string initial = "Первая подтверждённая правка";
        var work = await OfficeTextWork.OpenAsync(source, Token);
        try
        {
            var primary = work.Sheets[0];
            Assert.AreEqual(extension, work.NativeSectionExtension(primary.Id));
            Assert.IsTrue(await work.CanEditNativeSheetAsync(primary.Id, Token));
            Assert.IsFalse(work.SourceNeedsCopy);
            Assert.IsFalse(work.NeedsProject);
            var edit = Edited(original, extension, initial);
            await work.WriteNativeSectionAsync(primary.Id, edit, Token);
            Assert.IsTrue(work.IsDirty);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            await work.SaveAsync(source, Token);
            Assert.IsFalse(work.IsDirty);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source + ".bak"));
            Assert.IsTrue(Contains(File.ReadAllBytes(source), extension, initial));
            StringAssert.Contains(Metadata(source, extension), "office/editor");

            var secondary = await work.AddSheetFromFileAsync(source, Token);
            const string other = "Изменение во втором материале";
            await work.WriteNativeSectionAsync(secondary.Id, Edited(await work.ReadNativeSectionAsync(secondary.Id, Token), extension, other), Token);
            var model = new OutputModelUse("test-model", "Actual test model", "rev-42", "review");
            await work.UpsertAiEntryAsync(new("native-proof", secondary.Id, DateTimeOffset.UtcNow, "review",
                "private user request", "private system message", "private result", "completed", [model]), Token);
            var copy = files.File("Второй" + extension);
            await work.ExportNativeSectionAsync(secondary.Id, copy, Token);
            var stamped = Metadata(copy, extension);
            StringAssert.Contains(stamped, "test-model");
            Assert.IsFalse(stamped.Contains("private", StringComparison.Ordinal));
            Assert.IsTrue(Contains(File.ReadAllBytes(copy), extension, other));
            await work.SaveAsync(source, Token);
            Assert.IsTrue(work.NeedsProject);
            Assert.IsFalse(Metadata(source, extension).Contains("test-model", StringComparison.Ordinal));
            var draft = work.DraftId;
            work.Close(false);
            work = await OfficeTextWork.OpenAsync(source, Token, draft);
            Assert.IsTrue(Contains(await work.ReadNativeSectionAsync(secondary.Id, Token), extension, other));
            await work.SaveAsync(target, Token);
            Assert.IsFalse(work.NeedsProject);
            work.Close(true);
            work = await OfficeTextWork.OpenAsync(target, Token);
            Assert.IsTrue(Contains(await work.ReadNativeSectionAsync(primary.Id, Token), extension, initial));
            Assert.IsTrue(Contains(await work.ReadNativeSectionAsync(secondary.Id, Token), extension, other));
            var extracted = files.File("original" + extension);
            await work.ExportMaterialAsync(work.Sheets[0].Files.Single(), extracted, Token);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(extracted));
            var readerFile = OfficePackage.Extract(target, files.File("reader"));
            Assert.AreEqual(extension, Path.GetExtension(readerFile));
            Assert.IsTrue(Contains(File.ReadAllBytes(readerFile), extension, initial));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    [DataRow(".epub")]
    [DataRow(".fb2")]
    [DataRow(".pptx")]
    [DataRow(".odp")]
    public async Task ExistingSectionDraftExportsMatchingNativeFormat(string extension)
    {
        using var files = new Files();
        var kind = OfficeTextWork.ClassifySource("draft" + extension);
        var work = await OfficeTextWork.OpenAsync(null, Token, firstKind: kind);
        try
        {
            var sections = new OfficeSectionDraft(1, [new() { Title = "Тема", Body = "Уже введённый текст" }]);
            await work.WriteSheetAsync(OfficeSheets.PrimaryId, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sections), Token);
            var output = files.File("Черновик" + extension);
            await work.ExportNativeSectionAsync(OfficeSheets.PrimaryId, output, Token);
            Assert.IsNull(work.TargetPath);
            Assert.IsTrue(work.IsDirty);
            Assert.IsTrue(Contains(File.ReadAllBytes(output), extension, "Уже введённый текст"));
            StringAssert.Contains(Metadata(output, extension), "office/editor");
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task UnsupportedInputRemainsExtractableAndCannotBeOverwrittenWithReplacementBytes()
    {
        using var files = new Files();
        var source = files.File("opaque.pptx");
        byte[] opaque = [1, 4, 2, 255];
        File.WriteAllBytes(source, opaque);
        var work = await OfficeTextWork.OpenAsync(source, Token);
        try
        {
            Assert.IsTrue(work.SourceNeedsCopy);
            var replacement = OfficeNativeDocument.Create(".pptx", [new() { Body = "Replacement" }]);
            await Assert.ThrowsAsync<InvalidDataException>(() => work.WriteNativeSectionAsync(OfficeSheets.PrimaryId, replacement, Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => work.SaveAsync(source, Token));
            var project = files.File("safe.LOPATAdoxAI");
            await work.SaveAsync(project, Token);
            var output = files.File("attachment.pptx");
            await work.ExportMaterialAsync(work.Sheets[0].Files.Single(), output, Token);
            CollectionAssert.AreEqual(opaque, File.ReadAllBytes(output));
            CollectionAssert.AreEqual(opaque, File.ReadAllBytes(source));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task InvalidEmbeddedProvenanceDisablesEditingBeforeAndAfterProjectPromotion()
    {
        using var files = new Files();
        var source = files.File("invalid-provenance.fb2");
        var valid = OfficeNativeDocument.Create(".fb2", [new() { Title = "Глава", Body = "Содержимое" }]);
        using var input = new MemoryStream(valid);
        var book = XDocument.Load(input);
        var fb = book.Root!.Name.Namespace;
        book.Root.Element(fb + "description")!.Add(new XElement(fb + "custom-info",
            new XAttribute("info-type", "LOPATA.Provenance"), "{incomplete"));
        book.Save(source);
        var original = await File.ReadAllBytesAsync(source);
        Assert.IsTrue(OfficeNativeDocument.Open(original, ".fb2").HasEditableText);
        var work = await OfficeTextWork.OpenAsync(source, Token);
        try
        {
            Assert.IsTrue(work.SourceNeedsCopy);
            Assert.IsFalse(await work.CanEditNativeSheetAsync(OfficeSheets.PrimaryId, Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => work.SaveAsync(source, Token));
            var project = files.File("stored.LOPATAdoxAI");
            await work.SaveAsync(project, Token);
            work.Close(true);
            work = await OfficeTextWork.OpenAsync(project, Token);
            Assert.IsFalse(await work.CanEditNativeSheetAsync(OfficeSheets.PrimaryId, Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => work.WriteNativeSectionAsync(OfficeSheets.PrimaryId, valid, Token));
            CollectionAssert.AreEqual(original, await work.ReadNativeSectionAsync(OfficeSheets.PrimaryId, Token));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(source));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task SignedAttachmentStaysProtectedWhenTheProjectIsReopened()
    {
        using var files = new Files();
        var source = files.File("signed.epub");
        var unsigned = OfficeNativeDocument.Create(".epub", [new() { Title = "Signed source", Body = "Original" }]);
        await File.WriteAllBytesAsync(source, unsigned);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        using (var stream = archive.CreateEntry("META-INF/signatures.xml").Open())
            stream.Write("<signatures/>"u8);
        var original = await File.ReadAllBytesAsync(source);
        var work = await OfficeTextWork.OpenAsync(source, Token);
        try
        {
            Assert.IsTrue(work.SourceNeedsCopy);
            var project = files.File("stored.LOPATAdoxAI");
            await work.SaveAsync(project, Token);
            work.Close(true);
            work = await OfficeTextWork.OpenAsync(project, Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => work.WriteNativeSectionAsync(OfficeSheets.PrimaryId, unsigned, Token));
            var originalCopy = files.File("exact.epub");
            await work.ExportMaterialAsync(work.Sheets[0].Files.Single(), originalCopy, Token);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(originalCopy));
        }
        finally { work.Close(true); }
    }

    private static byte[] Edited(byte[] source, string extension, string replacement)
    {
        var doc = OfficeNativeDocument.Open(source, extension);
        var block = doc.Pages.SelectMany(p => p.Blocks).First(b => b.Text.Length != 0);
        block.SetText(replacement);
        return doc.Save();
    }

    private static bool Contains(byte[] bytes, string extension, string value) => OfficeNativeDocument.Open(bytes, extension)
        .Pages.SelectMany(page => page.Blocks).Any(block => block.Text.Contains(value, StringComparison.Ordinal));

    private static string Metadata(string path, string extension)
    {
        if (extension != ".pptx") return AIHub.Services.Books.BookOutputMetadata.ReadNativeProvenance(path, extension) ?? "";
        using var presentation = PresentationDocument.Open(path, false);
        return presentation.CustomFilePropertiesPart!.Properties!.Elements<CustomDocumentProperty>()
            .Single(e => e.Name == "LOPATA.Provenance").InnerText;
    }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("lopata-native-sheets-").FullName;
        internal string File(string name) => Path.Combine(_directory, name);
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
