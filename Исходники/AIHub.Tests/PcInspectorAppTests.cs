using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Lopata.Inspector;

namespace AIHub.Tests;

[TestClass]
public sealed class PcInspectorAppTests
{
    [TestMethod]
    public void SpecialReportLaunchDoesNotMatchOtherLauncherArguments()
    {
        Assert.IsTrue(ApplicationUpdateStartup.IsInspectorReportLaunch(["--inspector-report"]));
        Assert.IsTrue(ApplicationUpdateStartup.IsInspectorReportLaunch(["--recover-session", "--inspector-report"]));
        Assert.IsFalse(ApplicationUpdateStartup.IsInspectorReportLaunch(["--background", "--inspector-report"]));
        Assert.IsFalse(ApplicationUpdateStartup.IsInspectorReportLaunch(["--inspector-report", "unrelated-path"]));
        Assert.IsFalse(ApplicationUpdateStartup.IsInspectorReportLaunch(["--background"]));
    }

    [TestMethod]
    public void RecordsOnlyConfirmedNativeProgress()
    {
        var loading = ImageInspectorAttempt.ReadNativeProgress("load tensors from file: C:\\private\\model.safetensors");
        Assert.AreEqual("model_loading", loading?.Stage);
        Assert.IsNull(loading?.Detail);
        var step = ImageInspectorAttempt.ReadNativeProgress("  |=======>     | 3/8 - 1.20s/it");
        Assert.AreEqual("step 3/8", step?.Detail);
        Assert.AreEqual("model_loading", ImageInspectorAttempt.ReadNativeProgress("  |###| 47/430 - 1.37GB/s")?.Stage);
        var tile = ImageInspectorAttempt.ReadNativeProgress("  |###| 1/9 - 2.57it/s", decoding: true);
        Assert.AreEqual("image_decoding", tile?.Stage);
        Assert.IsNull(tile?.Detail);
        Assert.AreEqual("image_decoding", ImageInspectorAttempt.ReadNativeProgress("sampling completed, taking 1.2s")?.Stage);
        Assert.IsNull(ImageInspectorAttempt.ReadNativeProgress("backend connected"));
    }

    [TestMethod]
    public void DistinguishesDriverMemoryErrorHostMemoryAndUserDeferral()
    {
        Assert.AreEqual(InspectorOutcome.OutOfVideoMemory,
            ImageInspectorAttempt.Evaluate(1, "VK_ERROR_OUT_OF_DEVICE_MEMORY", ImageGenerationGpuPlacement.Unknown, true));
        Assert.AreEqual(InspectorOutcome.OutOfMemory,
            ImageInspectorAttempt.Evaluate(1, "std::bad_alloc", ImageGenerationGpuPlacement.Unknown, false));
        Assert.AreEqual(InspectorOutcome.Failure,
            ImageInspectorAttempt.Evaluate(1, "failed to connect", ImageGenerationGpuPlacement.Unknown, true));
        Assert.AreEqual(InspectorOutcome.Success,
            ImageInspectorAttempt.Evaluate(0, "saved", ImageGenerationGpuPlacement.Unknown, false));
        Assert.AreEqual(InspectorOutcome.Cancelled,
            ImageInspectorAttempt.Evaluate(new OperationCanceledException(ApplicationInspector.Deferred), CancellationToken.None));
        Assert.AreEqual(InspectorOutcome.Disconnected,
            ImageInspectorAttempt.Evaluate(new BackgroundOperationWaitingException("Disconnected"), CancellationToken.None));
    }

    [TestMethod]
    public async Task AceChecksTheSelectedCardAndRequiresAnExplicitDecisionWhenMemoryIsShort()
    {
        const long free = 3L << 30;
        var needed = (long)(3.7 * (1L << 30));
        var line = FormattableString.Invariant($"[LOPATA Inspector] generating|cuda:1|{needed}|{free}");
        Assert.IsTrue(MusicAceWorker.TryReadMemoryCheck(line, "cuda:1", out var stage, out var need, out var remaining));
        Assert.AreEqual("generating", stage);
        Assert.AreEqual(InspectorPreflightStatus.Insufficient, InspectorDiagnostics.CheckVideoMemory("cuda:1", need, remaining));
        Assert.IsFalse(MusicAceWorker.TryReadMemoryCheck(line, "cuda:0", out _, out _, out _));
        Assert.AreEqual(InspectorPreflightStatus.NotApplicable, InspectorDiagnostics.CheckVideoMemory("cpu", needed, free));
        Assert.AreEqual(InspectorPreflightStatus.NotApplicable, InspectorDiagnostics.CheckVideoMemory("remote", needed, free));
        Assert.AreEqual(InspectorPreflightStatus.Unknown, InspectorDiagnostics.CheckVideoMemory("cuda:1", needed, null));
        Assert.AreEqual(InspectorPreflightStatus.Sufficient, InspectorDiagnostics.CheckVideoMemory("cuda:1", needed, 4L << 30));

        var warning = InspectorDiagnostics.VideoMemoryWarning(need!.Value, remaining!.Value, true);
        StringAssert.Contains(warning, "3,7 ГБ");
        StringAssert.Contains(warning, "3,0 ГБ");
        var decision = new InspectorPreflightResult(true, warning, Status: InspectorPreflightStatus.Insufficient);
        await ApplicationInspector.ConfirmOrDeferAsync(decision, (_, _) => Task.FromResult(true), CancellationToken.None);
        var deferred = await Assert.ThrowsExactlyAsync<InspectorDeferredException>(() =>
            ApplicationInspector.ConfirmOrDeferAsync(decision, (_, _) => Task.FromResult(false), CancellationToken.None));
        Assert.AreEqual(warning, deferred.Warning);
        await Assert.ThrowsExactlyAsync<InspectorDeferredException>(() =>
            ApplicationInspector.ConfirmOrDeferAsync(decision, null, CancellationToken.None));
    }

    [TestMethod]
    public void ExplainsAdmissionFailureSeparatelyFromRuntimeMemoryAndNativeCrash()
    {
        Assert.AreEqual(InspectorOutcome.VideoMemoryAdmissionDenied,
            GenerationFailureClassifier.FromWorker(1, "RuntimeError: Insufficient free VRAM: need ~3.7 GB, only 3.0 GB available.", "cuda:1"));
        Assert.AreEqual(InspectorOutcome.OutOfVideoMemory,
            GenerationFailureClassifier.FromWorker(1, "torch.OutOfMemoryError: CUDA out of memory.", "cuda:1"));
        Assert.AreEqual(InspectorOutcome.OutOfMemory, GenerationFailureClassifier.FromWorker(1, "MemoryError", "cpu"));
        Assert.AreEqual(InspectorOutcome.DriverFailure, GenerationFailureClassifier.FromWorker(1, "VK_ERROR_DEVICE_LOST", "Vulkan1"));
        Assert.AreEqual(InspectorOutcome.WorkerFailure, GenerationFailureClassifier.FromWorker(unchecked((int)0xc0000005), null, null));
        Assert.AreEqual(InspectorOutcome.Failure, GenerationFailureClassifier.FromWorker(1, "GPU utilization 100%", "cuda:1"));
        Assert.AreEqual(InspectorOutcome.Cancelled,
            GenerationFailureClassifier.FromException(new OperationCanceledException(), new CancellationToken(true)));
    }

    [TestMethod]
    public void SameNamedGpusAreMatchedByTheBackendDeviceAndAmbiguousInventoryIsUnknown()
    {
        var chosen = new RuntimeDevice("Vulkan1", "Identical adapter", 16000, 7000);
        var other = chosen with { Id = "Vulkan0", FreeBytes = 3000 };
        Assert.AreEqual(7000L, RuntimeDeviceProbe.FindSameDevice([other, chosen], chosen)?.FreeBytes);
        Assert.IsNull(RuntimeDeviceProbe.FindSameDevice([other, chosen, chosen], chosen));
        Assert.IsNull(RuntimeDeviceProbe.FindSameDevice([other], chosen));
    }

    [TestMethod]
    public void ReportIncludesOnlyDisplayedAdviceWithoutIslandRatings()
    {
        var recent = DateTimeOffset.UtcNow;
        InspectorReport report = new([new() { Kind = InspectorEventKind.OperationFinished, Outcome = InspectorOutcome.Success }], [],
            [new("displayed", recent, "request", "Displayed advice", "completed", true),
             new("pending", recent, "request", "Pending advice", Helpful: false) { Shown = false }], []);
        using var document = JsonDocument.Parse(MainWindow.BuildInspectorEvidence(report));
        var notices = document.RootElement.GetProperty("Notices");
        Assert.AreEqual(1, notices.GetArrayLength());
        Assert.AreEqual("Displayed advice", notices[0].GetProperty("Text").GetString());
        Assert.IsFalse(notices[0].TryGetProperty("Helpful", out _));
        Assert.AreEqual("Success", document.RootElement.GetProperty("Events")[0].GetProperty("Outcome").GetString());
    }
}
