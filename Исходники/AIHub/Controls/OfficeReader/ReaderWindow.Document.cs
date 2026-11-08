using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.OfficeReader;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private int _openRevision;
    private bool _finding;

    private void SelectFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("Open"), CheckFileExists = true, Multiselect = true,
            Filter = T("Files") + "|" + string.Join(";", ReaderPreparation.SupportedExtensions.Select(e => "*" + e)) + "|" + T("AllFiles") + "|*.*"
        };
        if (dialog.ShowDialog(this) == true) _windowGroup.OpenFiles(dialog.FileNames, this);
    }

    public Task OpenAsync(string path)
    {
        if (string.Equals(Path.GetExtension(path), Services.OfficeEditor.OfficePackage.Extension, StringComparison.OrdinalIgnoreCase))
        {
            if (_editor is not null)
            {
                _windowGroup.OpenEditor(path);
                return Task.CompletedTask;
            }
            SourcePath = Path.GetFullPath(path);
            return SwitchToEditingAsync();
        }
        return LoadReaderDocumentAsync(path, null);
    }

    private async Task LoadReaderDocumentAsync(string path, ReaderState? previewState)
    {
        if (_closed) return;
        var revision = ++_openRevision;
        SavePosition(); _load?.Cancel();
        _selection.Clear();
        if (previewState is null)
        {
            SourcePath = Path.GetFullPath(path);
            _pendingAiActions.Clear();
        }
        var previous = _document; _document = null;
        ResetStatistics();
        _pages.ItemsSource = null; _spread.Children.Clear(); _slots = []; _contents.ItemsSource = null; _linkHistory.Clear();
        var oldLoad = _load;
        var lifetime = _load = new CancellationTokenSource();
        _loading = true; _file.Text = Path.GetFileName(path); _status.Text = T("Loading");
        _progress.Visibility = _cancel.Visibility = Visibility.Visible; UpdateNavigation();
        try
        {
            await CloseDocumentSpeechAsync();
            await CloseDocumentTranslationAsync(previous);
            if (previous is not null) await previous.DisposeAsync();
            oldLoad?.Dispose();
            var progress = new Progress<string>(key =>
            {
                if (revision == _openRevision && !_closed && _loading) _status.Text = _l(key);
            });
            var displayPath = previewState is null ? path : SourcePath ??
                Path.Combine(Path.GetDirectoryName(path)!, _l("OfficeEditor.NewDocument") + ".LOPATAdoxAI");
            var document = await Task.Run(() => ReaderPreparation.OpenAsync(path, progress, lifetime.Token, displayPath), lifetime.Token);
            if (_closed || revision != _openRevision || lifetime.IsCancellationRequested)
            { await document.DisposeAsync(); return; }
            _document = document;
            var state = previewState ?? ReaderStateStore.Load(path);
            _zoom = ReaderNavigation.ClampZoom(state.Zoom); _book = state.Book; _dark = state.Dark ?? _dark;
            _page = ReaderNavigation.ClampPage(state.Page, document.Pages.Count);
            PresentDocument(document, _page);
            Title = Path.GetFileName(path) + " · " + T("Title");
            _status.Text = T("ReadOnly");
        }
        catch (OperationCanceledException) { if (revision == _openRevision && !_closed) _status.Text = T("Cancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { if (revision == _openRevision && !_closed) ShowFailure(error); }
        finally
        {
            if (revision == _openRevision && !_closed)
            { _loading = false; _progress.Visibility = _cancel.Visibility = Visibility.Collapsed; UpdateNavigation(); }
        }
    }

    private void PresentDocument(ReaderDocument document, int page)
    {
            CancelStatistics(); _selection.Clear(); _linkHistory.Clear();
            _pages.ItemsSource = null; _spread.Children.Clear(); _document = document;
            _page = ReaderNavigation.ClampPage(page, document.Pages.Count);
            _file.Text = Path.GetFileName(document.SourcePath) + (ReferenceEquals(document, _translatedDocument) && _translationResult is { } result ? " · " + result.Source + " → " + result.Target : "");
            _slots = Enumerable.Range(0, document.Pages.Count).Select(index => new ReaderPageSlot(index, document, _load!.Token)
            {
                Selection = _selection, Localize = _l,
                Failed = error => { if (ReferenceEquals(document, _document)) ShowFailure(error); },
                FollowLink = target => { _linkHistory.Push(_page); Go(target); }
            }).ToList();
            _pages.ItemsSource = _slots;
            _contents.ItemsSource = document.Headings.Count > 0 ? document.Headings :
                Enumerable.Range(0, document.Pages.Count).Select(i => new ReaderHeading(T("Page") + " " + (i + 1), i)).ToArray();
            ApplyTheme(); ShowMode(); ResizePages(true); Go(_page);
            QueueStatistics();
    }

    private void ShowFailure(Exception error)
    {
        if (_closed) return;
        OwnedProcessRegistry.Log("office_reader_failed", "Office.Reader", detail: error.GetType().Name + ": " + error.Message);
        _status.Text = error.Message.StartsWith("Reader.", StringComparison.Ordinal) ? _l(error.Message) : T("OpenFailed");
        _status.ToolTip = _status.Text;
    }

    private void SavePosition()
    {
        if (_document is not null && SourcePath is { } source)
            ReaderStateStore.Save(source, new(_page, _zoom, _book, _dark));
    }

    private async Task FindAsync()
    {
        var document = _document; var lifetime = _load;
        if (document is null || lifetime is null || _finding || string.IsNullOrWhiteSpace(_search.Text)) return;
        if (document.PdfPath is null) { _status.Text = T("NoText"); return; }
        _finding = true; _status.Text = T("Searching");
        try
        {
            var result = await document.FindAsync(_search.Text.Trim(), (_page + 1) % document.Pages.Count, lifetime.Token);
            if (!ReferenceEquals(document, _document) || _closed) return;
            if (!result.HasText) _status.Text = T("NoText");
            else if (result.Page is { } page) { Go(page); _status.Text = T("Found"); }
            else _status.Text = T("NotFound");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { ShowFailure(error); }
        finally { _finding = false; }
    }
}
