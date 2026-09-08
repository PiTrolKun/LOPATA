using AIHub.Services;
namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryChatRuntimeTests
{
    [TestMethod]
    public void AdvisorRequiresCudaWhileWriterStaysCpu()
    {
        var args = LiteraryChatRuntime.Arguments("same model.gguf", 12346, LiteraryChatProfile.AdvisorGpu);
        Assert.AreEqual("CUDA0", args[Array.IndexOf(args, "--device") + 1]);
        Assert.AreEqual("99", args[Array.IndexOf(args, "-ngl") + 1]);
        Assert.AreEqual("0", args[Array.IndexOf(args, "--cache-ram") + 1]);
        Assert.IsFalse(args.Contains("--mmproj"));
        Assert.IsFalse(args.Contains("--no-op-offload"));
        var cpu = LiteraryChatRuntime.Arguments("same model.gguf", 12345);
        Assert.AreEqual("none", cpu[Array.IndexOf(cpu, "--device") + 1]);
    }
    [TestMethod]
    public void CpuProfileCannotOffloadOrLoadProjector()
    {
        var args = LiteraryChatRuntime.Arguments("local model.gguf", 12345);
        Assert.AreEqual("none", args[Array.IndexOf(args, "--device") + 1]);
        Assert.AreEqual("0", args[Array.IndexOf(args, "-ngl") + 1]);
        Assert.AreEqual("0", args[Array.IndexOf(args, "--cache-ram") + 1]);
        CollectionAssert.Contains(args, "--no-op-offload");
        CollectionAssert.Contains(args, "--offline");
        CollectionAssert.Contains(args, "--no-context-shift");
        Assert.IsFalse(args.Contains("--mmproj"));
        Assert.AreEqual("32768", args[Array.IndexOf(args, "-c") + 1]);
        Assert.AreEqual("local model.gguf", args[1]);
    }
}
