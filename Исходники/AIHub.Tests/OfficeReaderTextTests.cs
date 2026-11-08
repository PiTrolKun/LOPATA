using System.IO;
using AIHub.Controls.OfficeReader;
using AIHub.Services.OfficeReader;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeReaderTextTests
{
    internal static string MakePdf(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(600, 800);
        first.AddText("First page Alpha", 18, new PdfPoint(50, 730), font);
        first.AddText("Another line of original text.", 18, new PdfPoint(50, 690), font);
        var second = builder.AddPage(600, 800);
        second.AddText("Second page Beta", 18, new PdfPoint(50, 730), font);
        var path = Path.Combine(directory, name); File.WriteAllBytes(path, builder.Build()); return path;
    }

    [TestMethod]
    public void TextAndHitBoxesMatchActualPdfCoordinates()
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = MakePdf(root, "sample.pdf"); var page = ReaderPageText.Read(path, 0, CancellationToken.None);
            StringAssert.Contains(page.Text, "First page Alpha"); StringAssert.Contains(page.Text, "Another line");
            var first = page.Runs.First();
            Assert.AreEqual(0, first.Start); Assert.AreEqual(50.0 / 600, first.Left, .003);
            Assert.IsTrue(first.Top > .05 && first.Top < .15);
            Assert.AreEqual(first.Start, page.Hit(first.Left, first.Top + first.Height / 2));
            Assert.AreEqual(first.Start + first.Length, page.Hit(first.Left + first.Width, first.Top + first.Height / 2));
            Assert.AreEqual(0, page.Hit(0, -1)); Assert.AreEqual(page.Text.Length, page.Hit(1, 2));
            Assert.AreEqual("First", page.Text[first.WordStart..first.WordEnd]);
            Assert.AreEqual("", ReaderPageText.Read(null, 0, CancellationToken.None).Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SelectionCopiesExactFragmentsAcrossPagesInBothDirections()
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-copy-" + Guid.NewGuid().ToString("N"));
        var file = MakePdf(root, "sample.pdf");
        var document = new ReaderDocument(file, Path.Combine(root, "workspace"), new TestPages(), file);
        try
        {
            var first = await document.ReadTextAsync(0, CancellationToken.None);
            var second = await document.ReadTextAsync(1, CancellationToken.None);
            var selection = new ReaderTextSelection(); selection.Begin(1, 6, false); selection.Extend(0, 6);
            Assert.AreEqual(first.Text[6..] + Environment.NewLine + second.Text[..6], await selection.ReadAsync(document, CancellationToken.None));
            Assert.AreEqual((6, first.Text.Length - 6), selection.Range(0, first.Text.Length));
            Assert.AreEqual((0, 6), selection.Range(1, second.Text.Length));
            selection.SelectAll(2);
            Assert.AreEqual(first.Text + Environment.NewLine + second.Text, await selection.ReadAsync(document, CancellationToken.None));
            selection.Clear(); Assert.IsFalse(selection.HasSelection); Assert.AreEqual("", await selection.ReadAsync(document, CancellationToken.None));
        }
        finally { await document.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class TestPages : IReaderPages
    {
        public IReadOnlyList<ReaderPage> Pages { get; } = [new(600, 800), new(600, 800)];
        public Task<string> RenderAsync(int page, int width, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
