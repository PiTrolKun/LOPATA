using System.IO;
using AIHub.Controls.OfficeReader;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeReaderStatisticsTests
{
    [TestMethod]
    public void CountsUnicodeSpacesAndLogicalLinesWithoutDependingOnLineEndingStyle()
    {
        Assert.AreEqual(new ReaderTextStatistics(3, 9), ReaderTextStatistics.Count("Я 😀\r\nтест\n  "));
        Assert.AreEqual(new ReaderTextStatistics(3, 9), ReaderTextStatistics.Count("Я 😀\nтест\r  "));
        Assert.AreEqual(3L, ReaderTextStatistics.Count("Я 😀\nтест\n  ").ApproximateTokens);
        Assert.AreEqual(default, ReaderTextStatistics.Count(""));
        Assert.AreEqual(0L, new ReaderTextStatistics().ApproximateTokens);
        Assert.AreEqual(1L, ReaderTextStatistics.Count("я").ApproximateTokens);
    }

    [TestMethod]
    public async Task WholeDocumentAndReversedSelectionAgreeWithTheActualCopiedPdfText()
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-statistics-" + Guid.NewGuid().ToString("N"));
        var file = OfficeReaderTextTests.MakePdf(root, "sample.pdf");
        var document = new ReaderDocument(file, Path.Combine(root, "workspace"), new TestPages(), file);
        try
        {
            var all = await document.Statistics.ReadAllAsync(CancellationToken.None);
            Assert.AreEqual(new ReaderTextStatistics(3, 62), all);
            Assert.AreEqual(20L, all.ApproximateTokens);
            // A second whole-book request uses numeric totals without reopening the source.
            File.Move(file, file + ".moved");
            Assert.AreEqual(all, await document.Statistics.ReadAllAsync(CancellationToken.None));
            File.Move(file + ".moved", file);
            var selection = new ReaderTextSelection(); selection.Begin(1, 6, false); selection.Extend(0, 6);
            var bounds = selection.Bounds!.Value;
            var statistics = await document.Statistics.ReadSelectionAsync(bounds.Start.Page, bounds.Start.Offset, bounds.End.Page, bounds.End.Offset, CancellationToken.None);
            Assert.AreEqual(ReaderTextStatistics.Count(await selection.ReadAsync(document, CancellationToken.None)), statistics);
            Assert.AreEqual(all, await document.Statistics.ReadSelectionAsync(0, 0, 1, int.MaxValue, CancellationToken.None));
            Assert.AreEqual(new ReaderTextStatistics(1, 5), await document.Statistics.ReadSelectionAsync(0, 0, 0, 5, CancellationToken.None));
        }
        finally { await document.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationDoesNotPoisonTheDocumentAndDisposalClosesTheCounter()
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-statistics-cancel-" + Guid.NewGuid().ToString("N"));
        var file = OfficeReaderTextTests.MakePdf(root, "sample.pdf");
        var document = new ReaderDocument(file, Path.Combine(root, "workspace"), new TestPages(), file);
        try
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => document.Statistics.ReadAllAsync(cancelled.Token));
            Assert.AreEqual(62L, (await document.Statistics.ReadAllAsync(CancellationToken.None)).Characters);
            await document.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => document.Statistics.ReadAllAsync(CancellationToken.None));
        }
        finally { await document.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ImageOnlyDocumentHasNoInventedText()
    {
        await using var document = new ReaderDocument("comic.cbz", Path.GetTempPath(), new TestPages());
        Assert.AreEqual(default, await document.Statistics.ReadAllAsync(CancellationToken.None));
    }

    private sealed class TestPages : IReaderPages
    {
        public IReadOnlyList<ReaderPage> Pages { get; } = [new(600, 800), new(600, 800)];
        public Task<string> RenderAsync(int page, int width, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
