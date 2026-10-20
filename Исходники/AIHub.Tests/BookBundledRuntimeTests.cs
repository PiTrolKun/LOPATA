using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.Books;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookBundledRuntimeTests
{
    [TestMethod]
    public void OfficeUsesApplicationFilesAndHasNoDownloadPlan()
    {
        var office = BookConverterRuntime.FindBundledLibreOffice();
        Assert.IsNotNull(office);
        Assert.AreEqual(office, BookConverterRuntime.FindLibreOffice());
        var archived = File.Exists(Path.Combine(AppContext.BaseDirectory, "BookOfficeRuntime", BookOfficeBundle.FileName));
        Assert.IsTrue(office.StartsWith(archived ? AppDataPaths.RuntimeDirectory : Path.Combine(AppContext.BaseDirectory, "BookOfficeRuntime"), StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(office, new SystemCliDiscoveryService().Find("convert.legacy_office")?.ExecutablePath);
        if (!archived) foreach (var file in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(office)!, file)), "Office must carry its app-local CRT: " + file);
        var entry = ComponentCatalog.Find("runtime.libreoffice")!;
        Assert.IsTrue(entry.IsBuiltIn);
        Assert.AreEqual(0L, entry.DownloadSizeBytes);
        Assert.IsTrue(string.IsNullOrEmpty(entry.DownloadUrl));
        var plan = new ComponentManager().BuildPlanForComponents([entry.Id], "Bundled office test");
        Assert.IsTrue(plan.Items.Single().AlreadyAvailable);
        Assert.AreEqual(0L, plan.Items.Single().DownloadSizeBytes);
    }

    [TestMethod]
    public void IncompleteOfficeLayoutIsNotAdvertisedAsBundled()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-office-layout-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(root, "BookOfficeRuntime", "program"); Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "soffice.com"), "test");
        Assert.IsNull(BookConverterRuntime.FindBundledLibreOffice(root));
        File.WriteAllText(Path.Combine(program, "soffice.bin"), "test");
        Directory.CreateDirectory(Path.Combine(root, "BookOfficeRuntime", "share"));
        Assert.IsNotNull(BookConverterRuntime.FindBundledLibreOffice(root));
    }

    [TestMethod, DataRow("valid"), DataRow("missing"), DataRow("changed"), DataRow("escape"), DataRow("cancelled")]
    public async Task OfficeManifestRejectsMissingChangedAndUnsafeFiles(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-office-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        byte[] bytes = [0, 1, 2, 3]; var path = Path.Combine(root, "file.bin"); await File.WriteAllBytesAsync(path, bytes);
        var name = mode == "escape" ? "../file.bin" : "file.bin";
        using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Files = new[] { new { Name = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } } }));
        if (mode == "missing") File.Delete(path);
        if (mode == "changed") await File.WriteAllBytesAsync(path, [0, 1, 2, 4]); // Same size must still fail.
        using var cancellation = new CancellationTokenSource();
        if (mode == "cancelled") cancellation.Cancel();
        if (mode == "valid") await BookConverterRuntime.VerifyOfficeFilesAsync(root, manifest, cancellation.Token);
        else if (mode == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(() => BookConverterRuntime.VerifyOfficeFilesAsync(root, manifest, cancellation.Token));
        else await Assert.ThrowsAsync<InvalidDataException>(() => BookConverterRuntime.VerifyOfficeFilesAsync(root, manifest, cancellation.Token));
    }
}
