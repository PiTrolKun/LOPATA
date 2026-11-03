using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LetItOutContextWindowTests
{
    [TestMethod]
    public async Task ExtraReserveUsesTenPercentOfCapacityAndWholeBoundaries()
    {
        var result = await LetItOutContextWindow.ExtraReserveAsync(9000, 10000, [2,4,6,8],
            end => Task.FromResult(9000 - end * 180));
        Assert.AreEqual(6, result.Boundary); Assert.AreEqual(7920, result.Used);
        Assert.IsTrue(result.Used <= 8000); Assert.IsTrue(9000 - (result.Boundary - 2) * 180 > 8000);
    }

    [TestMethod]
    public async Task SmallHistoryReleasesOnlyWhatCanBeRemovedAndNeverRequiresAnEmptyWindow()
    {
        var result = await LetItOutContextWindow.ExtraReserveAsync(800, 16000, [2,4],
            end => Task.FromResult(800 - end * 100));
        Assert.AreEqual(4, result.Boundary); Assert.AreEqual(400, result.Used);
        var unchanged = await LetItOutContextWindow.ExtraReserveAsync(400, 16000, [], _ => throw new InvalidOperationException());
        Assert.AreEqual((0,400), unchanged);
    }

    [TestMethod]
    public async Task RollingFitRemovesWholeOldExchangesAndIncludesReplyHeadroom()
    {
        var messages = new List<LetItOutChatMessage> { new("user","Old question"),new("assistant","Old answer"),
            new("user","Latest question"),new("assistant","Latest answer"),new("user","Current draft") };
        var used = await LetItOutContextWindow.FitAsync(messages, 6100, list => Task.FromResult(700 + 1000 * list.Count));
        Assert.AreEqual(3, messages.Count); Assert.AreEqual("Latest question",messages[0].Content);
        Assert.AreEqual("Current draft",messages[^1].Content); Assert.AreEqual(3700,used);
    }

    [TestMethod]
    public async Task OneHugeCurrentMessageFailsWithoutDiscardingIt()
    {
        var messages = new List<LetItOutChatMessage> { new("user","Current message") };
        await Assert.ThrowsAsync<LetItOutChatContextException>(() => LetItOutContextWindow.FitAsync(messages,8192,_ => Task.FromResult(9000)));
        Assert.AreEqual(1,messages.Count);
    }
}
