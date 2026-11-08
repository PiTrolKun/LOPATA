using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderTextRun(int Start, int Length, double Left, double Top, double Width, double Height,
    int WordStart, int WordEnd);

/// <summary>Original PDF text and normalized hit boxes; the page bitmap remains unchanged.</summary>
internal sealed record ReaderPageText(string Text, IReadOnlyList<ReaderTextRun> Runs)
{
    internal static ReaderPageText Empty { get; } = new("", []);
    internal ReaderTextRun? Nearest(double x, double y) => Runs.MinBy(run =>
    {
        var dx = Math.Max(Math.Max(run.Left - x, x - run.Left - run.Width), 0);
        var dy = Math.Max(Math.Max(run.Top - y, y - run.Top - run.Height), 0);
        return dx * dx + dy * dy * 4;
    });

    internal int Hit(double x, double y)
    {
        if (Runs.Count == 0) return 0;
        if (y < Runs.Min(r => r.Top)) return 0;
        if (y > Runs.Max(r => r.Top + r.Height)) return Text.Length;
        var run = Nearest(x, y)!;
        return x < run.Left + run.Width / 2 ? run.Start : run.Start + run.Length;
    }

    internal static ReaderPageText Read(string? path, int page, CancellationToken token)
    {
        if (path is null) return Empty;
        using var pdf = PdfDocument.Open(path);
        return Read(pdf.GetPage(page + 1), token);
    }

    internal static ReaderPageText Read(UglyToad.PdfPig.Content.Page source, CancellationToken token, bool hitBoxes = true)
    {
        token.ThrowIfCancellationRequested();
        var words = source.GetWords().ToArray();
        if (words.Length == 0) return Empty;
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
        var text = new StringBuilder(); var runs = new List<ReaderTextRun>();
        foreach (var block in ordered)
        {
            foreach (var line in block.TextLines)
            {
                if (text.Length > 0) text.AppendLine();
                var first = true;
                foreach (var word in line.Words)
                {
                    token.ThrowIfCancellationRequested();
                    if (!first) text.Append(' '); first = false;
                    var start = text.Length;
                    var wordRuns = new List<ReaderTextRun>();
                    foreach (var letter in word.Letters)
                    {
                        var box = letter.BoundingBox;
                        var offset = text.Length; text.Append(letter.Value);
                        if (hitBoxes) wordRuns.Add(new(offset, text.Length - offset, box.Left / source.Width, 1 - box.Top / source.Height,
                            Math.Abs(box.Width / source.Width), Math.Abs(box.Height / source.Height), start, 0));
                    }
                    runs.AddRange(wordRuns.Select(run => run with { WordEnd = text.Length }));
                }
            }
        }
        return new(text.ToString(), runs);
    }
}
