using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderLink(double Left, double Top, double Width, double Height, int? Page, string? Note = null);

internal static class ReaderLinks
{
    internal static IReadOnlyList<ReaderLink> Read(string? path, int page)
    {
        if (path is null) return [];
        using var document = PdfDocument.Open(path);
        var source = document.GetPage(page + 1);
        var result = new List<ReaderLink>();
        foreach (var annotation in source.GetAnnotations())
        {
            var box = annotation.Rectangle;
            int? target = annotation.Action is GoToAction action && action.Destination.PageNumber >= 1 && action.Destination.PageNumber <= document.NumberOfPages
                ? action.Destination.PageNumber - 1 : null;
            var note = annotation.Type is AnnotationType.Text or AnnotationType.FreeText or AnnotationType.Highlight ? annotation.Content : null;
            if (target is null && string.IsNullOrWhiteSpace(note)) continue;
            result.Add(new(box.Left / source.Width, 1 - box.Top / source.Height,
                box.Width / source.Width, box.Height / source.Height, target, note));
        }
        return result;
    }
}
