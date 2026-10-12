using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LlamaDenseMemoryPolicyTests
{
    private static LiteraryModelMemoryMetadata Qwen() => new("qwen3", 32, 32768, 0, 5 * LlamaDenseMemoryPolicy.GiB)
    { EmbeddingLength = 4096, HeadCount = 32, KvHeadCount = 8, KeyLength = 128, ValueLength = 128 };

    [TestMethod]
    [DataRow("phi2")]
    [DataRow("phi3")]
    [DataRow("gemma")]
    [DataRow("starcoder2")]
    [DataRow("gpt2")]
    [DataRow("falcon")]
    [DataRow("gptneox")]
    [DataRow("stablelm")]
    [DataRow("olmo")]
    [DataRow("olmo2")]
    [DataRow("gemma2")]
    [DataRow("gemma3")]
    [DataRow("deci")]
    public void OrdinaryAttentionArchitecturesKeepFullKvBudget(string architecture)
    {
        Assert.AreEqual(4 * LlamaDenseMemoryPolicy.GiB,
            LlamaDenseMemoryPolicy.KvBytes(Qwen() with { Architecture = architecture }, 32768));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingKvHeadsAndLayerArraysKeepTheActualMhaBudget(bool array)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            void Text(string value) { var text = System.Text.Encoding.UTF8.GetBytes(value); writer.Write((ulong)text.Length); writer.Write(text); }
            void Number(string key, uint value) { Text("gpt2." + key); writer.Write(4u); writer.Write(value); }
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(array ? 6ul : 5ul);
            Text("general.architecture"); writer.Write(8u); Text("gpt2");
            Number("block_count", 12); Number("context_length", 1024);
            Number("embedding_length", 768); Number("attention.head_count", 12);
            if (array) { Text("gpt2.attention.head_count_kv"); writer.Write(9u); writer.Write(4u); writer.Write(12ul); for (var i = 0; i < 12; i++) writer.Write(12u); }
        }
        bytes.Position = 0;
        var model = LiteraryModelMemoryMetadata.Read(bytes);
        Assert.AreEqual(12, model.KvHeadCount);
        Assert.AreEqual(1024L * 12 * 12 * 128 * 2, LlamaDenseMemoryPolicy.KvBytes(model, 1024));
    }

    [TestMethod]
    public void VariableLayerHeadsAndSwaDimensionsNeverUseTheFirstLayerAsTheWholeBudget()
    {
        var model = Qwen() with { Architecture = "deci", BlockCount = 4, HeadCounts = new[] { 32, 32, 0, 32 },
            KvHeadCounts = new[] { 8, 16, 0, 8 } };
        Assert.AreEqual(256L * (8 + 16 + 0 + 8) * 256 * 2, LlamaDenseMemoryPolicy.KvBytes(model, 256));
        Assert.AreEqual(256L * 32 * 512 * 2, LlamaDenseMemoryPolicy.KvBytes(model with { KeyLengthSwa = 256, ValueLengthSwa = 256 }, 256));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { KvHeadCounts = new[] { 8, 16 } }, 256));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { KvHeadCounts = new[] { 8, 64, 0, 8 } }, 256));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { Architecture = "qwen3" }, 256));
    }

    [TestMethod]
    [DataRow(4u)]
    [DataRow(5u)]
    [DataRow(10u)]
    public void GemmaLayerHeadArraysKeepDifferentLocalAndGlobalHeads(uint elementType)
    {
        using var stream = GemmaHeader(elementType, [16, 16, 16, 16, 16, 4]);
        var model = LiteraryModelMemoryMetadata.Read(stream);
        CollectionAssert.AreEqual(new[] { 16, 16, 16, 16, 16, 4 }, model.KvHeadCounts!.ToArray());
        Assert.AreEqual(32, model.HeadCount); Assert.AreEqual(16, model.KvHeadCount);
        // The existing full-context budget remains conservative; do not collapse to layer 0.
        Assert.AreEqual(1024L * 84 * 1024 * 2, LlamaDenseMemoryPolicy.KvBytes(model, 1024));
    }

    [TestMethod]
    [DataRow(5u, -1)]
    [DataRow(5u, 1000001)]
    [DataRow(10u, 1000001)]
    [DataRow(6u, 16)]
    public void InvalidLayerArrayTypesAndValuesRemainRejected(uint elementType, int headCount)
    {
        using var stream = GemmaHeader(elementType, [headCount, 16, 16, 16, 16, 4]);
        Assert.ThrowsExactly<InvalidDataException>(() => LiteraryModelMemoryMetadata.Read(stream));
    }

    private static MemoryStream GemmaHeader(uint elementType, int[] heads)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            void Text(string value) { var bytes = System.Text.Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
            void Number(string key, uint value) { Text("gemma4." + key); writer.Write(4u); writer.Write(value); }
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(8ul);
            Text("general.architecture"); writer.Write(8u); Text("gemma4");
            Number("block_count", 6); Number("context_length", 262144); Number("embedding_length", 5376);
            Number("attention.head_count", 32); Number("attention.key_length", 512); Number("attention.value_length", 512);
            Text("gemma4.attention.head_count_kv"); writer.Write(9u); writer.Write(elementType); writer.Write((ulong)heads.Length);
            foreach (var value in heads)
                if (elementType == 10) writer.Write((ulong)value);
                else if (elementType == 6) writer.Write((float)value);
                else writer.Write(value);
        }
        stream.Position = 0; return stream;
    }

    [TestMethod]
    public void GemmaSwaBudgetFollowsPinnedNativeWindowAndBatchPadding()
    {
        var model = Qwen() with { Architecture = "gemma4", BlockCount = 6, KeyLength = 512, ValueLength = 512,
            KeyLengthSwa = 256, ValueLengthSwa = 256, SlidingWindow = 1024,
            KvHeadCounts = new[] { 16, 16, 16, 16, 16, 4 },
            SlidingWindowPattern = new[] { true, true, true, true, true, false } };
        Assert.AreEqual(1536L * 80 * 512 * 2 + 32768L * 4 * 1024 * 2, LlamaDenseMemoryPolicy.KvBytes(model, 32768));
        Assert.AreEqual(256L * (80 * 512 + 4 * 1024) * 2, LlamaDenseMemoryPolicy.KvBytes(model, 256));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { KeyLength = 0 }, 32768));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { SlidingWindow = 0 }, 32768));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { SlidingWindowPattern = new[] { true } }, 32768));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(model with { SharedKvLayers = 7 }, 32768));
    }

    [TestMethod]
    public void FullContextKvAndCpuReserveCountSeparatelyFromWeights()
    {
        var model = Qwen(); var gib = LlamaDenseMemoryPolicy.GiB;
        Assert.AreEqual(4 * gib, LlamaDenseMemoryPolicy.KvBytes(model, 32768));
        Assert.AreEqual(11 * gib, LlamaDenseMemoryPolicy.GpuRequired(model, 32768));
        Assert.IsTrue(LlamaDenseMemoryPolicy.CpuDecision(model, 32768, 32 * gib, 16 * gib).Allowed);
        Assert.IsFalse(LlamaDenseMemoryPolicy.CpuDecision(model, 32768, 32 * gib, 14 * gib).Allowed);
        Assert.AreEqual(15 * gib, LlamaDenseMemoryPolicy.CpuDecision(model, 32768, 32 * gib, 16 * gib).RequiredBytes);
    }

    [TestMethod]
    public void MissingDimensionsHybridStateAndExcessContextDoNotBecomeZeroBudget()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(Qwen() with { Architecture = "qwen35" }, 512));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(Qwen() with { KvHeadCount = 0 }, 512));
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(Qwen(), 32769));
        Assert.ThrowsExactly<IOException>(() => LlamaDenseMemoryPolicy.CpuDecision(Qwen(), 512, 0, 0));
        Assert.AreEqual(LlamaDenseMemoryPolicy.KvBytes(Qwen(), 256), LlamaDenseMemoryPolicy.KvBytes(Qwen(), 1));
    }

    [TestMethod]
    public void HybridBudgetIncludesDenseKvAndContextIndependentRecurrentState()
    {
        var hybrid = Qwen() with { Architecture = "qwen35", SsmConvKernel = 4,
            SsmInnerSize = 4096, SsmStateSize = 128, SsmGroupCount = 16, FullAttentionInterval = 4 };
        var recurrent = 24L * 4 * (3 * (4096 + 2 * 16 * 128) + 128 * 4096);
        Assert.AreEqual(LlamaDenseMemoryPolicy.GiB + recurrent, LlamaDenseMemoryPolicy.KvBytes(hybrid, 32768));
        Assert.AreEqual(recurrent, LlamaDenseMemoryPolicy.KvBytes(hybrid, 512) - 16L * 1024 * 1024);
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(hybrid with { SsmStateSize = 0 }, 512));
    }
}
