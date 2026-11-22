using Lopata.PcInspector;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace Lopata.Inspector.Tests;

[TestClass]
public sealed class InspectorTests
{
    [TestMethod]
    public void FullAuditForcesOnAndQuickAuditPreservesThisState()
    {
        using var profile = new Fixture();
        var store = profile.Store;
        Assert.AreEqual(new InspectorState(true, true), store.ReadState());
        Assert.AreEqual(new InspectorState(true, true), store.SetEnabled(false));
        Assert.AreEqual(new InspectorState(true, false), store.SetFullAudit(false));
        Assert.AreEqual(new InspectorState(false, false), store.SetEnabled(false));
        Assert.IsFalse(store.Publish(new InspectorEvent { OperationId = "disabled" }));
        Assert.AreEqual(new InspectorState(true, true), store.SetFullAudit(true));
        Assert.AreEqual(new InspectorState(true, false), new InspectorStore(store.ProfileRoot).SetFullAudit(false));
    }

    [TestMethod]
    public void OldProfilesWithQuickAuditRetainTheirModeBeforeMainAppStarts()
    {
        using var profile = new Fixture();
        File.WriteAllText(Path.Combine(profile.Store.ProfileRoot, "settings.json"),
            """{"languageCode":"en","behavior":{"componentAuditEnabled":false}}""");
        Assert.AreEqual(new InspectorState(true, false), profile.Store.ReadState());
        Assert.IsFalse(profile.Store.ReadRussian());
    }

    [TestMethod]
    public async Task PreflightReceiptAndConfirmedDisplayControlTheAdviceCard()
    {
        using var profile = new Fixture();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var store = profile.Store;
        Assert.IsTrue(store.RegisterWorker(new InspectorWorker(process.Id, process.StartTime.ToUniversalTime())));
        var item = new InspectorEvent { OperationId = "picture", Kind = InspectorEventKind.Preflight };
        var waiting = store.PublishPreflightAsync(item, CancellationToken.None);
        var (path, queued) = store.PendingEvents().Single();
        Assert.AreEqual(item, queued);
        var notice = new InspectorNotice(InspectorStore.Hash("picture"), DateTimeOffset.UtcNow, "picture", "Pressure")
            { Shown = false };
        Assert.IsTrue(store.RecordNotice(notice));
        Assert.HasCount(0, store.ReadNotices());
        Assert.IsFalse(store.Rate(notice.Id, true));
        store.CompleteEvent(path, queued, notice);
        var receipt = await waiting;
        Assert.IsTrue(receipt.Acknowledged);
        Assert.AreEqual("Pressure", receipt.Warning);
        Assert.AreEqual(notice.Id, receipt.NoticeId);
        Assert.IsTrue(store.ConfirmNoticeDisplayed(notice.Id));
        Assert.AreEqual(notice.Id, store.ReadNotices().Single().Id);
        Assert.IsFalse(store.RecordNotice(notice));
    }

    [TestMethod]
    public void RatingsChangeOnlyOncePerAdviceAndSurviveCardAndRawHistoryRotation()
    {
        using var profile = new Fixture();
        var store = profile.Store;
        var now = DateTimeOffset.UtcNow;
        var notice = new InspectorNotice(InspectorStore.Hash("one"), now.AddSeconds(-1), "operation", "Useful");
        var exp = new InspectorExperience("compact", "context", now, "operation", 1, "krea", "revision", "vulkan",
            1024, 1024, "gpu", 32, false, 4, "normal", InspectorOutcome.Success, 5, 0);
        Assert.IsTrue(store.RecordNotice(notice));
        Assert.IsTrue(store.Rate(notice.Id, true));
        store.RecordExperience(exp);
        store.FinishNotices("operation", 1, InspectorOutcome.Success, exp);
        Assert.AreEqual(1, store.ReadExperience(exp.Context, now).Single().HelpfulCount);
        Assert.IsTrue(store.Rate(notice.Id, false));
        Assert.IsTrue(store.Rate(notice.Id, false));
        store.RecordExperience(exp); // a repeated event cannot reset ratings
        var after = store.ReadExperience(exp.Context, now).Single();
        Assert.AreEqual(0, after.HelpfulCount);
        Assert.AreEqual(1, after.UnhelpfulCount);
        Assert.AreEqual("Success", store.ReadNotices().Single().Outcome);
        var oldest = Path.Combine(store.DirectoryPath, "journal", "samples", "old.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(oldest)!);
        File.WriteAllText(oldest, "{}\n");
        File.SetLastWriteTimeUtc(oldest, now.AddDays(-8).UtcDateTime);
        store.Rotate(now);
        Assert.IsFalse(File.Exists(oldest));
        Assert.HasCount(1, store.ReadNotices());
        store.Rotate(now.AddDays(8));
        Assert.HasCount(0, store.ReadNotices());
        Assert.AreEqual(1, store.ReadExperience(exp.Context, now.AddDays(8)).Single().UnhelpfulCount);
    }

    [TestMethod]
    public void PressureNeedsMeasuredMemoryAndComparableRunsRequireWarmthAndActualAdapter()
    {
        var gpu = new InspectorGpuSample("card", "Physical", 200, null, null, null);
        Assert.IsFalse(InspectorObserver.WaitingForVideoMemory(gpu, 80));
        Assert.IsTrue(InspectorObserver.WaitingForVideoMemory(gpu with { DedicatedBytes = 130 }, 80));
        Assert.IsFalse(InspectorObserver.WaitingForVideoMemory(gpu with { DedicatedBytes = 0 }, 80));
        var item = new InspectorEvent { Model = "Krea", Engine = "revision", Backend = "vulkan", Width = 1024, Height = 1024 };
        Assert.IsNull(ObservedAttempt.FindContext(item, gpu));
        item = item with { Warm = false };
        Assert.IsNull(ObservedAttempt.FindContext(item, null));
        Assert.AreNotEqual(ObservedAttempt.FindContext(item, gpu), ObservedAttempt.FindContext(item with { Warm = true }, gpu));
        Assert.AreNotEqual(ObservedAttempt.FindContext(item, gpu),
            ObservedAttempt.FindContext(item, gpu with { CapacityBytes = 100 }));
    }

    [TestMethod]
    public void TelemetrySeparatesAmbiguousAdaptationAndObservesActualWindowsMemory()
    {
        var gpu = new InspectorGpuSample("amd", "A GPU", 200, 100, 0, null);
        var frame = new TelemetryFrame(new InspectorSample(DateTimeOffset.UtcNow, null, null, null,
            [gpu, gpu with { Adapter = "nvidia" }]), []);
        Assert.IsNull(frame.MatchAdapter("A GPU"));
        Assert.AreEqual(gpu, frame.MatchAdapter("amd"));
        using var telemetry = new WindowsInspectorTelemetry();
        var sampled = telemetry.Read(DateTimeOffset.UtcNow).Sample;
        Assert.IsTrue(sampled.TotalRamBytes > 0);
        Assert.IsTrue(sampled.AvailableRamBytes >= 0);
    }

    [TestMethod]
    public async Task IndependentWorkerAcceptsPreflightKeepsOneInstanceAndStopsForServicing()
    {
        using var profile = new Fixture();
        var store = profile.Store;
        var executable = Path.Combine(AppContext.BaseDirectory, "LOPATA Controller PC.exe");
        var oldStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.IsTrue(store.RequestStop());
        Assert.IsTrue(store.StopRequested(oldStart));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var worker = StartWorker(executable, store.ProfileRoot);
        try
        {
            while (!store.IsWorkerRunning()) await Task.Delay(50, timeout.Token);
            Assert.AreEqual(worker.Id, store.ReadWorker()!.ProcessId);
            using var duplicate = StartWorker(executable, store.ProfileRoot);
            await duplicate.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, duplicate.ExitCode);
            Assert.AreEqual(worker.Id, store.ReadWorker()!.ProcessId);

            var preflight = await store.PublishPreflightAsync(new InspectorEvent
            {
                OperationId = "memory-fixture",
                Kind = InspectorEventKind.Preflight,
                UiErrorShown = true,
                ExpectedRamBytes = long.MaxValue
            }, timeout.Token);
            Assert.IsTrue(preflight.Acknowledged);
            Assert.IsFalse(string.IsNullOrEmpty(preflight.Warning));
            Assert.HasCount(0, store.ReadNotices());
            Assert.IsTrue(store.ConfirmNoticeDisplayed(preflight.NoticeId));
            Assert.HasCount(1, store.ReadNotices());
            Assert.IsTrue(store.ReadReport().Samples.Any(item => item.TotalRamBytes > 0));

            Assert.IsTrue(store.RequestStop());
            await worker.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, worker.ExitCode, string.Join(" ", store.ReadReport().Events.Select(item => item.Detail)));
            Assert.IsFalse(store.IsWorkerRunning());
            Assert.AreEqual(new InspectorState(true, true), store.ReadState());
        }
        finally
        {
            if (!worker.HasExited)
            {
                store.RequestStop();
                if (!worker.WaitForExit(3000)) worker.Kill();
                worker.WaitForExit();
            }
        }
    }

    private static Process StartWorker(string executable, string root)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--profile-root");
        info.ArgumentList.Add(root);
        info.ArgumentList.Add("--app-path");
        info.ArgumentList.Add(Path.Combine(root, "fixture-app.exe"));
        return Process.Start(info)!;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "lopata-inspector-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Store = new InspectorStore(root);
        }

        internal InspectorStore Store { get; }
        public void Dispose() => Directory.Delete(Store.ProfileRoot, recursive: true);
    }
}
