using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LlamaStagedStartupTests
{
    private static readonly LlamaStartupPlacement Gpu = new(LlamaPlacementMode.Gpu, 7, 32768);
    private static readonly LlamaStartupPlacement Hybrid = new(LlamaPlacementMode.Hybrid, 4, 32768);
    [TestMethod]
    public async Task MemoryPressureRetiresFullGpuBeforeHybridAndRemembersSuccessfulLayers()
    {
        var events = new List<string>(); var retired = false;
        var result = await LlamaStagedStartup.RunAsync(Gpu, (placement, _) => {
            events.Add("load:" + placement.Mode);
            if (placement.Mode == LlamaPlacementMode.Gpu) throw new LlamaVramPressureException();
            Assert.IsTrue(retired); return Task.FromResult(placement);
        }, _ => { events.Add("plan"); Assert.IsTrue(retired); return Task.FromResult(Hybrid); },
            async () => { await Task.Yield(); retired = true; events.Add("retire"); }, _ => { }, default);
        Assert.AreEqual(Hybrid, result);
        CollectionAssert.AreEqual(new[] { "load:Gpu", "retire", "plan", "load:Hybrid" }, events);
    }
    [TestMethod]
    public async Task FailedHybridIsTheOnlyGpuPathToCpuAndCpuFailureStops()
    {
        var attempted = new List<LlamaPlacementMode>(); var retirements = 0;
        var result = await LlamaStagedStartup.RunAsync(Gpu, (placement, _) => {
            attempted.Add(placement.Mode);
            if (placement.Mode != LlamaPlacementMode.Cpu) throw new IOException("native allocation failed");
            return Task.FromResult(placement);
        }, _ => Task.FromResult(Hybrid), () => { retirements++; return Task.CompletedTask; }, _ => { }, default);
        CollectionAssert.AreEqual(new[] { LlamaPlacementMode.Gpu, LlamaPlacementMode.Hybrid, LlamaPlacementMode.Cpu }, attempted);
        Assert.AreEqual(2, retirements); Assert.AreEqual(0, result.GpuLayers);
        await Assert.ThrowsExactlyAsync<IOException>(() => LlamaStagedStartup.RunAsync(result,
            (_, _) => throw new IOException("CPU failed"), _ => throw new AssertFailedException("CPU cannot upgrade"),
            () => Task.CompletedTask, _ => { }, default));
    }
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RememberedSuccessfulPlacementIsReusedWithoutGpuUpgrade(int value)
    {
        var mode = (LlamaPlacementMode)value;
        var saved = new LlamaStartupPlacement(mode, mode == LlamaPlacementMode.Hybrid ? 4 : 0, 8192);
        var calls = 0;
        var result = await LlamaStagedStartup.RunAsync(saved, (placement, _) => {
            calls++; Assert.AreEqual(saved, placement); return Task.FromResult(placement);
        }, _ => throw new AssertFailedException("Unrequested GPU upgrade"),
            () => throw new AssertFailedException("A ready placement should not retire"), _ => { }, default);
        Assert.AreEqual(saved, result); Assert.AreEqual(1, calls);
    }
    [TestMethod]
    public async Task CancellationRetiresWorkerWithoutStartingAnotherMode()
    {
        using var cancellation = new CancellationTokenSource(); var retired = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => LlamaStagedStartup.RunAsync(Gpu,
            (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Gpu); },
            _ => throw new AssertFailedException("Cancelled startup tried a successor"),
            () => { retired = true; return Task.CompletedTask; }, _ => { }, cancellation.Token));
        Assert.IsTrue(retired);
    }
    [TestMethod]
    public void ThresholdIsSelectedDeviceOccupancyAt97Percent()
    {
        Assert.IsFalse(LlamaStagedStartup.AtVramLimit(new("Vulkan0", "GPU", 10000, 301)));
        Assert.IsTrue(LlamaStagedStartup.AtVramLimit(new("Vulkan0", "GPU", 10000, 300)));
        Assert.IsTrue(LlamaStagedStartup.AtVramLimit(new("Vulkan0", "GPU", 10000, 0)));
        Assert.IsFalse(LlamaStagedStartup.AtVramLimit(new("Vulkan0", "GPU", 0, 0)));
    }
    [TestMethod]
    public void NativeAllocationIntentCountsBothCachesAndNeverCountsOtherAdaptersOrRepeatedBuffers()
    {
        var tracker = new LlamaGpuAllocationTracker("Vulkan0", 1000);
        tracker.Observe("load_tensors: Vulkan0 model buffer size = 100.00 MiB");
        tracker.Observe("load_tensors: Vulkan1 model buffer size = 500.00 MiB");
        tracker.Observe("llama_kv_cache_iswa: creating non-SWA KV cache, size = 32768 cells");
        tracker.Observe("llama_kv_cache: Vulkan0 KV buffer size = 20.00 MiB");
        tracker.Observe("llama_kv_cache_iswa: creating     SWA KV cache, size = 1536 cells");
        tracker.Observe("llama_kv_cache: Vulkan0 KV buffer size = 10.00 MiB");
        tracker.Observe("sched_reserve: Vulkan0 compute buffer size = 1.50 MiB");
        tracker.Observe("sched_reserve: Vulkan0 compute buffer size = 1.00 MiB");
        Assert.AreEqual(1000 + (long)(131.5 * 1048576), tracker.IntendedUsedBytes);
    }
    [TestMethod]
    public void HybridBudgetUsesLastLayerSizesRatherThanUniformLayerGuess()
    {
        var gib = LlamaDenseMemoryPolicy.GiB;
        var layout = new LlamaLayerPlacement(gib, new[] { gib, 4 * gib, 2 * gib, gib });
        Assert.AreEqual(2, layout.HybridLayers(new("Vulkan0", "GPU", 10 * gib, 10 * gib), gib));
        Assert.ThrowsExactly<IOException>(() => layout.HybridLayers(new("Vulkan0", "GPU", 10 * gib, gib), gib));
    }
    [TestMethod]
    public void PlacementPreferenceIsAtomicAndInvalidatesOnModelContextOrDeviceChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = Path.Combine(root, "model.gguf"); File.WriteAllBytes(model, [1, 2, 3]);
            var selected = new LlamaRuntimeSelection(new(root, "Vulkan", []), new("Vulkan0", "GPU", 10000, 9000));
            var key = LlamaPlacementPreferences.Key(model, 32768, selected);
            var preferences = new LlamaPlacementPreferences(Path.Combine(root, "choices"));
            Assert.IsNull(preferences.Load(key, 6, 32768)); preferences.Save(key, Hybrid);
            Assert.AreEqual(Hybrid, new LlamaPlacementPreferences(Path.Combine(root, "choices")).Load(key, 6, 32768));
            Assert.AreNotEqual(key, LlamaPlacementPreferences.Key(model, 8192, selected));
            Assert.AreNotEqual(key, LlamaPlacementPreferences.Key(model, 32768, selected with { Device = new("Vulkan1", "Other", 10000, 9000) }));
            File.AppendAllText(model, "changed"); Assert.AreNotEqual(key, LlamaPlacementPreferences.Key(model, 32768, selected));
            preferences.Save(key, Hybrid with { GpuLayers = 6 });
            Assert.ThrowsExactly<InvalidDataException>(() => preferences.Load(key, 6, 32768));
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TensorPlacementCountsPaddingAndRejectsOutOfFileOffsets()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-layer-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "directory.bin");
            void Write(ulong lastOffset)
            {
                using var stream = File.Create(path);
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8);
                foreach (var (name, offset) in new[] { ("blk.0.weight", 0ul), ("token_embd.weight", 64ul),
                    ("blk.1.weight", 96ul), ("blk.2.weight", lastOffset) })
                {
                    var text = System.Text.Encoding.UTF8.GetBytes(name); writer.Write((ulong)text.Length); writer.Write(text);
                    writer.Write(1u); writer.Write(1ul); writer.Write(0u); writer.Write(offset);
                }
                var dataStart = (stream.Position + 31) / 32 * 32;
                stream.SetLength(dataStart + 480);
            }
            Write(224);
            var model = new LiteraryModelMemoryMetadata("gemma4", 3, 32768, 0, new FileInfo(path).Length)
                { TensorDirectoryOffset = 0, TensorCount = 4 };
            var layout = LlamaLayerPlacement.Read(path, model);
            CollectionAssert.AreEqual(new long[] { 64, 128, 256 }, layout.LayerBytes.ToArray());
            Assert.AreEqual(new FileInfo(path).Length - 448, layout.FixedBytes);
            Write(999);
            Assert.ThrowsExactly<InvalidDataException>(() => LlamaLayerPlacement.Read(path, model));
        }
        finally { Directory.Delete(root, true); }
    }
}
