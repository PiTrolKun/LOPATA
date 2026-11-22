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
