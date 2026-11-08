using UglyToad.PdfPig;

namespace AIHub.Services.OfficeReader;

/// <summary>One background pass, one open PDF, only per-page numeric totals retained.</summary>
internal sealed class ReaderDocumentStatistics(ReaderDocument document) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<ReaderTextStatistics[]>? _pages;
    private bool _disposed;

    private Task<ReaderTextStatistics[]> PagesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pages ??= Task.Run(() => ReadPages(_lifetime.Token), _lifetime.Token);
            return _pages.WaitAsync(token);
        }
    }

    private ReaderTextStatistics[] ReadPages(CancellationToken token)
    {
        var result = new ReaderTextStatistics[document.Pages.Count];
        if (document.PdfPath is null) return result;
        using var pdf = PdfDocument.Open(document.PdfPath);
        for (var page = 0; page < result.Length; page++)
        {
            token.ThrowIfCancellationRequested();
            var text = ReaderPageText.Read(pdf.GetPage(page + 1), token, hitBoxes: false).Text;
            result[page] = ReaderTextStatistics.Count(text);
        }
        return result;
    }

    internal async Task<ReaderTextStatistics> ReadAllAsync(CancellationToken token)
    {
        var result = new ReaderTextStatistics();
        foreach (var page in await PagesAsync(token).ConfigureAwait(false)) result += page;
        return result;
    }

    internal async Task<ReaderTextStatistics> ReadSelectionAsync(int startPage, int startOffset,
        int endPage, int endOffset, CancellationToken token)
    {
        var result = new ReaderTextStatistics();
        if (startPage < 0 || endPage < startPage || endPage >= document.Pages.Count) return result;
        // Full middle pages use scalar totals; Ctrl+A never concatenates the whole book.
        var totals = endPage > startPage ? await PagesAsync(token).ConfigureAwait(false) : null;
        for (var page = startPage; page <= endPage; page++)
        {
            token.ThrowIfCancellationRequested();
            if (totals is not null && (page != startPage || startOffset == 0) && (page != endPage || endOffset == int.MaxValue))
            { result += totals[page]; continue; }
            var text = (await document.ReadTextAsync(page, token).ConfigureAwait(false)).Text;
            var start = page == startPage ? Math.Clamp(startOffset, 0, text.Length) : 0;
            var end = page == endPage ? Math.Clamp(endOffset, 0, text.Length) : text.Length;
            result += ReaderTextStatistics.Count(text.AsSpan(start, Math.Max(0, end - start)));
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        Task<ReaderTextStatistics[]>? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); pending = _pages;
        }
        try { if (pending is not null) await pending.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { /* The requesting UI reports extraction failures; cancellation also belongs to normal close. */ }
        finally { _lifetime.Dispose(); }
    }
}
