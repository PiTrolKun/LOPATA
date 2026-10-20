using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Services.Books;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIHub.Controls;

/// <summary>Presentation only. It never receives or mutates the export model or a file.</summary>
internal static class BookReviewHighlightLayer
{
    internal static int Apply(FlowDocument document, BookReviewEvidence evidence, bool isCurrent, Func<string, string> l)
    {
        var runs = new List<(Run Run, int Start)>(); var text = new StringBuilder();
        void WalkBlocks(BlockCollection blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph paragraph: WalkInlines(paragraph.Inlines); text.Append('\n'); break;
                    case Section section: WalkBlocks(section.Blocks); break;
                    case System.Windows.Documents.List list:
                        foreach (var item in list.ListItems) WalkBlocks(item.Blocks); break;
                    case Table table:
                        foreach (var group in table.RowGroups)
                            foreach (var row in group.Rows)
                                foreach (var cell in row.Cells) WalkBlocks(cell.Blocks); break;
                }
        }
        void WalkInlines(InlineCollection inlines)
        {
            foreach (var inline in inlines)
                switch (inline)
                {
                    case Run run: runs.Add((run, text.Length)); text.Append(run.Text); break;
                    case Span span: WalkInlines(span.Inlines); break;
                    case LineBreak: text.Append('\n'); break;
                    case InlineUIContainer: text.Append('\n'); break;
                }
        }
        WalkBlocks(document.Blocks);
        var resolution = BookReviewEvidenceResolver.Resolve(text.ToString(), evidence, isCurrent);
        // Each character stores the winning evidence. Red always overrides green.
        var owners = new BookReviewMark?[text.Length];
        foreach (var resolved in resolution.Marks)
            for (var index = resolved.Start; index < resolved.Start + resolved.Length; index++)
                if (owners[index]?.Kind != BookReviewMarkKind.Questionable) owners[index] = resolved.Mark;
        foreach (var (run, start) in runs)
        {
            if (run.Text.Length == 0 || !owners.Skip(start).Take(run.Text.Length).Any(mark => mark is not null)) continue;
            var replacement = new Span();
            foreach (var property in new[] { TextElement.ForegroundProperty, TextElement.BackgroundProperty, TextElement.FontFamilyProperty,
                TextElement.FontSizeProperty, TextElement.FontWeightProperty, TextElement.FontStyleProperty })
                replacement.SetValue(property, run.GetValue(property));
            for (var index = 0; index < run.Text.Length;)
            {
                var mark = owners[start + index]; var end = index + 1;
                while (end < run.Text.Length && ReferenceEquals(owners[start + end], mark)) end++;
                var part = new Run(run.Text[index..end]);
                if (mark is not null)
                {
                    var red = mark.Kind == BookReviewMarkKind.Questionable;
                    part.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(red ? "#FEE2E2" : "#D1FAE5"));
                    part.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(red ? "#991B1B" : "#065F46"));
                    part.ToolTip = l("BookConverter.Review." + (red ? "Questionable" : "AiProcessed")) + "\n" +
                        Path.GetFileName(mark.SourcePath) + (mark.Page is { } page ? " · " + string.Format(l("BookConverter.Review.SourcePage"), page) : "") +
                        (string.IsNullOrWhiteSpace(mark.Reason) ? "" : "\n" + mark.Reason);
                }
                replacement.Inlines.Add(part); index = end;
            }
            var parent = run.Parent switch { Paragraph paragraph => paragraph.Inlines, Span span => span.Inlines, _ => null };
            if (parent is not null) { parent.InsertBefore(run, replacement); parent.Remove(run); }
        }
        return resolution.Unresolved;
    }
}
