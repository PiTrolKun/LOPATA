using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryLoopTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LiteraryLoops", name));
    private static void Feed(string text, int chunkSize = 1)
    {
        var detector = new LiteraryLoopDetector();
        for (var i = 0; i < text.Length; i += chunkSize) detector.Append(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
        detector.Complete();
    }

    [TestMethod]
    public void RealCyclesAreDetectedAcrossArbitraryStreamBoundaries()
    {
        foreach (var seed in new[] { 20, 24, 26, 29, 32, 34 })
            foreach (var size in new[] { 1, 37, 4096 })
                Assert.Throws<LiteraryLoopException>(() => Feed(Fixture($"rune_{seed}.txt"), size));
        var error = Assert.Throws<LiteraryLoopException>(() => Feed(Fixture("alpha_numeric.txt")));
        Assert.AreEqual("numeric_template", error.Evidence.Kind);
    }

    [TestMethod]
    public void SuccessfulStoriesQuotationsAndShortIntentionalRefrainPass()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LiteraryLoops"), "*.txt"))
        {
            if (Path.GetFileName(file).StartsWith("rune_") || Path.GetFileName(file) == "alpha_numeric.txt") continue;
            Feed(File.ReadAllText(file));
        }
        Feed(string.Concat(Enumerable.Repeat("Мы вернёмся домой.\n", 10)));
        Feed(new string('я', 100000)); // A huge unbroken word cannot grow detector state indefinitely.
    }

    [TestMethod]
    public async Task StreamLoopThrowsBeforeDoneAndPreservesVisiblePartial()
    {
        var visible = new StringBuilder();
        var source = Sse(Fixture("rune_34.txt"), "stop");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var doneSeen = false;
        await Assert.ThrowsAsync<LiteraryLoopException>(() => LiteraryLoopStream.ReadAsync(stream, new Capture(visible),
            line => doneSeen |= line.Contains("[DONE]"), CancellationToken.None));
        Assert.IsFalse(doneSeen);
        Assert.IsTrue(visible.Length > 0);
        Assert.IsTrue(visible.Length < Fixture("rune_34.txt").Length);
    }

    [TestMethod]
    public async Task CleanRecoveryKeepsOnlySuccessfulReturnAndAllowsOnlyOneRetry()
    {
        var attempts = new List<bool>(); var notices = 0;
        var result = await LiteraryLoopRecovery.RunAsync(recovery =>
        {
            attempts.Add(recovery);
            if (!recovery) throw Loop();
            return Task.FromResult("Законченный ответ");
        }, _ => { notices++; return Task.CompletedTask; }, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { false, true }, attempts);
        Assert.AreEqual(1, notices); Assert.AreEqual("Законченный ответ", result);

        attempts.Clear(); notices = 0;
        await Assert.ThrowsAsync<LiteraryLoopException>(() => LiteraryLoopRecovery.RunAsync(recovery =>
        { attempts.Add(recovery); throw Loop(); }, _ => { notices++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.AreEqual(2, attempts.Count); Assert.AreEqual(1, notices);
    }

    [TestMethod]
    public async Task CancellationDuringRecoveryCannotStartSecondGeneration()
    {
        using var cts = new CancellationTokenSource(); var attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => LiteraryLoopRecovery.RunAsync(_ =>
        { attempts++; throw Loop(); }, _ => { cts.Cancel(); return Task.CompletedTask; }, cts.Token));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task TransportErrorAndLengthLimitAreNotRetried()
    {
        foreach (var error in new Exception[] { new IOException("disconnect"), new OperationCanceledException(),
            new ImageAnalysisContextExhaustedException("limit", outputTruncated: true) })
        {
            var attempts = 0; var notices = 0;
            try
            {
                await LiteraryLoopRecovery.RunAsync(_ => { attempts++; throw error; },
                    _ => { notices++; return Task.CompletedTask; }, CancellationToken.None);
                Assert.Fail("Failure became success.");
            }
            catch (Exception actual) { Assert.AreSame(error, actual); }
            Assert.AreEqual(1, attempts); Assert.AreEqual(0, notices);
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Sse("Частичный ответ", "length")));
        await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() => LiteraryLoopStream.ReadAsync(stream, null, _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public void RecoveryRetainsExactInputBudgetsAndSlotsWithoutChangingNormalSampling()
    {
        foreach (var role in Enum.GetValues<LiteraryChatProfile>())
        {
            ImageAnalysisHiddenMessage[] messages = [new() { Role = "user", Content = "Дословная цитата: на старом скамейке" }];
            using var normal = JsonDocument.Parse(LiteraryModelPolicy.Request(role, messages));
            using var retry = JsonDocument.Parse(LiteraryModelPolicy.Request(role, messages, recovery: true));
            Assert.AreEqual(normal.RootElement.GetProperty("messages").GetRawText(), retry.RootElement.GetProperty("messages").GetRawText());
            foreach (var key in new[] { "max_tokens", "id_slot", "temperature" })
                Assert.AreEqual(normal.RootElement.GetProperty(key).GetRawText(), retry.RootElement.GetProperty(key).GetRawText());
            Assert.AreEqual(1.05, normal.RootElement.GetProperty("repeat_penalty").GetDouble());
            Assert.AreEqual(1.1, retry.RootElement.GetProperty("repeat_penalty").GetDouble());
            Assert.IsFalse(retry.RootElement.GetProperty("cache_prompt").GetBoolean());
        }
    }

    private static LiteraryLoopException Loop() => new(new("literal", 2, 48));
    private sealed class Capture(StringBuilder text) : IProgress<ModelStreamChunk>
    { public void Report(ModelStreamChunk value) => text.Append(value.Text); }
    private static string Sse(string text, string finish)
    {
        var output = new StringBuilder();
        for (var i = 0; i < text.Length; i += 11)
            output.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text.Substring(i, Math.Min(11, text.Length - i)) } } } })).Append("\n\n");
        return output.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = finish } } }))
            .Append("\n\ndata: [DONE]\n").ToString();
    }
}
