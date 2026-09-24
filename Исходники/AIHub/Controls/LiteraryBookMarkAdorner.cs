using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Services.LiteraryImport;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>Paint only visible review passages without changing text or its native undo history.</summary>
internal sealed class LiteraryBookMarkAdorner(TextBox editor, ImportReviewBook book) : Adorner(editor)
{
    private static readonly SolidColorBrush MarkBrush = new(System.Windows.Media.Color.FromArgb(76, 255, 215, 0));
    protected override void OnRender(DrawingContext drawing)
    {
        if (editor.LineCount <= 0 || editor.ActualWidth <= 24 || editor.ActualHeight <= 8) return;
        var first = editor.GetFirstVisibleLineIndex(); var last = editor.GetLastVisibleLineIndex();
        if (first < 0 || last < first) return;
        drawing.PushClip(new RectangleGeometry(new Rect(2, 2, Math.Max(0, editor.ActualWidth - 23), Math.Max(0, editor.ActualHeight - 4))));
        for (var line = first; line <= last; line++)
        {
            var start = editor.GetCharacterIndexFromLineIndex(line);
            var end = start + editor.GetLineLength(line);
            foreach (var mark in book.Marks.Where(m => m.Start < end && m.Start + m.Length > start))
            {
                var from = Math.Max(start, mark.Start); var to = Math.Min(end, mark.Start + mark.Length);
                if (to <= from) continue;
                var a = editor.GetRectFromCharacterIndex(from); var b = editor.GetRectFromCharacterIndex(to - 1, true);
                if (a.IsEmpty || b.IsEmpty) continue;
                drawing.DrawRoundedRectangle(MarkBrush, null, new Rect(a.X, a.Y, Math.Max(4, b.Right - a.X), a.Height), 2, 2);
            }
        }
        drawing.Pop();
    }
}
