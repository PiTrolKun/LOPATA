using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRuntimeDeliveryTests
{
    [TestMethod]
    public async Task PublishedCpuAndCudaPacksContainVerifiedRuntimeAndLocalDependencies()
    {
        foreach (var pack in new[] { MusicYueRuntime.CpuPack, MusicYueRuntime.CudaPack })
        {
            var directory = MusicYueRuntime.DirectoryForPack(pack);
            Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "MusicRuntime", pack), directory);
            await MusicYueRuntime.VerifyAsync(directory, default);
            foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll",
                "LICENSE-MSVC.txt", "LICENSE-yue2.txt", "LICENSE-ggml.txt", "LICENSE-yyjson.txt" })
                Assert.IsTrue(File.Exists(Path.Combine(directory, name)), name);
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*.gguf", SearchOption.AllDirectories).Any());
            Assert.IsFalse(File.Exists(Path.Combine(directory, "nvcc.exe")));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => MusicYueRuntime.DirectoryForPack("../other"));
    }
}
