namespace AIHub.Services.Books;

public enum BookReviewDecision { Save, Skip, Cancel }

/// <summary>One private target-stage review. Publication remains the converter's responsibility.</summary>
public sealed class BookReviewSession
{
    private readonly object _state = new();
    private BookReviewDocument _document;
    private readonly Func<BookReviewDocument, CancellationToken, Task<BookReviewDocument>> _refresh;
    private int _revision;
    private int _previewRevision;
    private bool _refreshing;
    private BookReviewEvidence _evidence = new([], []);
    private int _evidenceRevision;
    /// <summary>Evidence is bound to this exact converted revision, never to a mutable source DOM.</summary>
    public BookReviewEvidence Evidence
    {
        get { lock (_state) return new(_evidence.Sources.ToArray(), _evidence.Marks.ToArray()); }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_state) { _evidence = new(value.Sources.ToArray(), value.Marks.ToArray()); _evidenceRevision = _revision; }
        }
    }
    public bool IsEvidenceCurrent { get { lock (_state) return _evidenceRevision == _revision; } }
    public string PreviewPath { get; }
    public string OutputFormat { get; }
    public string SourceName { get; }
    public BookReviewDocument Document { get { lock (_state) return _document.Clone(); } }
    public int Revision { get { lock (_state) return _revision; } }
    public int PreviewRevision { get { lock (_state) return _previewRevision; } }
    public bool IsDirty { get { lock (_state) return _revision != _previewRevision; } }
    public bool IsRefreshing { get { lock (_state) return _refreshing; } }
    public bool CanSave { get { lock (_state) return !_refreshing && _revision == _previewRevision; } }
    internal BookReviewSession(string previewPath, string outputFormat, string sourceName, BookReviewDocument document, Func<BookReviewDocument, CancellationToken, Task<BookReviewDocument>> refresh)
    {
        PreviewPath = previewPath; OutputFormat = outputFormat; SourceName = sourceName; _document = document; _refresh = refresh;
    }
    public void SetDocument(BookReviewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_state)
        {
            if (_refreshing) throw new InvalidOperationException("BookConverter.Review.Busy");
            if (document.Origin != _document.Origin) throw new ArgumentException("BookConverter.Review.DocumentMismatch", nameof(document));
            if (document.CanonicalHtml() == _document.CanonicalHtml()) return;
            _document = document.Clone(); _revision++;
        }
    }
    public async Task RefreshPreviewAsync(CancellationToken cancellationToken = default)
    {
        BookReviewDocument snapshot;
        lock (_state)
        {
            if (_refreshing) throw new InvalidOperationException("BookConverter.Review.Busy");
            if (_revision == _previewRevision) return;
            _refreshing = true; snapshot = _document.Clone();
        }
        try
        {
            var rendered = await _refresh(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_state) { _document = rendered; _previewRevision = _revision; }
        }
        finally { lock (_state) _refreshing = false; }
    }
}
