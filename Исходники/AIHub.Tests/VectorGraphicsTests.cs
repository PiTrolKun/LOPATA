using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorGraphics;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorGraphicsTests
{
    private const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'><defs><clipPath id='c'><circle cx='100' cy='50' r='38'/></clipPath><filter id='glow'><feGaussianBlur stdDeviation='2'/></filter><linearGradient id='g'><stop stop-color='green'/><stop offset='1' stop-color='black'/></linearGradient></defs><rect width='200' height='100' fill='black'/><g clip-path='url(#c)'><rect width='200' height='100' fill='url(#g)'/><text x='70' y='50' fill='white' font-family='monospace'>Кот 01</text></g><circle cx='20' cy='20' r='8' fill='green' filter='url(#glow)'/></svg>";

    [TestMethod]
    public void StaticIllustrationKeepsTextClipGradientFilterAndPublicMetadata()
    {
        var original = VectorSvg.Extract("```svg\n" + Svg + "\n```");
        var facts = new OutputProvenance { Scenario = "vector/test", ModelHistory = "recorded", Models = [new("fixture", "Qwen fixture", "revision", "SVG generation")] };
        var tagged = VectorSvg.WithProvenance(original, facts, "Автор <&>");
        var document = VectorSvg.Parse(tagged); XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.AreEqual("Кот 01", document.Descendants(ns + "text").Single().Value);
        Assert.AreEqual(1, document.Descendants(ns + "clipPath").Count());
        Assert.AreEqual(1, document.Descendants(ns + "feGaussianBlur").Count());
        StringAssert.Contains(document.Descendants(ns + "metadata").Single().Value, "revision");
        Assert.AreEqual("Author: Автор <&>", document.Descendants(ns + "desc").Single().Value);
    }

    [TestMethod]
    public void DangerousAndIncompleteDocumentsNeverBecomePreviewableSvg()
    {
        foreach (var content in new[] {
            "<script>alert(1)</script>", "<foreignObject/>", "<image href='https://example.com/a.png'/>",
            "<use href='#c'/>", "<animate attributeName='x'/>", "<style>@import url(https://example.com);</style>",
            "<rect onload='run()'/>", "<rect fill='url(https://example.com/a.svg#x)'/>",
            "<rect style='fill:u&#114;l(file:///C:/test)'/>", "<g xml:base='file:///C:/test'/>",
            "<rect style='fill:u\\72l(https://example.com)'/>" })
        {
            Assert.Throws<InvalidDataException>(() => VectorSvg.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'>" + content + "</svg>"), content);
        }
        Assert.Throws<InvalidDataException>(() => VectorSvg.Parse("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///C:/test'>]>" + Svg));
        Assert.Throws<InvalidDataException>(() => VectorSvg.Extract("<svg xmlns='http://www.w3.org/2000/svg'>"));
        Assert.Throws<InvalidDataException>(() => VectorSvg.Parse(Svg.Replace("0 0 200 100", "0 0 NaN 100")));
    }

    [TestMethod]
    public void OnlyExactTextClosingTypoIsCorrectedAndAllSafetyChecksStillApply()
    {
        var malformed = Svg.Replace("Кот 01</text>", "<tspan>8</span></text>");
        Assert.Throws<InvalidDataException>(() => VectorSvg.Extract(malformed));
        var prepared = VectorSvg.Prepare(malformed);
        Assert.AreEqual(1, prepared.SyntaxCorrections);
        StringAssert.Contains(prepared.Svg, "<tspan>8</tspan>");
        Assert.Throws<InvalidDataException>(() => VectorSvg.Prepare(malformed.Replace("</svg>", "<script/></svg>")));
        Assert.Throws<InvalidDataException>(() => VectorSvg.Prepare(malformed.Replace("</g>", "</wrong>")));
        Assert.AreEqual(0, VectorSvg.Prepare(Svg).SyntaxCorrections);
    }

    [TestMethod]
    public void SavedContentFailureReleasesPendingUiStateButDoesNotDiscardOtherFailures()
    {
        var id = Guid.NewGuid().ToString("N"); var folder = VectorStore.Folder(id);
        try
        {
            var request = new VectorRequest(id, "fixture", folder, null, DateTimeOffset.UtcNow);
            VectorStore.Save(new(request, "failed", "Vector.Error.InvalidSvg"));
            var store = new BackgroundOperationStore(Path.Combine(folder, "background", "operation.json"));
            store.Save(new() { Kind = VectorStore.BackgroundKind, Title = "fixture", Project = id,
                Input = JsonSerializer.SerializeToElement(request), Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true });
            var controller = new BackgroundOperationController(store); controller.Load();
            Assert.IsTrue(controller.HasPending);
            VectorWork.RecoverFailedContent(controller);
            Assert.IsFalse(controller.HasPending); Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase);
        }
        finally
        {
            var resolved = Path.GetFullPath(folder); var root = Path.GetFullPath(VectorStore.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != id) throw new IOException("Unsafe fixture cleanup");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    [TestMethod]
    public async Task CompletedModelStageResumesThroughActualRendererWithoutReinferenceOrOverwrite()
    {
        var id = Guid.NewGuid().ToString("N");
        var folder = VectorStore.Folder(id); var output = Path.Combine(folder, "export");
        var request = new VectorRequest(id, "fixture input", output, "fixture author", DateTimeOffset.UtcNow);
        var facts = new OutputProvenance { Scenario = "vector/test" };
        var auditStore = Path.Combine(folder, "audit-fixture");
        using var policy = ComponentAudit.UseTestStore(auditStore, false);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var requestedTerms = new List<string>();
        // No user receipts are created: this test substitutes the dialog boundary only.
        ComponentLicenseGate.ConfirmAsync = (ids, _) => { requestedTerms.AddRange(ids); return Task.CompletedTask; };
        try
        {
            VectorStore.Save(new(request, "generated", Provenance: facts));
            var answerPath = VectorStore.FilePath(id, "answer.txt"); await File.WriteAllTextAsync(answerPath, Svg);
            var stages = new List<string>(); using var work = new VectorWork();
            using (var interrupted = new CancellationTokenSource())
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => work.RunAsync(request,
                    (stage, _) => { if (stage == "Rendering") interrupted.Cancel(); }, interrupted.Token));
                Assert.AreEqual("waiting", VectorStore.Load(id).Phase);
                Assert.AreEqual(Svg, await File.ReadAllTextAsync(answerPath));
            }
            var exported = await work.RunAsync(request, (stage, _) => stages.Add(stage), CancellationToken.None);
            Assert.IsFalse(stages.Contains("Generating"), "A committed model answer must not trigger inference.");
            Assert.IsTrue(requestedTerms.Contains(VectorRenderer.TermsId));
            var png = await File.ReadAllBytesAsync(VectorStore.FilePath(id, "preview.png"));
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            Assert.IsTrue(png.Length > 1000); StringAssert.Contains(Encoding.UTF8.GetString(png), "LOPATA");
            Assert.AreEqual("completed", VectorStore.Load(id).Phase);
            var original = await File.ReadAllBytesAsync(exported);
            var binding = VectorStore.FilePath(id, "preview.source-sha256");
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), await File.ReadAllTextAsync(binding));
            File.Delete(binding);
            await work.RunAsync(request, (stage, _) => stages.Add(stage), CancellationToken.None);
            Assert.IsFalse(stages.Contains("Generating")); CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(exported));
            Assert.AreEqual(1, Directory.GetFiles(output, "*.svg").Length);
            Assert.AreEqual(Svg, await File.ReadAllTextAsync(answerPath));
        }
        finally
        {
            ComponentLicenseGate.ConfirmAsync = previousGate;
            var resolved = Path.GetFullPath(folder); var root = Path.GetFullPath(VectorStore.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != id) throw new IOException("Unsafe fixture cleanup");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
