using System.IO;
using System.Text;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryRamReserveTests
{
    private static LiteraryModelMemoryMetadata Model => new("qwen35", 65, 262144, 1, 12_599_187_008);

    [TestMethod]
    public void VerifiedHybridProfileMovesExactlyTwoMainLayersWithoutChangingKvOrFitReserve()
    {
        Assert.AreEqual(64, Model.ReserveGpuLayers); // 65 GGUF blocks + output, minus two.
        var args = LiteraryChatRuntime.Arguments("model.gguf", 12345, 4123, Model.ReserveGpuLayers);
        Assert.AreEqual("64", args[Array.IndexOf(args, "-ngl") + 1]);
        Assert.AreEqual("4123", args[Array.IndexOf(args, "--fit-target") + 1]);
        Assert.AreEqual("on", args[Array.IndexOf(args, "--fit") + 1]);
        Assert.IsFalse(args.Contains("--no-kv-offload"));
        Assert.IsFalse(args.Contains("-ctk") || args.Contains("-ctv"));
        Assert.IsFalse((Model with { Architecture = "qwen35moe" }).SupportsRamReserve);
        Assert.Throws<InvalidDataException>(() => { _ = (Model with { BlockCount = 2 }).ReserveGpuLayers; });
    }

    [TestMethod]
    public void AdmissionIncludesMappedModelHostBuffersAndPhysicalRamSafetyReserve()
    {
        const long gib = LiteraryRamReservePolicy.GiB;
        var exact = LiteraryRamReservePolicy.Evaluate(12 * gib, 32 * gib, 18 * gib);
        Assert.IsTrue(exact.Allowed);
        Assert.AreEqual(4 * gib, exact.SafetyReserveBytes);
        Assert.IsFalse(LiteraryRamReservePolicy.Evaluate(12 * gib, 32 * gib, 18 * gib - 1).Allowed);
        var large = LiteraryRamReservePolicy.Evaluate(12 * gib, 128 * gib, 30 * gib);
        Assert.AreEqual(128 * gib / 10, large.SafetyReserveBytes);
        Assert.Throws<IOException>(() => LiteraryRamReservePolicy.Evaluate(12 * gib, 0, 18 * gib));
        Assert.IsFalse(LiteraryRamReservePolicy.Evaluate(long.MaxValue, 32 * gib, 32 * gib).Allowed);
    }

    [TestMethod]
    public void ReserveOfferedOnlyForMemoryLimitedWindowAndNeverBeyondModelLimit()
    {
        var fit = LiteraryRamReservePolicy.Snapshot(Model, 4096, 3800, 256, false);
        Assert.IsTrue(fit.CanUseRamReserve);
        Assert.IsFalse(fit.ExceedsModelContext);
        Assert.AreEqual(262144, fit.ModelContextTokens);
        Assert.IsFalse(LiteraryRamReservePolicy.Snapshot(Model, 8192, 3800, 256, false).CanUseRamReserve);
        Assert.IsFalse(LiteraryRamReservePolicy.Snapshot(Model, 4096, 3800, 256, true).CanUseRamReserve);
        Assert.IsFalse(LiteraryRamReservePolicy.Snapshot(null, 4096, 3800, 256, false).CanUseRamReserve);
        var impossible = LiteraryRamReservePolicy.Snapshot(Model, 4096, 262144, 256, false);
        Assert.IsTrue(impossible.ExceedsModelContext);
        Assert.IsFalse(impossible.CanUseRamReserve);
        Assert.IsFalse(LiteraryRamReservePolicy.Snapshot(Model, 4096, int.MaxValue, 1024, false).CanUseRamReserve);
    }

    [TestMethod]
    public void ModelMetadataIsReadWithoutWeightsAndInvalidHeadersFailClosed()
    {
        using var valid = Header();
        var metadata = LiteraryModelMemoryMetadata.Read(valid);
        Assert.AreEqual("qwen35", metadata.Architecture);
        Assert.AreEqual(65, metadata.BlockCount);
        Assert.AreEqual(1, metadata.PredictionLayers);
        Assert.AreEqual(262144, metadata.ModelContextTokens);
        Assert.AreEqual(64, metadata.ReserveGpuLayers);
        using var truncated = new MemoryStream(valid.ToArray()[..^3]);
        Assert.Throws<EndOfStreamException>(() => LiteraryModelMemoryMetadata.Read(truncated));
        using var duplicate = Header(duplicate: true);
        Assert.Throws<InvalidDataException>(() => LiteraryModelMemoryMetadata.Read(duplicate));
    }

    [TestMethod]
    public void TemporaryProfileResetsAfterCancellationAndNextOperationIsOrdinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIHub.RamReserve." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".creation"), "test");
        try
        {
            using var runtime = new LiteraryChatRuntime(root, preparing: true);
            try
            {
                runtime.BeginBudgetOperation(new(UseRamReserve: true));
                Assert.IsTrue(runtime.UsesRamReserve);
                throw new OperationCanceledException();
            }
            catch (OperationCanceledException) { }
            finally { runtime.EndBudgetOperation(); }
            Assert.IsFalse(runtime.UsesRamReserve);
            runtime.BeginBudgetOperation(new(RefreshMemory: true));
            Assert.IsFalse(runtime.UsesRamReserve);
            runtime.EndBudgetOperation();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void OnlyExplicitCudaAllocationFailureGetsTheGpuDiagnosis()
    {
        Assert.IsTrue(LiteraryRamReservePolicy.IsExplicitCudaOutOfMemory("CUDA error: out of memory"));
        Assert.IsFalse(LiteraryRamReservePolicy.IsExplicitCudaOutOfMemory("not enough context memory"));
        Assert.IsFalse(LiteraryRamReservePolicy.IsExplicitCudaOutOfMemory("CPU allocation: out of memory"));
        Assert.IsFalse(LiteraryRamReservePolicy.IsExplicitCudaOutOfMemory("CUDA device unavailable"));
    }

    private static MemoryStream Header(bool duplicate = false)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(duplicate ? 6ul : 5ul);
            Text("general.architecture"); writer.Write(8u); Text("qwen35");
            Number("qwen35.block_count", 65); Number("qwen35.context_length", 262144);
            Text("tokenizer.test"); writer.Write(9u); writer.Write(8u); writer.Write(2ul); Text("one"); Text("two");
            Number("qwen35.nextn_predict_layers", 1);
            if (duplicate) Number("qwen35.block_count", 65);
            void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
            void Number(string key, uint value) { Text(key); writer.Write(4u); writer.Write(value); }
        }
        stream.Position = 0; return stream;
    }
}
