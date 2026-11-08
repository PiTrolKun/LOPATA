using System.Text;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderSpeechRange(int FirstPage, int Count, int FirstOffset = 0, int LastOffset = int.MaxValue)
{
    internal async Task<string> ReadAsync(ReaderDocument document, CancellationToken token)
    {
        if (FirstPage < 0 || Count < 1 || Count > document.Pages.Count - FirstPage)
            throw new ArgumentOutOfRangeException(nameof(FirstPage));
        var text = new StringBuilder();
        for (var page = FirstPage; page < FirstPage + Count; page++)
        {
            token.ThrowIfCancellationRequested();
            var original = (await document.ReadTextAsync(page, token)).Text;
            var start = page == FirstPage ? Math.Clamp(FirstOffset, 0, original.Length) : 0;
            var end = page == FirstPage + Count - 1 ? Math.Clamp(LastOffset, 0, original.Length) : original.Length;
            if (text.Length > 0) text.AppendLine();
            text.Append(original.AsSpan(start, Math.Max(0, end - start)));
        }
        return text.ToString();
    }
}
