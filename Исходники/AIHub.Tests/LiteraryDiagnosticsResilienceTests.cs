using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryDiagnosticsResilienceTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "lopata-diagnostics-resilience-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void UnwritableRootAndThrowingFailureReporterDoNotInterruptCaller()
    {
        Directory.CreateDirectory(_root);
        var blockedRoot = Path.Combine(_root, "blocked");
        File.WriteAllText(blockedRoot, "keep");
        var reports = 0;
        using var log = new LiteraryRequestDiagnostics("CPU", _ =>
        {
            reports++;
            throw new InvalidOperationException("reporter unavailable");
        }, blockedRoot, () => true);

        log.Write("after_failure", "must not save");
        log.Dispose();

        Assert.AreEqual(1, reports);
        Assert.AreEqual("keep", File.ReadAllText(blockedRoot));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void InvalidPathIsReportedWithoutCreatingOutput()
    {
        var reports = new List<string>();
        using var log = new LiteraryRequestDiagnostics("CPU", reports.Add, _root + "\0", () => true);
        log.Write("after_failure", "must not save");

        Assert.AreEqual(1, reports.Count);
        StringAssert.Contains(reports[0], "diagnostics failure");
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public void ThrowingStartupReporterClosesAndReleasesRecording()
    {
        var reports = 0;
        using var log = new LiteraryRequestDiagnostics("CPU", _ =>
        {
            reports++;
            throw new ApplicationException("reporter unavailable");
        }, _root, () => true);
        log.Write("after_failure", "must not save");
        log.Dispose();

        Assert.AreEqual(2, reports);
        var content = ReadClosed(log.FilePath);
        StringAssert.Contains(content, "begin");
        Assert.IsFalse(content.Contains("after_failure", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnsupportedAndCyclicPayloadsStopRecordingWithoutInterruptingCaller(bool cyclic)
    {
        var reports = new List<string>();
        using var log = new LiteraryRequestDiagnostics("CPU", reports.Add, _root, () => true);
        log.Write("valid", "already flushed");
        log.Write("invalid", cyclic ? new CyclicPayload() : typeof(string));
        log.Write("after_failure", "must not save");
        log.Dispose();

        Assert.AreEqual(2, reports.Count);
        StringAssert.Contains(reports[1], cyclic ? nameof(JsonException) : nameof(NotSupportedException));
        var content = ReadClosed(log.FilePath);
        StringAssert.Contains(content, "already flushed");
        Assert.IsFalse(content.Contains("after_failure", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("\"kind\":\"invalid\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThrowingPayloadGetterAndReporterAreContained()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", message =>
        {
            if (message.Contains("failure", StringComparison.Ordinal)) throw new IOException("report failed");
        }, _root, () => true);
        log.Write("invalid", new ThrowingPayload());
        log.Write("after_failure", "must not save");
        log.Dispose();

        Assert.IsFalse(ReadClosed(log.FilePath).Contains("after_failure", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CancellationFromPayloadIsNotHidden()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", _ => { }, _root, () => true);
        Assert.Throws<OperationCanceledException>(() => log.Write("cancelled", new CancelledPayload()));
    }

    [TestMethod]
    public void CancellationDuringStartupReleasesFileBeforePropagating()
    {
        Assert.Throws<OperationCanceledException>(() => new LiteraryRequestDiagnostics("CPU",
            _ => throw new OperationCanceledException(), _root, () => true));

        var files = Directory.GetFiles(_root, "*.jsonl", SearchOption.AllDirectories);
        Assert.AreEqual(1, files.Length);
        StringAssert.Contains(ReadClosed(files[0]), "begin");
    }

    [TestMethod]
    public void DisabledLoggingDoesNotEvaluatePathPayloadOrReporter()
    {
        var reports = 0;
        using var log = new LiteraryRequestDiagnostics("CPU", _ => reports++, _root + "\0", () => false);
        using var process = new Process();
        log.Watch(process);
        log.Write("invalid", new ThrowingPayload());
        log.Dispose();

        Assert.AreEqual(0, reports);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public void WriteAndDisposeIoFailuresStillReleaseRecording()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", message =>
        {
            if (message.Contains("failure", StringComparison.Ordinal)) throw new IOException("report failed");
        }, _root, () => true);
        var writerField = typeof(LiteraryRequestDiagnostics).GetField("_writer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((StreamWriter)writerField.GetValue(log)!).Dispose();
        var failingWriter = new FailingWriter();
        writerField.SetValue(log, failingWriter);

        log.Write("unwritable", "must not save");
        log.Dispose();

        Assert.IsTrue(failingWriter.Disposed);
        Assert.IsNull(writerField.GetValue(log));
        Assert.IsFalse(ReadClosed(log.FilePath).Contains("unwritable", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailedEnabledCallbackDoesNotCreateOutput()
    {
        var reports = new List<string>();
        using var log = new LiteraryRequestDiagnostics("CPU", reports.Add, _root,
            () => throw new InvalidOperationException("settings unavailable"));

        Assert.AreEqual(1, reports.Count);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public void TruncationReporterFailureKeepsRecordingClosedAndWithinLimit()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", message =>
        {
            if (message.Contains("truncated", StringComparison.Ordinal)) throw new IOException("report failed");
        }, _root, () => true, 1024);
        log.Write("oversized", new string('x', 2048));
        log.Write("after_failure", "must not save");
        log.Dispose();

        var content = ReadClosed(log.FilePath);
        StringAssert.Contains(content, "truncated");
        Assert.IsFalse(content.Contains("after_failure", StringComparison.Ordinal));
        Assert.IsTrue(new FileInfo(log.FilePath).Length <= 1024);
    }

    private static string ReadClosed(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class CyclicPayload { public object Self => this; }
    private sealed class ThrowingPayload { public string Value => throw new ApplicationException("payload unavailable"); }
    private sealed class CancelledPayload { public string Value => throw new OperationCanceledException(); }
    private sealed class FailingWriter() : StreamWriter(new MemoryStream())
    {
        public bool Disposed { get; private set; }
        public override void WriteLine(string? value) => throw new IOException("write failed");
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            throw new IOException("flush failed");
        }
    }
}
