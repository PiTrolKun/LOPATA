using System.IO;
using System.Net;
using System.Net.Http;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class NativeInferenceRecoveryTests
{
    [TestMethod]
    public async Task HardwareFailureRetiresBeforeOneCpuRetryAndDiscardsFailedOutput()
    {
        var calls = new List<string>();
        var gpu = true;
        var attempts = 0;
        var result = await NativeInferenceRecovery.ExecuteAsync(async () =>
        {
            calls.Add(gpu ? "gpu-request" : "cpu-request");
            await Task.Yield();
            if (++attempts == 1) throw new HttpRequestException("VK_ERROR_OUT_OF_DEVICE_MEMORY", null, HttpStatusCode.InternalServerError);
            return "complete CPU answer";
        }, () => gpu, () => false, () => false,
            () => { calls.Add("retire-gpu"); return Task.CompletedTask; },
            () => { calls.Add("start-cpu"); gpu = false; return Task.CompletedTask; },
            _ => { }, CancellationToken.None);
        Assert.AreEqual("complete CPU answer", result);
        CollectionAssert.AreEqual(new[] { "gpu-request", "retire-gpu", "start-cpu", "cpu-request" }, calls);
    }

    [TestMethod]
    public async Task VisiblePartialAnswerRetiresButIsNeverRetriedOrReturnedAsSuccess()
    {
        var retired = false;
        var restarted = false;
        await Assert.ThrowsAsync<IOException>(() => NativeInferenceRecovery.ExecuteAsync<string>(
            () => throw new IOException("Model stream ended before a completion marker."),
            () => true, () => true, () => true,
            () => { retired = true; return Task.CompletedTask; },
            () => { restarted = true; return Task.CompletedTask; }, _ => { }, CancellationToken.None));
        Assert.IsTrue(retired);
        Assert.IsFalse(restarted);
    }

    [TestMethod]
    public async Task CpuFailureCannotCauseAThirdAttempt()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => NativeInferenceRecovery.ExecuteAsync<string>(
            () => { attempts++; throw new IOException("out of memory"); },
            () => true, () => true, () => false, () => Task.CompletedTask,
            () => Task.CompletedTask, _ => { }, CancellationToken.None));
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task CancellationAfterRetirementNeverStartsCpu()
    {
        using var cancellation = new CancellationTokenSource();
        var restarted = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => NativeInferenceRecovery.ExecuteAsync<string>(
            () => throw new IOException("out of memory"), () => true, () => true, () => false,
            () => { cancellation.Cancel(); return Task.CompletedTask; },
            () => { restarted = true; return Task.CompletedTask; }, _ => { }, cancellation.Token));
        Assert.IsFalse(restarted);
    }

    [TestMethod]
    public void CpuCancellationInputAndTransportErrorsAreNotHardwareFailures()
    {
        Assert.IsFalse(NativeInferenceRecovery.CanRecover(new IOException("out of memory"), false, true, CancellationToken.None));
        Assert.IsFalse(NativeInferenceRecovery.CanRecover(new IOException("out of memory"), true, true, new CancellationToken(true)));
        Assert.IsFalse(NativeInferenceRecovery.CanRecover(new OperationCanceledException(), true, true, CancellationToken.None));
        Assert.IsFalse(NativeInferenceRecovery.CanRecover(new HttpRequestException("out of memory", null, HttpStatusCode.BadRequest), true, true, CancellationToken.None));
        Assert.IsFalse(NativeInferenceRecovery.CanRecover(new HttpRequestException("connection lost"), true, false, CancellationToken.None));
        Assert.IsFalse(NativeInferenceRecovery.CanRecover(new System.Text.Json.JsonException("invalid input"), true, true, CancellationToken.None));
    }
}
