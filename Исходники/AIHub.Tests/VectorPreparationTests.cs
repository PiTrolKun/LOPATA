using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorGraphics;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorPreparationTests
{
    [TestMethod]
    public async Task EntryChecksRespectAuditProofAndReinstallationAndNeverHideMissingFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-vector-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fixture.bin"); await File.WriteAllTextAsync(path, "original");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var full = 0; var presence = 0;
        Task Verify(CancellationToken token) => ComponentAudit.RunAsync("fixture-svg", folder, "rev1",
            ct => { ct.ThrowIfCancellationRequested(); presence++; if (!File.Exists(path)) throw new FileNotFoundException(); return Task.CompletedTask; },
            async ct => { full++; if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct))) != hash) throw new InvalidDataException(); }, token);
        Task<string> Model(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult("fixture.gguf"); }
        try
        {
            var store = Path.Combine(folder, "audit");
            using (ComponentAudit.UseTestStore(store, false))
            {
                Assert.IsTrue((await VectorPreparation.CheckComponentsAsync(Verify, Model, CancellationToken.None)).Ready);
                Assert.AreEqual(1, full); Assert.AreEqual(0, presence); // First use remains full with audit off.
                await File.WriteAllTextAsync(path, "modified");
                Assert.IsTrue((await VectorPreparation.CheckComponentsAsync(Verify, Model, CancellationToken.None)).Ready);
                Assert.AreEqual(1, full); Assert.AreEqual(1, presence);
            }
            using (ComponentAudit.UseTestStore(store, true))
            {
                var failed = await VectorPreparation.CheckComponentsAsync(Verify, Model, CancellationToken.None);
                Assert.IsFalse(failed.RendererReady); Assert.IsTrue(failed.ModelReady); Assert.AreEqual(2, full);
            }
            await File.WriteAllTextAsync(path, "original");
            using (ComponentAudit.UseTestStore(store, false))
            {
                Assert.IsTrue((await VectorPreparation.CheckComponentsAsync(Verify, Model, CancellationToken.None)).Ready);
                ComponentAudit.Invalidate(folder);
                Assert.IsTrue((await VectorPreparation.CheckComponentsAsync(Verify, Model, CancellationToken.None)).Ready);
                Assert.AreEqual(4, full);
                File.Delete(path);
                Assert.IsFalse((await VectorPreparation.CheckComponentsAsync(Verify, Model, CancellationToken.None)).RendererReady);
                var missingModel = await VectorPreparation.CheckComponentsAsync(_ => Task.CompletedTask,
                    _ => throw new FileNotFoundException(), CancellationToken.None);
                Assert.IsTrue(missingModel.RendererReady); Assert.IsFalse(missingModel.ModelReady); Assert.IsFalse(missingModel.Ready);
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => VectorPreparation.CheckComponentsAsync(Verify, Model, canceled.Token));
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void ShippedExamplesKeepTheirRecordedBytesPromptsAndSvgMetadata()
    {
        using var stream = VectorExamples.Open("manifest.json"); using var json = JsonDocument.Parse(stream);
        Assert.AreEqual(2, VectorExamples.All.Count);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("Id").GetString()!;
            var example = VectorExamples.All.Single(x => x.Id == id);
            foreach (var file in item.GetProperty("Sha256").EnumerateObject())
            {
                using var bytes = VectorExamples.Open(id + file.Name);
                Assert.AreEqual(file.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            using var svg = VectorExamples.Open(id + ".svg"); using var reader = new StreamReader(svg);
            var text = reader.ReadToEnd(); _ = VectorSvg.Parse(text); StringAssert.Contains(text, "LOPATA");
            StringAssert.Contains(VectorExamples.Prompt(example), "кота");
        }
    }

    [TestMethod]
    public async Task ZoomFrameRendersVisibleSvgGeometryInsteadOfStretchingTheOldPng()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-svg-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(folder, "audit"), false);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            const string original = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' fill='black'/><rect x='50' y='0' width='.1' height='100' fill='white'/></svg>";
            var frame = VectorViewportFrame.Compose(original, 200, 100, 1, 20, -1900, -950);
            var parsed = VectorSvg.Parse(frame); XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.AreEqual("0 0 200 100", (string?)parsed.Root!.Attribute("viewBox"));
            Assert.AreEqual("matrix(20 0 0 20 -1900 -950)", (string?)parsed.Root.Element(ns + "g")!.Attribute("transform"));
            var png = Path.Combine(folder, "frame.png");
            await VectorRenderer.RenderFrameAsync(frame, png, 400, 200, CancellationToken.None);
            using var image = new System.Drawing.Bitmap(png);
            Assert.AreEqual(400, image.Width); Assert.AreEqual(200, image.Height);
            // The 0.1-unit source line becomes four crisp screen pixels at 20x zoom and 2x DPI.
            Assert.IsTrue(image.GetPixel(201, 100).R > 245);
            Assert.IsTrue(image.GetPixel(205, 100).R < 10);
            StringAssert.Contains(original, "width='.1'");
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousGate; Directory.Delete(folder, true); }
    }
}
