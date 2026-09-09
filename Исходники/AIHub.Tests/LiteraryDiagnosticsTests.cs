using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryDiagnosticsTests
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "lopata-diagnostics-" + Guid.NewGuid().ToString("N")); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static string ReadLive(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    [TestMethod]
    public async Task ResourceSamplerRecordsProcessAndGpuAvailability()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", _ => { }, _root, () => true);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        log.Watch(process);
        await Task.Delay(1200);
        var text = ReadLive(log.FilePath);
        StringAssert.Contains(text, "cpuTimeMs");
        StringAssert.Contains(text, "dedicatedBytes");
        StringAssert.Contains(text, "available");
    }
    [TestMethod]
    public void DisabledDoesNotCreateFiles()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", _ => { }, _root, () => false);
        log.Write("secret", "user text");
        Assert.IsFalse(Directory.Exists(_root));
    }
    [TestMethod]
    public void TurningOffStopsExistingLogAndKeepsRolesSeparate()
    {
        var enabled = true;
        using var cpu = new LiteraryRequestDiagnostics("CPU", _ => { }, _root, () => enabled);
        using var gpu = new LiteraryRequestDiagnostics("GPU", _ => { }, _root, () => enabled);
        cpu.Write("text", "CPU only"); gpu.Write("text", "GPU only");
        enabled = false; cpu.Write("text", "must not save");
        enabled = true; cpu.Write("text", "must not resume mid-request");
        var content = File.ReadAllText(cpu.FilePath);
        StringAssert.Contains(content, "disabled");
        Assert.IsFalse(content.Contains("must not"));
        Assert.IsFalse(content.Contains("GPU only"));
        Assert.AreNotEqual(cpu.FilePath, gpu.FilePath);
    }
    [TestMethod]
    public void OversizedLogIsExplicitlyTruncated()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", _ => { }, _root, () => true, 2048);
        log.Write("text", new string('x', 4096));
        StringAssert.Contains(File.ReadAllText(log.FilePath), "truncated");
        Assert.IsTrue(new FileInfo(log.FilePath).Length < 2048);
    }
    [TestMethod]
    public async Task RawResponseSurvivesParserFailure()
    {
        using var log = new LiteraryRequestDiagnostics("CPU", _ => { }, _root, () => true);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {broken json}\n"));
        try { await OmniLlamaProtocol.ReadAsync(stream, null, null, CancellationToken.None, onRawLine: line => log.Write("sse", line)); Assert.Fail(); }
        catch (JsonException) { }
        StringAssert.Contains(ReadLive(log.FilePath), "broken json");
    }
    [TestMethod]
    public async Task RawResponseSurvivesCancellation()
    {
        using var log = new LiteraryRequestDiagnostics("GPU", _ => { }, _root, () => true);
        using var cts = new CancellationTokenSource();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[]}\ndata: [DONE]\n"));
        try { await OmniLlamaProtocol.ReadAsync(stream, null, null, cts.Token, onRawLine: line => { log.Write("sse", line); cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }); Assert.Fail(); }
        catch (OperationCanceledException) { }
        StringAssert.Contains(ReadLive(log.FilePath), "choices");
    }
}
