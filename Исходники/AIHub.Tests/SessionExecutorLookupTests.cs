using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class SessionExecutorLookupTests
{
    [TestMethod]
    public void HistoryUsesOneDiscoveryAndNextRefreshSeesChangedInstallation()
    {
        var scans = 0;
        IReadOnlyList<DebugModelInfo> Discover() { scans++; return [new() { Name = "model", Path = "model-" + scans + ".gguf", Role = "executor", IsRunnable = true, SizeBytes = 10 }]; }
        var batch = new SessionExecutorLookup(Discover);
        var first = batch.Find(new() { SelectedExecutorModel = "org/model" });
        var second = batch.Find(new() { SelectedExecutorModel = "model" });
        Assert.AreEqual(1, scans); Assert.AreEqual(first!.InstalledPath, second!.InstalledPath);
        var refreshed = new SessionExecutorLookup(Discover).Find(new() { SelectedExecutorModel = "model" });
        Assert.AreEqual(2, scans); Assert.AreNotEqual(first.InstalledPath, refreshed!.InstalledPath);
    }

    [TestMethod]
    public void ExistingArtifactAndUnselectedExecutorAvoidDiscoveryAndDoNotInventReadiness()
    {
        var path = Path.GetTempFileName(); var scans = 0;
        try {
            var lookup = new SessionExecutorLookup(() => { scans++; return [new() { Name = "model", Role = "tool", IsRunnable = true }]; });
            var saved = new ExecutorModelArtifact { IsInstalled = true, InstalledPath = path };
            Assert.AreSame(saved, lookup.Find(new() { ExecutorArtifact = saved }));
            Assert.IsNull(lookup.Find(new())); Assert.AreEqual(0, scans);
            Assert.IsNull(lookup.Find(new() { SelectedExecutorModel = "model" })); Assert.AreEqual(1, scans);
        }
        finally { File.Delete(path); }
    }
}
