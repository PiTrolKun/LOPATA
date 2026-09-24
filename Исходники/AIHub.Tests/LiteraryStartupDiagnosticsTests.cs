using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LiteraryStartupDiagnosticsTests
{
    private static JsonDocument Entry(string line) => JsonDocument.Parse(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]);

    [TestMethod]
    public async Task InterviewFailureIsRecordedWithDetailedLoggingOffAndReleasesQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".creation"), "test");
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var enabled = LiteraryRequestDiagnostics.Enabled;
        try
        {
            LiteraryRequestDiagnostics.Enabled = false;
            ComponentLicenseGate.ConfirmAsync = (_, _) => throw new IOException("synthetic prelaunch failure");
            using var runtime = new LiteraryChatRuntime(root, preparing: true);
            for (var i = 0; i < 2; i++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<IOException>(() => runtime.InterviewAsync([], _ => { }, timeout.Token));
                Assert.IsFalse(runtime.IsBusy);
            }
            var text = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "Diagnostics/LiteraryShared"), "*.log").Single());
            StringAssert.Contains(text, "startup_failure");
            StringAssert.Contains(text, "license_check");
            StringAssert.Contains(text, "synthetic prelaunch failure");
            StringAssert.Contains(text, "Structured request failed: role=Interview");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Diagnostics/LiteraryDetailed")));
        }
        finally
        {
            ComponentLicenseGate.ConfirmAsync = previousGate;
            LiteraryRequestDiagnostics.Enabled = enabled;
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExitedProcessKeepsExitCodeAndLastStderrBeforeDisposal()
    {
        var lines = new List<string>();
        var trace = new LiteraryStartupDiagnostics(lines.Add, (_, _) => { });
        trace.Stage("health_check");
        trace.Capture("stderr", "failed to open GGUF: access denied");
        using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        { Arguments = "/c exit 7", UseShellExecute = false, CreateNoWindow = true })!;
        await process.WaitForExitAsync();
        trace.Failure(new InvalidOperationException("Native exit"), process, false, false);
        using var entry = Entry(lines.Last());
        var data = entry.RootElement.GetProperty("data");
        Assert.AreEqual("process_exited", data.GetProperty("reason").GetString());
        Assert.AreEqual(7, data.GetProperty("process").GetProperty("ExitCode").GetInt32());
        Assert.AreEqual("stderr", data.GetProperty("tail")[0].GetProperty("stream").GetString());
        StringAssert.Contains(data.GetProperty("tail")[0].GetProperty("text").GetString()!, "GGUF");
    }

    [TestMethod]
    public void TimeoutAndUserCancellationAreDistinctAndTailIsBounded()
    {
        foreach (var cancelled in new[] { false, true })
        {
            var lines = new List<string>();
            var trace = new LiteraryStartupDiagnostics(lines.Add, (_, _) => { });
            trace.Stage("health_check");
            for (var i = 0; i < 100; i++) trace.Capture("stderr", i + new string('x', 5000));
            trace.Failure(new OperationCanceledException(), null, cancelled, true);
            using var entry = Entry(lines.Last());
            var data = entry.RootElement.GetProperty("data");
            Assert.AreEqual(cancelled ? "cancelled" : "startup_timeout", data.GetProperty("reason").GetString());
            Assert.AreEqual(40, data.GetProperty("tail").GetArrayLength());
            Assert.IsTrue(data.GetProperty("tail")[0].GetProperty("text").GetString()!.Length < 2100);
        }
    }

    [TestMethod]
    public void ReadyClearsStartupTailAndNeverCapturesLaterModelText()
    {
        var lines = new List<string>();
        var trace = new LiteraryStartupDiagnostics(lines.Add, (_, _) => { });
        trace.Capture("stderr", "startup evidence");
        using var process = Process.GetCurrentProcess();
        trace.Ready(process, 1024);
        trace.Capture("stdout", "PRIVATE_MODEL_RESPONSE");
        trace.Failure(new IOException("later"), process, false, false);
        Assert.IsFalse(string.Join("\n", lines).Contains("PRIVATE_MODEL_RESPONSE", StringComparison.Ordinal));
        using var entry = Entry(lines.Last());
        Assert.AreEqual(0, entry.RootElement.GetProperty("data").GetProperty("tail").GetArrayLength());
    }
}
