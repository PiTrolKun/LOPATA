using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryAutomaticBudgetTests
{
    [TestMethod]
    public void TheReportedFailedRequestCanUseTheRemainingWindow()
    {
        Assert.AreEqual(10976, LiteraryAutomaticBudget.Reply(16384,5152));
        Assert.Throws<ImageAnalysisContextExhaustedException>(()=>LiteraryAutomaticBudget.Reply(4096,int.MaxValue));
        Assert.Throws<ImageAnalysisContextExhaustedException>(()=>LiteraryAutomaticBudget.Reply(4096,-1));
    }
    [TestMethod]
    public void WddmReclaimableMemoryIsNotCountedAsPhysicalHeadroom()
    {
        const long mib=1024*1024;
        var margin=LiteraryAutomaticBudget.FitMargin(23000*mib,18000*mib);
        Assert.AreEqual(6800,margin);
        Assert.AreEqual(16200,23000-margin);
        Assert.AreEqual(1024,LiteraryAutomaticBudget.FitMargin(8000*mib,10000*mib));
        Assert.Throws<IOException>(()=>LiteraryAutomaticBudget.FitMargin(23000*mib,0));
    }
}
