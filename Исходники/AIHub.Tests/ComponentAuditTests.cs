using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ComponentAuditTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-audit-" + Guid.NewGuid().ToString("N"));
        public string Component { get; }
        public string Store => Path.Combine(Root, "state");
        public int Full, Presence;
        public Fixture() { Component = Path.Combine(Root, "runtime"); Directory.CreateDirectory(Component); }
        public Task Check(string revision = "v1", CancellationToken token = default,
            Func<CancellationToken, Task>? full = null) => ComponentAudit.RunAsync("fixture", Component, revision,
                ct => { ct.ThrowIfCancellationRequested(); Presence++; return Task.CompletedTask; },
                async ct => { Full++; if (full is not null) await full(ct); }, token);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [TestMethod]
    public async Task ProofIsPersistentAcrossOperationsButResetForRevisionAndReinstallation()
    {
        using var f = new Fixture();
        using var policy = ComponentAudit.UseTestStore(f.Store, false);
        await f.Check();
        await f.Check();
        Assert.AreEqual(1, f.Full); Assert.AreEqual(1, f.Presence);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(f.Store, "receipts")).Length);
        await f.Check("v2");
        ComponentAudit.Invalidate(f.Component);
        await f.Check("v2");
        Assert.AreEqual(3, f.Full);
        // A parent installation invalidation also reaches its nested component.
        ComponentAudit.Invalidate(f.Root);
        await f.Check("v2"); Assert.AreEqual(4, f.Full);
    }

    [TestMethod]
    public async Task DamagedInstallationRegistryRecoversWithFreshProofRequired()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        await f.Check();
        await File.WriteAllTextAsync(Path.Combine(f.Store, "installations.json"), "not json");
        await f.Check(); await f.Check();
        Assert.AreEqual(2, f.Full); Assert.AreEqual(1, f.Presence);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(f.Store, "installations.json")), "\"*\"");
    }

    [TestMethod]
    public async Task ConcurrentFirstUsersShareOneSuccessfulAudit()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Check(full: async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        await entered.Task;
        var others = Enumerable.Range(0, 7).Select(_ => f.Check()).ToArray();
        release.SetResult(); await Task.WhenAll(others.Prepend(first));
        Assert.AreEqual(1, f.Full); Assert.AreEqual(7, f.Presence);
    }

    [TestMethod]
    public async Task FailedCanceledAndCorruptProofRequireFullRetry()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Check(full: _ => throw new InvalidDataException("bad file")));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Check(token: cancel.Token,
            full: _ => { cancel.Cancel(); return Task.CompletedTask; }));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Store, "receipts")));
        await f.Check();
        await File.WriteAllTextAsync(Directory.GetFiles(Path.Combine(f.Store, "receipts")).Single(), "not json");
        await f.Check(); Assert.AreEqual(4, f.Full); Assert.AreEqual(0, f.Presence);
        var receipt = Directory.GetFiles(Path.Combine(f.Store, "receipts")).Single();
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(receipt))!;
        json["Root"] = null; await File.WriteAllTextAsync(receipt, json.ToJsonString());
        await f.Check(); Assert.AreEqual(5, f.Full, "An incomplete JSON receipt is not proof.");
    }

    [TestMethod]
    public async Task InstallationOnlyCheckCannotConsumeFirstUseProof()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        using (ComponentAudit.Installation()) { await f.Check(); await f.Check(); }
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Store, "receipts")));
        await f.Check(); await f.Check();
        Assert.AreEqual(3, f.Full); Assert.AreEqual(1, f.Presence);
    }

    [TestMethod]
    public async Task OperationCapturesModeAndFullAuditFailureRevokesOlderProof()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store);
        var before = ComponentAudit.Enabled;
        try
        {
            ComponentAudit.Configure(true);
            using (ComponentAudit.CaptureOperation())
            {
                ComponentAudit.Configure(false);
                await f.Check(); await f.Check("v2");
                Assert.AreEqual(2, f.Full, "The operation keeps its starting full mode.");
                await f.Check(); Assert.AreEqual(1, f.Presence, "Identical nested requests are checked once.");
            }
            await f.Check(); Assert.AreEqual(2, f.Presence);
            using (ComponentAudit.CaptureOperation())
            {
                using (ComponentAudit.ForceFull()) await f.Check();
                using (ComponentAudit.ForceFull())
                    await Assert.ThrowsAsync<InvalidDataException>(() => f.Check(full: _ => throw new InvalidDataException("bad file")));
                await f.Check(); Assert.AreEqual(5, f.Full, "A failed re-audit revokes both stored and operation-local proof.");
            }
        }
        finally { ComponentAudit.Configure(before); }
    }

    [TestMethod]
    public async Task PresenceSkipsPayloadReadsAndSizeChecksButMissingFileFails()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        var file = Path.Combine(f.Component, "weights.bin");
        var data = new byte[32 * 1024 * 1024]; Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(file, data);
        var files = new[] { new PinnedTreeFile("weights.bin", data.Length, Convert.ToHexString(SHA256.HashData(data))) };
        var clock = Stopwatch.StartNew();
        await RuntimeFileAudit.VerifyAsync("model-fixture", f.Component, "v1", files, default);
        var fullMs = clock.Elapsed.TotalMilliseconds;
        await File.WriteAllTextAsync(file, "short replacement");
        // Exclusive open proves presence does not open/read the payload, even after a size change.
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            clock.Restart(); await RuntimeFileAudit.VerifyAsync("model-fixture", f.Component, "v1", files, default);
            Console.WriteLine($"Component fixture: full 32 MiB={fullMs:F2} ms; quick={clock.Elapsed.TotalMilliseconds:F2} ms; payload exclusively locked.");
        }
        File.Delete(file);
        await Assert.ThrowsAsync<FileNotFoundException>(() => RuntimeFileAudit.VerifyAsync("model-fixture", f.Component, "v1", files, default));
    }

    [TestMethod]
    public async Task ManagedCardsAndRuntimeShareProofWithoutUpdatingFullVerifiedDate()
    {
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        var file = Path.Combine(f.Component, "model.bin"); await File.WriteAllTextAsync(file, "valid");
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
        var store = new ManagedModelLibraryStore(Path.Combine(f.Root, "library"));
        var card = new ManagedModelArtifactCard {
            ModelArtifactId = "audit-model", ModelsRoot = f.Root, InstallDirectory = f.Component,
            Revision = "v1", IsManaged = true,
            Files = [new ManagedModelArtifactFile { RelativePath = "model.bin", SizeBytes = 5, Sha256 = expected, IsRequired = true }]
        };
        store.Upsert(card); using var acquisition = new ManagedModelAcquisitionService(store);
        var first = await acquisition.VerifyAsync(card.ModelArtifactId, null, default);
        Assert.AreEqual(ManagedModelStatuses.Installed, first.Status);
        Assert.IsNotNull(first.LastVerifiedAt);
        await File.WriteAllTextAsync(file, "changed and longer");
        await RuntimeFileAudit.VerifyAsync(card.ModelArtifactId, f.Component, card.Revision,
            [new PinnedTreeFile("model.bin", 5, expected)], default);
        var quick = await acquisition.VerifyAsync(card.ModelArtifactId, null, default);
        Assert.AreEqual(first.LastVerifiedAt, quick.LastVerifiedAt);
        ComponentAudit.Invalidate(f.Component);
        var failed = await acquisition.VerifyAsync(card.ModelArtifactId, null, default);
        Assert.AreEqual(ManagedModelStatuses.Corrupted, failed.Status);
    }

    [TestMethod]
    public async Task LivePackagedAudioAuditBenchmarkWithoutExecutionOrUserStateChanges()
    {
        var report = Environment.GetEnvironmentVariable("LOPATA_AUDIT_REAL_BENCHMARK_REPORT");
        if (string.IsNullOrWhiteSpace(report)) Assert.Inconclusive("Explicit read-only packaged component benchmark required.");
        using var f = new Fixture(); using var policy = ComponentAudit.UseTestStore(f.Store, false);
        var root = Path.Combine(AppContext.BaseDirectory, "MusicAudioRuntime");
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json")));
        var files = manifest.RootElement.GetProperty("Files").EnumerateArray().Select(row =>
            new PinnedTreeFile(row.GetProperty("Name").GetString()!, -1, row.GetProperty("Sha256").GetString()!)).ToArray();
        var revision = manifest.RootElement.GetProperty("Revision").GetString()!;
        var watch = Stopwatch.StartNew();
        await RuntimeFileAudit.VerifyAsync("Music.Audio", root, revision, files, default);
        var first = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        using (ComponentAudit.ForceFull()) await RuntimeFileAudit.VerifyAsync("Music.Audio", root, revision, files, default);
        var full = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); await RuntimeFileAudit.VerifyAsync("Music.Audio", root, revision, files, default);
        var quick = watch.Elapsed.TotalMilliseconds;
        var bytes = files.Sum(file => new FileInfo(Path.Combine(root, file.Path)).Length);
        var proof = new { component = "Music.Audio", files = files.Length, bytes, firstFullMs = first,
            repeatedFullMs = full, presenceMs = quick, modelInference = false, userStateChanged = false };
        await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(proof, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Packaged Music.Audio: {files.Length} files, {bytes} bytes; full={full:F2} ms; presence={quick:F2} ms.");
    }

    [TestMethod]
    public void ExpectedFailuresDoNotSuggestComponentAudit()
    {
        Exception[] expected = [new OperationCanceledException(), new OutOfMemoryException(),
            new HttpRequestException(), new ArgumentException(), new IOException("CUDA out of memory"),
            new BackgroundOperationWaitingException("studio_request_needs_decision"),
            new InvalidOperationException("outer", new HttpRequestException())];
        foreach (var error in expected) Assert.IsFalse(ComponentAuditFailures.IsEligible(error));
        Assert.IsTrue(ComponentAuditFailures.IsEligible(new FileNotFoundException("worker.exe is missing")));
        Assert.IsTrue(ComponentAuditFailures.IsEligible(new InvalidOperationException("worker returned no response")));
    }
}
