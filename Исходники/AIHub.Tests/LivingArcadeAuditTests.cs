using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeAuditTests
{
    [TestMethod]
    public void RejectedLevelIsKnownContentFailureWhileBrokenComponentStillRequiresDiagnosis()
    {
        Assert.IsFalse(ComponentAuditFailures.IsEligible(new InvalidDataException("Arcade.Ai.InvalidOutput")));
        Assert.IsFalse(ComponentAuditFailures.IsEligible(new BackgroundOperationFailedException("Arcade.Ai.Rejected")));
        Assert.IsFalse(ComponentAuditFailures.IsEligible(new InvalidDataException("LivingArcade.LevelCostExceeded")));
        Assert.IsTrue(ComponentAuditFailures.IsEligible(new DllNotFoundException("native runtime unavailable")));
        Assert.IsTrue(ComponentAuditFailures.IsEligible(new IOException("Managed model payload is corrupt")));
    }
}
