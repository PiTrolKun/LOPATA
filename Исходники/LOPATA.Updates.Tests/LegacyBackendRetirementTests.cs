using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class LegacyBackendRetirementTests
{
    [TestMethod]
    public async Task DroppingLegacyCudaRootRetiresOnlyUnchangedOwnedFiles()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("AIHub.exe", "app");
        var llama = Path.Combine(fixture.Folder, "legacy-llama");
        var chatllm = Path.Combine(fixture.Folder, "chatllm");
        Directory.CreateDirectory(llama);
        Directory.CreateDirectory(chatllm);
        File.WriteAllText(Path.Combine(llama, "llama-server.exe"), "old runtime");
        File.WriteAllText(Path.Combine(llama, "cudart64_12.dll"), "old CUDA");
        File.WriteAllText(Path.Combine(chatllm, "chatllm.exe"), "unchanged backend");
        var roots = new Dictionary<string, string> { ["app"] = fixture.App, ["chatllm"] = chatllm, ["llama"] = llama };
        var previous = await UpdatePackageBuilder.BuildAsync(roots, Path.Combine(fixture.Folder, "before"),
            "0.4.1-beta", new('a', 40), [new("0.4.1-beta", DateTimeOffset.UtcNow, "Fixture")]);
        File.WriteAllText(Path.Combine(llama, "user-model.gguf"), "user data");
        var target = await UpdatePackageBuilder.BuildAsync(
            new Dictionary<string, string> { ["app"] = fixture.App, ["chatllm"] = chatllm },
            Path.Combine(fixture.Folder, "after"), "0.4.2-beta", new('b', 40),
            [new("0.4.2-beta", DateTimeOffset.UtcNow, "Managed hardware libraries")], previous);
        Assert.IsFalse(target.Files.Any(file => file.Root == "llama"));
        Assert.IsTrue(target.Files.Any(file => file.Root == "chatllm"));
        var plan = await UpdatePlanner.CreateAsync(previous, target, new UpdateRoots(roots));
        Assert.AreEqual(2, plan.ChangedFiles);
        Assert.IsTrue(plan.Operations.Where(operation => operation.Action != UpdateAction.Keep).All(operation => operation.Action == UpdateAction.Delete && operation.File.Root == "llama"));
        Assert.AreEqual("user data", File.ReadAllText(Path.Combine(llama, "user-model.gguf")));
        Assert.IsTrue(File.Exists(Path.Combine(llama, "llama-server.exe")), "Planning is read-only.");
        File.WriteAllText(Path.Combine(llama, "cudart64_12.dll"), "locally modified");
        await Assert.ThrowsAsync<IOException>(() => UpdatePlanner.CreateAsync(previous, target, new UpdateRoots(roots)));
        Assert.AreEqual("locally modified", File.ReadAllText(Path.Combine(llama, "cudart64_12.dll")));
    }
}
