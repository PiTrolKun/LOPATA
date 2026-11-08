using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeEditorTests
{
    [TestMethod]
    public void NativePackageKeepsExactRichPayloadAndPublicMetadata()
    {
        var root = Temporary(); var odt = Path.Combine(root, "original.odt");
        using (var archive = ZipFile.Open(odt, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("content.xml").Open())) writer.Write("<document>Привет <b>жирный</b></document>");
        var path = Path.Combine(root, "document.LOPATAdoxAI");
        OfficePackage.Save(path, odt, new OutputProvenance { Scenario = "office/editor", CreatedUtc = DateTimeOffset.UtcNow });
        var restored = OfficePackage.Extract(path, Path.Combine(root, "restored"));
        CollectionAssert.AreEqual(File.ReadAllBytes(odt), File.ReadAllBytes(restored));
        Assert.IsNotNull(ReaderFileRequest.Parse(["--reader", "--", path]));
        using (var container = ZipFile.OpenRead(path))
        using (var text = new StreamReader(container.GetEntry("manifest.json")!.Open()))
        { var metadata = text.ReadToEnd(); StringAssert.Contains(metadata, "LOPATA"); Assert.IsFalse(metadata.Contains(root)); }
        OfficePackage.Save(path, odt, new OutputProvenance { Scenario = "office/editor" });
        Assert.IsTrue(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public void ForeignZipAndDuplicatePayloadCannotMasqueradeAsNativeDocument()
    {
        foreach (var duplicate in new[] { false, true })
        {
            var root = Temporary(); var path = Path.Combine(root, "invalid.LOPATAdoxAI");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write("{\"Format\":\"LOPATAdoxAI\",\"Schema\":1,\"Kind\":\"text\"}");
                if (duplicate) { zip.CreateEntry("content/document.odt"); zip.CreateEntry("content/document.odt"); }
                else zip.CreateEntry("../content/document.odt");
            }
            Assert.ThrowsExactly<InvalidDataException>(() => OfficePackage.Extract(path, Path.Combine(root, "out")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "content")));
        }
    }

    [TestMethod]
    public void LeftShiftCaptureRejectsChordsAutorepeatAndInjectedEvents()
    {
        var trigger = new CaptureTrigger();
        Assert.IsFalse(trigger.Accept(0xA0, true, false, false));
        Assert.IsFalse(trigger.Accept(0xA0, true, false, false));
        Assert.IsTrue(trigger.Accept(0xA0, false, false, false));
        Assert.IsFalse(trigger.Accept(0xA0, false, false, false));
        trigger.Accept(0xA0, true, false, false); trigger.Accept(0x41, true, false, false);
        trigger.Accept(0xA0, true, false, false);
        Assert.IsFalse(trigger.Accept(0xA0, false, false, false));
        trigger.Accept(0xA0, true, false, true); Assert.IsFalse(trigger.Accept(0xA0, false, false, false));
        trigger.Accept(0xA0, true, true, false); Assert.IsFalse(trigger.Accept(0xA0, false, true, false));
        trigger.Accept(0xA1, true, false, false); Assert.IsFalse(trigger.Accept(0xA1, false, false, false));
    }

    [TestMethod]
    public void ClipboardHtmlKeepsRussianLinksButDropsScripts()
    {
        var fragment = CaptureHtml.Fragment("Version:1.0\r\n<!--StartFragment--><p onclick='bad()'><b>Привет</b><a href='https://example.org'>ссылка</a><script>bad()</script></p><!--EndFragment-->");
        Assert.IsNotNull(fragment); StringAssert.Contains(fragment, "Привет"); StringAssert.Contains(fragment, "https://example.org");
        Assert.IsFalse(fragment.Contains("onclick")); Assert.IsFalse(fragment.Contains("script"));
        Assert.IsNull(CaptureHtml.Fragment("old clipboard text"));
    }

    [TestMethod]
    public void IndependentPdfReaderSeesRealTextAndEmbeddedProvenance()
    {
        var result = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeEditor_20261108", "engine-result.json");
        if (!File.Exists(result)) Assert.Inconclusive("Run the explicit installed engine cycle first.");
        using var json = JsonDocument.Parse(File.ReadAllText(result));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(json.RootElement.GetProperty("work").GetString()!, "output.pdf"));
        StringAssert.Contains(pdf.Information.Subject, "LOPATA.Provenance=");
        StringAssert.Contains(pdf.GetPage(1).Text, "Проверка");
    }

    private static string Temporary()
    { var path = Path.Combine(Path.GetTempPath(), "LOPATA.OfficeEditor.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
