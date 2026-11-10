using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace AIHub.Services.OfficeReader;

public sealed record ReaderPage(double Width, double Height);
public sealed record ReaderHeading(string Title, int Page, int Level = 0);
internal sealed record ReaderSearchResult(int? Page, bool HasText);

internal interface IReaderPages : IAsyncDisposable
{
    IReadOnlyList<ReaderPage> Pages { get; }
    Task<string> RenderAsync(int page, int width, CancellationToken token);
}

internal sealed class ReaderDocument : IAsyncDisposable
{
    private readonly SemaphoreSlim _metadata = new(1, 1);
    private bool _disposed;
    private readonly Dictionary<int, ReaderPageText> _textPages = [];
    private readonly ReaderPreviewInfo? _preview;
    internal string SourcePath { get; }
    internal string? PdfPath { get; }
    internal string Workspace { get; }
    internal IReaderPages Backend { get; }
    internal IReadOnlyList<ReaderHeading> Headings { get; }
    internal IReadOnlyList<ReaderPage> Pages => Backend.Pages;
    internal ReaderDocumentStatistics Statistics { get; }

    internal ReaderDocument(string source, string workspace, IReaderPages backend, string? pdf = null)
    {
        SourcePath = source; Workspace = workspace; Backend = backend; PdfPath = pdf;
        Statistics = new(this);
        var metadata = Path.Combine(workspace, ReaderTextPreview.MetadataName);
        if (File.Exists(metadata))
        {
            _preview = System.Text.Json.JsonSerializer.Deserialize<ReaderPreviewInfo>(File.ReadAllText(metadata))
                ?? throw new InvalidDataException("Reader.RenderFailed");
            _preview.Validate(Pages.Count);
        }
        var headings = new List<ReaderHeading>();
        if (pdf is not null)
        {
            using var document = PdfDocument.Open(pdf);
            if (document.TryGetBookmarks(out var bookmarks))
            {
                void Walk(IEnumerable<BookmarkNode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        if (node is DocumentBookmarkNode local && local.PageNumber >= 1 && local.PageNumber <= Pages.Count)
                            headings.Add(new(node.Title, local.PageNumber - 1, node.Level));
                        Walk(node.Children);
                    }
                }
                Walk(bookmarks.Roots);
            }
        }
        Headings = _preview?.Headings ?? (IReadOnlyList<ReaderHeading>)headings;
    }

    internal async Task<IReadOnlyList<ReaderLink>> ReadLinksAsync(int page, CancellationToken token)
    {
        await _metadata.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _preview is not null ? _preview.Links[page] : await Task.Run(() => ReaderLinks.Read(PdfPath, page), token);
        }
        finally { _metadata.Release(); }
    }

    internal async Task<ReaderPageText> ReadTextAsync(int page, CancellationToken token)
    {
        await _metadata.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_textPages.TryGetValue(page, out var cached)) return cached;
            var result = await Task.Run(() => ReaderPageText.Read(PdfPath, page, token), token);
            if (_textPages.Count >= 8) _textPages.Remove(_textPages.Keys.First());
            _textPages[page] = result;
            return result;
        }
        finally { _metadata.Release(); }
    }

    internal async Task<ReaderSearchResult> FindAsync(string text, int start, CancellationToken token)
    {
        await _metadata.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Find(text, start, token), token);
        }
        finally { _metadata.Release(); }
    }

    private ReaderSearchResult Find(string text, int start, CancellationToken token)
    {
        if (PdfPath is null || string.IsNullOrWhiteSpace(text)) return new ReaderSearchResult(null, false);
        using var pdf = PdfDocument.Open(PdfPath);
        var hasText = false;
        for (var offset = 0; offset < Pages.Count; offset++)
        {
            token.ThrowIfCancellationRequested();
            var index = (Math.Max(0, start) + offset) % Pages.Count;
            var content = string.Join(" ", pdf.GetPage(index + 1).GetWords().Select(word => word.Text));
            hasText |= !string.IsNullOrWhiteSpace(content);
            if (content.Contains(text, StringComparison.CurrentCultureIgnoreCase)) return new ReaderSearchResult(index, true);
        }
        return new ReaderSearchResult(null, hasText);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await Statistics.DisposeAsync();
        await Backend.DisposeAsync();
        await _metadata.WaitAsync();
        try { _textPages.Clear(); ReaderPreparation.DeleteWorkspace(Workspace); }
        finally { _metadata.Release(); }
    }
}
