using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class RagBookLoadTests
{
    private static RagBookResources Resources(double? cpu, long available = 16L << 30) => new(cpu, 32L << 30, available);

    [TestMethod]
    public void ExternalCpuLoadDrainsAndRecoveryRampsWithinBudget()
    {
        var policy = new RagBookLoadPolicy(16, 50);
        for (var i = 0; i < 30; i++) policy.Update(Resources(5));
        Assert.AreEqual(8, policy.Maximum); Assert.AreEqual(8, policy.Capacity);
        for (var i = 0; i < 30; i++) policy.Update(Resources(95));
        Assert.AreEqual(0, policy.Capacity);
        for (var i = 0; i < 30; i++) policy.Update(Resources(5));
        Assert.AreEqual(8, policy.Capacity);
    }

    [TestMethod]
    public void MemoryAndMissingTelemetryCannotGrantUnlimitedWorkers()
    {
        var policy = new RagBookLoadPolicy(128, 50);
        for (var i = 0; i < 50; i++) policy.Update(Resources(0));
        Assert.IsTrue(policy.Capacity <= 32);
        policy.Update(Resources(null)); Assert.IsTrue(policy.Capacity <= 1);
        policy.Update(Resources(0, 1L << 30)); Assert.AreEqual(0, policy.Capacity);
        for (var i = 0; i < 8; i++) policy.Update(Resources(0));
        Assert.IsTrue(policy.Capacity > 0);
        Assert.AreEqual(1, new RagBookLoadPolicy(1, 50).Maximum);
    }

    [TestMethod]
    public async Task QueuedAdmissionIsCancellableAndLeasesReleaseCapacityOnce()
    {
        var controller = new RagBookLoadController(50, new Progress<RagBookProgress>(), () => Resources(0), 4);
        var first = await controller.EnterAsync(default); var second = await controller.EnterAsync(default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => { using var ignored = await controller.EnterAsync(cancel.Token); });
        first.Dispose(); first.Dispose();
        using var next = await controller.EnterAsync(default);
        second.Dispose();
    }

    [TestMethod]
    public void OldJobAndWorkspaceDefaultToModerateLoad()
    {
        var job = System.Text.Json.JsonSerializer.Deserialize<RagBookJob>("{\"Id\":\"old\",\"Name\":\"n\",\"Directory\":\"d\",\"ModelsRoot\":\"m\",\"Books\":[]}")!;
        Assert.AreEqual(50, job.CpuTargetPercent);
        Assert.AreEqual(50, new RagBookDraft().CpuTargetPercent);
        Assert.AreEqual(75, (job with { CpuTargetPercent = 75 }).CpuTargetPercent);
    }
}
