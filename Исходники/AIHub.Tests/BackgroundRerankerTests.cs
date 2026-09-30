using System.Diagnostics;
using System.Reflection;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class BackgroundRerankerTests
{
    [TestMethod]
    public async Task CanceledRerankerWaitsForItsWorkerExit()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-background-reranker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var script = Path.Combine(root, "worker.ps1");
        var marker = Path.Combine(root, "pid.txt"); Process? worker = null;
        try
        {
            File.WriteAllText(script, "[Console]::In.ReadToEnd() | Out-Null\n[IO.File]::WriteAllText('" + marker.Replace("'", "''")
                + "', [string]$PID)\n[Threading.Thread]::Sleep(60000)");
            using var cancellation = new CancellationTokenSource();
            var method = typeof(WebSearchRerankerService).GetMethod("ExecutePythonRerankerAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var work = (Task)method.Invoke(null, ["pwsh.exe", script, root, "query", new List<WebSearchResult>
                { new() { Title = "test", Url = "https://example.invalid", Snippet = "text" } }, cancellation.Token])!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
            worker = Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(25)));
            Assert.IsTrue(worker.HasExited);
        }
        finally
        {
            if (worker is not null) { if (!worker.HasExited) worker.Kill(true); await worker.WaitForExitAsync(); worker.Dispose(); }
            var path = Path.GetFullPath(root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("lopata-background-reranker-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }
}
