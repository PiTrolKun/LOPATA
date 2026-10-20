using System.IO;
using AIHub.Services;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass]
public sealed class BookSmartPaddleMemoryTests
{
    private static LiteraryModelMemoryMetadata Pinned() => new("paddleocr", 18, 131072, 0, BookSmartPaddleMemoryPolicy.ModelBytes)
    { EmbeddingLength = 1024, HeadCount = 16, KvHeadCount = 2, KeyLength = 128, ValueLength = 128 };

    [TestMethod]
    public void FullContextCountsEveryPinnedLayerAndExplicitKeyValueDimensions()
    {
        Assert.AreEqual(576L * 1024 * 1024, BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 32768));
        Assert.AreEqual(2304L * 1024 * 1024, BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 131072));
        Assert.AreEqual(256L * 18 * 2 * 256 * 2, BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 1));
        Assert.AreEqual(BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 256), BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 255));
        Assert.IsTrue(BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 257) > BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), 256));
    }

    [TestMethod]
    public void WeightsVisionAndBothGraphAllowancesRemainSeparateFromKv()
    {
        var expected = BookSmartPaddleMemoryPolicy.ModelBytes + BookSmartPaddleMemoryPolicy.ProjectorBytes
            + 576L * 1024 * 1024 + 4 * LlamaDenseMemoryPolicy.GiB;
        Assert.AreEqual(expected, BookSmartPaddleMemoryPolicy.GpuRequired(Pinned(), BookSmartPaddleMemoryPolicy.ProjectorBytes));
        Assert.AreEqual(expected, BookSmartPaddleMemoryPolicy.Runtime.GpuRequired(Pinned(), BookSmartPaddleMemoryPolicy.ProjectorBytes));
        var cpu = BookSmartPaddleMemoryPolicy.CpuDecision(Pinned(), BookSmartPaddleMemoryPolicy.ProjectorBytes,
            32 * LlamaDenseMemoryPolicy.GiB, 16 * LlamaDenseMemoryPolicy.GiB);
        Assert.IsTrue(cpu.Allowed);
        Assert.AreEqual(expected + 6 * LlamaDenseMemoryPolicy.GiB, cpu.RequiredBytes);
        Assert.IsFalse(BookSmartPaddleMemoryPolicy.CpuDecision(Pinned(), BookSmartPaddleMemoryPolicy.ProjectorBytes,
            32 * LlamaDenseMemoryPolicy.GiB, 10 * LlamaDenseMemoryPolicy.GiB).Allowed);
    }

    [TestMethod, DataRow(0), DataRow(-1), DataRow(131073)]
    public void UnsupportedContextsNeverBecomeZeroBudget(int context)
        => Assert.ThrowsExactly<InvalidDataException>(() => BookSmartPaddleMemoryPolicy.KvBytes(Pinned(), context));

    [TestMethod, DataRow("architecture"), DataRow("blocks"), DataRow("head"), DataRow("kvhead"), DataRow("key"),
     DataRow("value"), DataRow("embedding"), DataRow("modelbytes"), DataRow("context"), DataRow("hybrid"), DataRow("swa")]
    public void UnexpectedProfilesAreRejectedInsteadOfSpoofingAnotherArchitecture(string changed)
    {
        var model = Pinned();
        model = changed switch
        {
            "architecture" => model with { Architecture = "qwen3" }, "blocks" => model with { BlockCount = 19 },
            "head" => model with { HeadCount = 8 }, "kvhead" => model with { KvHeadCount = 0 },
            "key" => model with { KeyLength = 64 }, "value" => model with { ValueLength = 64 },
            "embedding" => model with { EmbeddingLength = 2048 }, "modelbytes" => model with { FileBytes = model.FileBytes - 1 },
            "context" => model with { ModelContextTokens = 32768 }, "hybrid" => model with { SsmStateSize = 128 },
            "swa" => model with { SlidingWindow = 1024 }, _ => throw new InvalidOperationException()
        };
        Assert.ThrowsExactly<InvalidDataException>(() => BookSmartPaddleMemoryPolicy.GpuRequired(model, BookSmartPaddleMemoryPolicy.ProjectorBytes));
    }

    [TestMethod]
    public void WrongProjectorAndPerLayerArraysAreNotAcceptedAsThePinnedPair()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BookSmartPaddleMemoryPolicy.GpuRequired(Pinned(), BookSmartPaddleMemoryPolicy.ProjectorBytes - 1));
        Assert.ThrowsExactly<InvalidDataException>(() => BookSmartPaddleMemoryPolicy.GpuRequired(Pinned() with { KvHeadCounts = Enumerable.Repeat(2, 18).ToArray() }, BookSmartPaddleMemoryPolicy.ProjectorBytes));
        Assert.ThrowsExactly<InvalidDataException>(() => BookSmartPaddleMemoryPolicy.GpuRequired(Pinned() with { HeadCounts = Enumerable.Repeat(16, 18).ToArray() }, BookSmartPaddleMemoryPolicy.ProjectorBytes));
    }

    [TestMethod]
    public void GenericDenseAndOmniPoliciesStillRejectPaddleWithoutExplicitDedicatedPlanner()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LlamaDenseMemoryPolicy.KvBytes(Pinned(), 32768));
        Assert.ThrowsExactly<InvalidDataException>(() => OmniRuntimeMemoryPolicy.GpuRequired(Pinned(), BookSmartPaddleMemoryPolicy.ProjectorBytes));
        var qwen = new LiteraryModelMemoryMetadata("qwen3", 32, 32768, 0, 5 * LlamaDenseMemoryPolicy.GiB)
        { EmbeddingLength = 4096, HeadCount = 32, KvHeadCount = 8, KeyLength = 128, ValueLength = 128 };
        Assert.AreEqual(11 * LlamaDenseMemoryPolicy.GiB, LlamaDenseMemoryPolicy.GpuRequired(qwen, 32768));
    }
}
