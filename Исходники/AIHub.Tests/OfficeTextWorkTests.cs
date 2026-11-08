using System.IO;
using System.IO.Compression;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeTextWorkTests
{
    [TestMethod]
    public async Task BlankIsAnOpenableProjectBeforeInputAndCanBeDiscarded()
    {
        var work = await OfficeTextWork.OpenAsync(null, CancellationToken.None);
        var draft = work.TemporaryProjectPath;
        try
        {
            Assert.IsFalse(work.IsDirty);
            var extracted = OfficePackage.Extract(draft, Path.Combine(Path.GetDirectoryName(draft)!, "reader"));
            CollectionAssert.AreEqual(work.Document, File.ReadAllBytes(extracted));
        }
        finally { work.Close(true); }
        Assert.IsFalse(File.Exists(draft));
    }

    [TestMethod]
    public async Task PartialSaveRetainsActualAiAndMaterialThroughRecoveryAndProjectSave()
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var source = Path.Combine(temp.Path, "example.docx");
        var material = Path.Combine(temp.Path, "notes.txt");
        var project = Path.Combine(temp.Path, "result.LOPATAdoxAI");
        var initial = OfficeTextFormats.PlainDocument("Before");
        var edited = OfficeTextFormats.PlainDocument("Переписано");
        File.WriteAllBytes(source, initial);
        File.WriteAllText(material, "Приложение");
        var work = await OfficeTextWork.OpenAsync(source, token);
        var id = work.DraftId;
        try
        {
            await work.CheckpointAsync(edited, token);
            await work.AddProjectReasonAsync("Speech", token, new OutputModelUse("m", "name", "rev", "voice"));
            await work.AddMaterialAsync(material, token);
            await work.SaveAsync(source, token);
            Assert.IsTrue(work.IsDirty);
            Assert.IsTrue(work.NeedsProject);
            CollectionAssert.AreEqual(initial, File.ReadAllBytes(source + ".bak"));
        }
        finally { work.Close(false); }
        work = await OfficeTextWork.OpenAsync(source, token, id);
        try
        {
            var savedMaterial = work.Materials.Single();
            Assert.AreEqual("notes.txt", savedMaterial.Name);
            var extracted = Path.Combine(temp.Path, "extracted.txt");
            await work.ExportMaterialAsync(savedMaterial, extracted, token);
            CollectionAssert.AreEqual(File.ReadAllBytes(material), File.ReadAllBytes(extracted));
            await work.SaveAsync(project, token);
            Assert.IsFalse(work.IsDirty);
            Assert.IsFalse(work.NeedsProject);
        }
        finally { work.Close(true); }
        var info = OfficeTextPackage.Inspect(project);
        Assert.AreEqual("m", info.AiActions.Single().Model?.Id);
        using var zip = ZipFile.OpenRead(project);
        using var reader = new StreamReader(zip.GetEntry(info.Materials.Single().Entry)!.Open());
        Assert.AreEqual("Приложение", reader.ReadToEnd());
    }

    [TestMethod]
    public async Task RecoveryNeverOverwritesSourceChangedOutsideTheSession()
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var source = Path.Combine(temp.Path, "document.docx");
        File.WriteAllBytes(source, OfficeTextFormats.PlainDocument("before"));
        var work = await OfficeTextWork.OpenAsync(source, token);
        var id = work.DraftId;
        try { await work.CheckpointAsync(OfficeTextFormats.PlainDocument("draft"), token); }
        finally { work.Close(false); }
        var external = OfficeTextFormats.PlainDocument("other window");
        File.WriteAllBytes(source, external);
        work = await OfficeTextWork.OpenAsync(source, token, id);
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => work.SaveAsync(source, token));
            CollectionAssert.AreEqual(external, File.ReadAllBytes(source));
            Assert.IsTrue(work.IsDirty);
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task UnsupportedOriginalIsProtectedEvenAfterRecoveryFromSimplifiedDraft()
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var path = Path.Combine(temp.Path, "complex.docx");
        File.WriteAllBytes(path, OfficeTextFormats.PlainDocument("Protected"));
        using (var doc = WordprocessingDocument.Open(path, true))
            doc.MainDocumentPart!.Document!.Body!.PrependChild(new W.SdtBlock(new W.SdtContentBlock(new W.Paragraph())));
        var original = File.ReadAllBytes(path);
        var work = await OfficeTextWork.OpenAsync(path, token);
        var id = work.DraftId;
        try
        {
            Assert.IsTrue(work.SourceNeedsCopy);
            await work.CheckpointAsync(OfficeTextFormats.PlainDocument("Text only"), token);
        }
        finally { work.Close(false); }
        work = await OfficeTextWork.OpenAsync(path, token, id);
        try
        {
            Assert.IsTrue(work.SourceNeedsCopy);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => work.SaveAsync(path, token));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            var project = Path.Combine(temp.Path, "safe-copy.LOPATAdoxAI");
            await work.SaveAsync(project, token);
            Assert.IsFalse(work.IsDirty);
            var kept = work.OriginalMaterial;
            Assert.IsNotNull(kept);
            var copy = Path.Combine(temp.Path, "exact-original.docx");
            await work.ExportMaterialAsync(kept, copy, token);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(copy));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task TextOnlySaveUsesVisibleUnicodeTextAndPreservesBlankLinesOnReopen()
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var path = Path.Combine(temp.Path, "draft.txt");
        File.WriteAllText(path, "Before");
        var body = "\r\nРусский\tEnglish 😊\r\n\r\n";
        var work = await OfficeTextWork.OpenAsync(path, token);
        try
        {
            await work.CheckpointAsync(OfficeTextFormats.PlainDocument(body), token);
            await work.SaveAsync(path, token);
            var text = File.ReadAllText(path);
            StringAssert.StartsWith(text, "[LOPATA.Provenance=");
            StringAssert.EndsWith(text, "\n\n" + body);
        }
        finally { work.Close(true); }
        work = await OfficeTextWork.OpenAsync(path, token);
        try
        {
            using var document = WordprocessingDocument.Open(new MemoryStream(work.Document), false);
            var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ToArray();
            Assert.AreEqual(4, paragraphs.Length);
            Assert.AreEqual("", paragraphs[0].InnerText);
            Assert.AreEqual("РусскийEnglish 😊", paragraphs[1].InnerText);
            Assert.AreEqual(1, paragraphs[1].Descendants<W.TabChar>().Count());
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task SaveAsCannotClobberAnExternallyChangedOriginalAfterSwitchingTargets()
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var a = Path.Combine(temp.Path, "A.docx");
        var b = Path.Combine(temp.Path, "B.docx");
        File.WriteAllBytes(a, OfficeTextFormats.PlainDocument("A"));
        var work = await OfficeTextWork.OpenAsync(a, token);
        try
        {
            await work.SaveAsync(b, token);
            var external = OfficeTextFormats.PlainDocument("Edited externally");
            File.WriteAllBytes(a, external);
            await Assert.ThrowsExactlyAsync<IOException>(() => work.SaveAsync(a, token));
            CollectionAssert.AreEqual(external, File.ReadAllBytes(a));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task AProjectWithAProtectedPrimaryCanSaveAndContinueInASafeProjectCopy()
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var payload = Path.Combine(temp.Path, "complex.docx");
        File.WriteAllBytes(payload, OfficeTextFormats.PlainDocument("Protected"));
        using (var doc = WordprocessingDocument.Open(payload, true))
            doc.MainDocumentPart!.Document!.Body!.PrependChild(new W.SdtBlock(new W.SdtContentBlock(new W.Paragraph())));
        var originalProject = Path.Combine(temp.Path, "original.LOPATAdoxAI");
        OfficeTextPackage.Store(originalProject, File.ReadAllBytes(payload), new OfficeTextManifest(), null);
        var copy = Path.Combine(temp.Path, "edited.LOPATAdoxAI");
        var before = File.ReadAllBytes(originalProject);
        var work = await OfficeTextWork.OpenAsync(originalProject, token);
        try
        {
            Assert.IsTrue(work.SourceNeedsCopy);
            await work.CheckpointAsync(OfficeTextFormats.PlainDocument("edited"), token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => work.SaveAsync(originalProject, token));
            await work.SaveAsync(copy, token);
            Assert.IsFalse(work.IsDirty);
            await work.CheckpointAsync(OfficeTextFormats.PlainDocument("final"), token);
            await work.SaveAsync(copy, token);
            Assert.IsFalse(work.IsDirty);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(originalProject));
        }
        finally { work.Close(true); }
        work = await OfficeTextWork.OpenAsync(copy, token);
        try
        {
            var source = work.OriginalMaterial;
            Assert.IsNotNull(source);
            var extracted = Path.Combine(temp.Path, "retained.docx");
            await work.ExportMaterialAsync(source, extracted, token);
            CollectionAssert.AreEqual(File.ReadAllBytes(payload), File.ReadAllBytes(extracted));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    [DataRow(".txt", true)]
    [DataRow(".md", true)]
    [DataRow(".txt", false)]
    [DataRow(".md", false)]
    public async Task RecoveredFormatCheckUsesTheOriginalBaselineOrRetainsTheUnresolvedWarning(string ext, bool storedBaseline)
    {
        var token = CancellationToken.None;
        using var temp = new TemporaryOfficeDirectory();
        var file = Path.Combine(temp.Path, "draft" + ext);
        File.WriteAllText(file, "plain");
        // Start with an ordinary source, then checkpoint richer editor state.
        // For Markdown, build the fixture from an earlier private draft to keep
        // the regression independent of a LibreOffice installation.
        var seed = Path.Combine(temp.Path, "seed.txt");
        File.WriteAllText(seed, "plain");
        var work = await OfficeTextWork.OpenAsync(ext == ".txt" ? file : seed, token);
        var id = work.DraftId;
        var info = Path.Combine(Path.GetDirectoryName(work.TemporaryProjectPath)!, "draft.json");
        const string signature = """{"version":1,"plain":{"properties":["section"],"counted":[]},"markdown":{"properties":["section"],"counted":[]}}""";
        try
        {
            if (storedBaseline) work.SetCompatibilityBaseline(signature);
            await work.CheckpointAsync(OfficeTextFormats.PlainDocument("revised"), token);
            await work.SetFormatReasonAsync("Formatting needs the project", token);
        }
        finally { work.Close(false); }
        if (ext == ".md")
        {
            var draft = System.Text.Json.JsonSerializer.Deserialize<EditorDraftInfo>(File.ReadAllText(info))!;
            File.WriteAllText(info, System.Text.Json.JsonSerializer.Serialize(draft with { Source = file, Target = file }));
            if (!storedBaseline)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(info)!, "format-baseline.json"),
                    """{"version":1,"plain":{"properties":[],"counted":[["tbl",-1]]},"markdown":{"properties":[],"counted":[]}}""");
        }
        work = await OfficeTextWork.OpenAsync(file, token, id);
        try
        {
            Assert.AreEqual(storedBaseline ? signature : null, work.CompatibilityBaseline);
            Assert.AreEqual(!storedBaseline, work.CompatibilityBaselineUnavailable);
            Assert.IsTrue(work.NeedsProject);
            await work.CheckpointAsync(OfficeTextFormats.PlainDocument("another edit"), token);
            if (!storedBaseline)
                await work.SetFormatReasonAsync(null, token); // False 'compatible' from old restored surface.
            Assert.IsTrue(work.NeedsProject);
            if (ext == ".txt")
            {
                await work.SaveAsync(file, token);
                Assert.IsTrue(work.IsDirty);
                Assert.IsTrue(work.NeedsProject);
            }
        }
        finally { work.Close(true); }
    }

    private sealed class TemporaryOfficeDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("lopata-office-work-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
