using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryReadinessTests
{
    [TestMethod]
    public void ParsesQuotedNameAndSelectsCudaAdapterRatherThanFirstRow()
    {
        var rows = LiteraryGpuTelemetry.Parse("00000000:02:00.0,\"GPU, A\", 8192, 1024, 7168\n00000000:01:00.0,GPU B,32768,20000,12768");
        Assert.AreEqual("GPU, A", rows[0].Name);
        var selected = LiteraryGpuTelemetry.Select(rows, "0000:01:00.0")!;
        Assert.AreEqual("GPU B", selected.Name);
        Assert.AreEqual(12768 * LiteraryAutomaticBudget.MiB, selected.FreeBytes);
    }
    [TestMethod]
    public void UnknownOrAmbiguousIdentityNeverSelectsArbitraryAdapter()
    {
        var rows = LiteraryGpuTelemetry.Parse("0000:01:00.0,GPU A,8192,0,8192");
        Assert.IsNull(LiteraryGpuTelemetry.Select(rows, null));
        Assert.IsNull(LiteraryGpuTelemetry.Select(rows, "0000:02:00.0"));
        Assert.IsNull(LiteraryGpuTelemetry.Select([rows[0], rows[0]], "0000:01:00.0"));
    }
    [TestMethod]
    [DataRow("0000:01:00.0,GPU,8192,N/A,0")]
    [DataRow("0000:01:00.0,GPU,0,0,0")]
    [DataRow("0000:01:00.0,GPU,8192,-1,0")]
    [DataRow("0000:01:00.0,GPU,8192,0,9000")]
    [DataRow("bad,GPU,8192,0,0")]
    [DataRow("0000:01:00.0,GPU,8192,0")]
    public void InvalidReadingsAreUnavailableRatherThanGreenOrZero(string text)
    {
        Assert.Throws<FormatException>(() => LiteraryGpuTelemetry.Parse(text));
    }
    [TestMethod]
    public void SpareIsPhysicalReserveNotWddmReclaimableDifference()
    {
        var mib = LiteraryAutomaticBudget.MiB;
        Assert.AreEqual(1200 * mib, LiteraryAutomaticBudget.SpareBytes(16000 * mib, 12000 * mib));
        Assert.AreEqual(5200, LiteraryAutomaticBudget.FitMargin(16000 * mib, 12000 * mib));
        Assert.AreEqual(1024 * mib, LiteraryAutomaticBudget.SpareBytes(2000 * mib, 1000 * mib));
    }
    [TestMethod]
    public void ColdRuntimeNeverInventsLoadedModelEstimate()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LOPATA-readiness-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, ".creation"), "");
            using var runtime = new LiteraryChatRuntime(root, preparing: true);
            Assert.IsNull(runtime.RecommendedGpuSpareBytes);
        }
        finally { System.IO.Directory.Delete(root, true); }
    }
}
