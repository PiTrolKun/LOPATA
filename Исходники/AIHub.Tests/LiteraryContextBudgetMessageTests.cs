using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryContextBudgetMessageTests
{
    [TestMethod]
    public void RejectedBudgetKeepsMeasuredValuesAndReplyReserve()
    {
        var error = Assert.Throws<ImageAnalysisContextExhaustedException>(() => LiteraryAutomaticBudget.Reply(5376, 5117));
        Assert.AreEqual(new ModelContextBudgetSnapshot(5117, 5376, 256, 256), error.Budget);
        Assert.IsFalse(error.OutputTruncated);
        Assert.AreEqual("capacity=5376 input=5117 safety=256 reply=256",
            LiteraryContextBudgetMessage.Format(error, key => key == "Literary.Context.BudgetDetails"
                ? "capacity={0} input={1} safety={2} reply={3}" : key));
        Assert.AreEqual(256, LiteraryAutomaticBudget.Reply(5376, 4864));
    }

    [TestMethod]
    public void MissingMeasurementsAndTruncatedOutputKeepDistinctFallbacks()
    {
        var unknown = new ImageAnalysisContextExhaustedException("native error");
        Assert.AreEqual("input-limit", LiteraryContextBudgetMessage.Format(unknown, key => key, "input-limit", "output-limit"));
        var truncated = new ImageAnalysisContextExhaustedException("length", true, new(5117, 5376, 256, 256));
        Assert.AreEqual("output-limit", LiteraryContextBudgetMessage.Format(truncated, key => key, "input-limit", "output-limit"));
        var invalid = Assert.Throws<ImageAnalysisContextExhaustedException>(() => LiteraryAutomaticBudget.Reply(5376, -1));
        Assert.IsNull(invalid.Budget);
    }
}
