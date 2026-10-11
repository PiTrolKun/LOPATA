using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryModelSelectionTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-poetry-selection-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-poetry-selection-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    [TestMethod]
    public void CatalogRequiresExactCandidateArchitectureAndUsableMetadata()
    {
        foreach (var candidate in MusicPoetryModelCatalog.All)
            Assert.IsTrue(MusicPoetryModelCatalog.IsCompatible(Write(candidate.FileName, candidate.Architecture)));
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(Write("Krea-2-Turbo-Q6_K.gguf", "krea2")));
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(Write("Qwen3VL-4B-Instruct-Q4_K_M.gguf", "qwen3vl")));
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(Write("random-chat.gguf", "qwen3")));
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(Write("Qwen3.8-4B-Distill.Q5_K_M.gguf", "qwen35")));
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(Write(CoreModelManager.CoreModelFileName, "krea2")));
        var truncated = Write(CoreModelManager.CoreModelFileName, "qwen3"); File.WriteAllBytes(truncated.Path, [1, 2, 3]);
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(truncated));
        var shortContext = Write(CoreModelManager.CoreModelFileName, "qwen3", 512);
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(shortContext));
        var toolOnly = Write(CoreModelManager.CoreModelFileName, "qwen3"); toolOnly.IsRunnable = false;
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(toolOnly));
    }

    [TestMethod]
    public async Task DirectRuntimeCannotBypassCatalogWithAnOldSessionOrRenamedDiffusionFile()
    {
        using var runtime = new MusicPoetryRuntime(new(new UserProfileStore(), new IpLocationService()));
        foreach (var model in new[] { Write("Krea-2-Turbo-Q6_K.gguf", "krea2"),
            Write(CoreModelManager.CoreModelFileName, "krea2") })
        {
            var session = MusicPoetryProtocol.Create(new() { Lyrics = "Preserved poem" }, key => key);
            session.ModelPath = model.Path;
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                runtime.GenerateAsync(model, session, "", _ => Assert.Fail("Unsupported model reached generation"), CancellationToken.None));
            Assert.AreEqual("Music.Poetry.ModelUnsupported", error.Message);
            Assert.AreEqual("Preserved poem", session.Lyrics);
        }
    }

    [TestMethod]
    public async Task RuntimeUsesCanonicalExecutorLicenseBeforePreparingModel()
    {
        var executor = MusicPoetryModelCatalog.All.Single(candidate => candidate.Id == "executor");
        var previous = ComponentLicenseGate.ConfirmAsync;
        IReadOnlyList<string>? requested = null;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (ids, _) => {
                requested = ids; return Task.FromException(new OperationCanceledException("Test decline")); };
            using var runtime = new MusicPoetryRuntime(new(new UserProfileStore(), new IpLocationService()));
            var session = MusicPoetryProtocol.Create(new() { Lyrics = "Preserved poem" }, key => key);
            await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.GenerateAsync(
                Write(executor.FileName, executor.Architecture), session, "", _ => Assert.Fail("Declined model reached generation"), default));
            CollectionAssert.AreEqual(new[] { "sm54/Qwen3.6-27B-Q4_K_M-GGUF" }, requested!.ToArray());
            Assert.AreEqual("Preserved poem", session.Lyrics);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    [TestMethod]
    public async Task CatalogLicensesExistAndExecutorConsentIsSharedWithoutBypassingChangedTerms()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Licenses");
        var catalog = new ComponentLicenseService(source, Path.Combine(_root, "unused.json"));
        foreach (var candidate in MusicPoetryModelCatalog.All)
            Assert.IsTrue(catalog.Entries.Any(entry => entry.Id == candidate.LicenseId), candidate.Id);
        var executor = MusicPoetryModelCatalog.All.Single(candidate => candidate.Id == "executor");
        var entry = catalog.Entries.Single(entry => entry.Id == executor.LicenseId);
        Assert.IsFalse(entry.Basic); Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Terms));
        foreach (var text in entry.Texts) Assert.IsFalse(string.IsNullOrWhiteSpace(catalog.ReadText(text)));
        Directory.CreateDirectory(_root);
        var catalogPath = Path.Combine(_root, "catalog.json"); var receipts = Path.Combine(_root, "receipts.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new[] { entry }));
        var service = new ComponentLicenseService(_root, receipts);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.EnsureAsync([entry.Id], _ => Task.FromResult(false), default));
        Assert.IsFalse(File.Exists(receipts));
        // Original executor scenario records the same component acknowledgement.
        await service.EnsureAsync([entry.Id], _ => Task.FromResult(true), default);
        var receiptBytes = File.ReadAllBytes(receipts);
        await new ComponentLicenseService(_root, receipts).EnsureAsync([executor.LicenseId],
            _ => throw new AssertFailedException("Chat asked again after executor consent"), default);
        CollectionAssert.AreEqual(receiptBytes, File.ReadAllBytes(receipts));
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new[] { entry with { Terms = "changed-terms" } }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ComponentLicenseService(_root, receipts)
            .EnsureAsync([executor.LicenseId], _ => Task.FromResult(false), default));
        CollectionAssert.AreEqual(receiptBytes, File.ReadAllBytes(receipts));
    }

    private DebugModelInfo Write(string name, string architecture, uint context = 32768)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, name);
        using (var file = File.Create(path)) using (var writer = new BinaryWriter(file, Encoding.UTF8))
        {
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(3ul);
            String("general.architecture"); writer.Write(8u); String(architecture);
            String(architecture + ".block_count"); writer.Write(4u); writer.Write(32u);
            String(architecture + ".context_length"); writer.Write(4u); writer.Write(context);
            void String(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        }
        return new() { Name = name, Path = path, Format = "gguf", IsRunnable = true };
    }
}
