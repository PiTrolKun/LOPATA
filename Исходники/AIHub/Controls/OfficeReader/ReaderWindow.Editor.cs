using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private readonly Grid _shell = new();
    private OfficeTextEditorView? _editor;
    private BitmapFrame? _projectIcon;
    private bool _editorReady, _closeApproved, _checkingClose;
    private Task _modeChange = Task.CompletedTask;
    private readonly Dictionary<string, (OfficeAiEntry Entry, string? ResultFile)> _pendingAiActions = [];
    private System.Windows.Controls.Button _saveReadingProject = null!;
    private System.Windows.Controls.Button _enterEditor = null!;
    internal bool IsEditing => _editor?.Visibility == Visibility.Visible;
    internal bool CanOpenReaderFile => SourcePath is null && _editor is null;

    private void MountEditor()
    {
        if (_editor is not null) return;
        if (_shell.RowDefinitions.Count == 0)
        {
            _shell.RowDefinitions.Add(new RowDefinition());
            _shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var editor = new OfficeTextEditorView(this, _l, _dark);
        _editor = editor;
        editor.ReaderTranslateRequested = async selection =>
        {
            if (!_editorReady || !_modeChange.IsCompleted || _checkingClose) return;
            _modeChange = ShowEditedDocumentAsync(editor);
            await _modeChange;
            if (!IsEditing && !_closed && !_translating)
            {
                _translationTask = TranslateAsync(selection);
                await _translationTask;
            }
        };
        editor.ReturnFromReading = async () =>
        {
            await SwitchToEditingAsync();
            return IsEditing;
        };
        editor.OpenRequested += (path, recovery) => _windowGroup.OpenEditor(path, recovery);
        editor.ReadRequested += () =>
        {
            if (_editorReady && _modeChange.IsCompleted && !_checkingClose)
                _modeChange = ShowEditedDocumentAsync(editor);
        };
        editor.TitleChanged += () =>
        {
            if (!_editorReady || !ReferenceEquals(editor, _editor)) return;
            UpdateEditorIdentity(editor);
        };
        _shell.Children.Add(editor);
        _root.Visibility = Visibility.Collapsed;
        Title = _l("OfficeEditor.Title");
    }

    private void UpdateEditorIdentity(OfficeTextEditorView editor)
    {
        SourcePath = editor.TargetPath ?? editor.SourcePath;
        Icon = editor.IsProjectWorkspace
            ? _projectIcon ??= BitmapFrame.Create(new Uri("pack://application:,,,/AIHub;component/Assets/OfficeProjectFileIcon.ico"))
            : Owner?.Icon;
        if (IsEditing) Title = editor.DocumentTitle;
    }

    internal Task SwitchToEditingAsync(string? recoveryId = null)
    {
        if (!_modeChange.IsCompleted || _checkingClose || _loading || _closed || _speaking || _translating) return _modeChange;
        return _modeChange = ActivateEditorAsync(recoveryId);
    }

    private async Task ActivateEditorAsync(string? recoveryId)
    {
        string? original = SourcePath;
        var fromReader = _root.Visibility == Visibility.Visible && _document != null;
        try
        {
            SavePosition();
            MountEditor();
            var editor = _editor!;
            editor.RestoreProjectStrip();
            _root.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            if (editor.IsDarkTheme != _dark) editor.SetTheme(_dark);
            if (!_editorReady)
            {
                var firstKind = OfficeSheetKind.Text;
                if (original is null && recoveryId is null)
                {
                    // Let the window finish its Loaded event before opening an owned modal picker.
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                    firstKind = editor.ChooseFirstSheetKind() ?? throw new OperationCanceledException();
                }
                await editor.InitializeAsync(original, CancellationToken.None, recoveryId, firstKind);
                _editorReady = true;
                if (_document is not null && _page > 0) await editor.GoToPageAsync(_page);
            }
            if (fromReader && _document != null) editor.FollowNativeReadingPosition(_page, _document.Headings);
            foreach (var (id, pending) in _pendingAiActions.ToArray())
            {
                await editor.RecordAiJournalAsync(pending.Entry, pending.ResultFile);
                _pendingAiActions.Remove(id);
            }
            UpdateEditorIdentity(editor);
            editor.ResumeEditing();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (_editorReady && _editor is not null)
            {
                ReportEditorFailure(error);
                return;
            }
            if (_editor is { } failed)
            {
                _shell.Children.Remove(failed);
                _editor = null;
                await failed.DisposeAsync();
            }
            _root.Visibility = Visibility.Visible;
            SourcePath = original;
            Title = original is null ? T("Title") : Path.GetFileName(original) + " · " + T("Title");
            if (error is OperationCanceledException)
            {
                _status.Text = T("Cancelled");
                if (_document is null && !_checkingClose) Close();
                return;
            }
            ReportEditorFailure(error);
        }
    }

    private async Task ShowEditedDocumentAsync(OfficeTextEditorView editor)
    {
        try
        {
            // Keep the editable surface mounted, including its selection and undo stack.
            // Only the immutable preview is handed to the established read/speech pipeline.
            await editor.FinishDictationAsync();
            editor.IsEnabled = false;
            string snapshot = await editor.CreateReaderSnapshotAsync();
            try
            {
                var state = new ReaderState(editor.CurrentPage, _zoom, _book, editor.IsDarkTheme);
                await LoadReaderDocumentAsync(snapshot, state);
            }
            finally { File.Delete(snapshot); }
            if (_document is null) throw new InvalidDataException("Reader.RenderFailed");
            if (!string.IsNullOrWhiteSpace(editor.NativeChapterTitle))
            {
                var matches = _document.Headings.Where(h => string.Equals(h.Title.Trim(), editor.NativeChapterTitle.Trim(),
                    StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (matches.Count == 1) Go(matches[0].Page);
            }
            _root.Visibility = Visibility.Visible;
            editor.Visibility = Visibility.Collapsed;
            if (editor.DetachProjectStripForReader() is { } tabs)
            {
                Grid.SetRow(tabs, 1);
                _shell.Children.Add(tabs);
            }
            _file.Text = SourcePath is null ? _l("OfficeEditor.NewDocument") : Path.GetFileName(SourcePath);
            Title = _file.Text + " · " + T("Title");
            UpdateReaderProjectOffer();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            ReportEditorFailure(error);
        }
        finally { editor.IsEnabled = true; }
    }

    private void ReportEditorFailure(Exception error)
    {
        OwnedProcessRegistry.Log("office_text_editor_failed", "Office.Editor", detail: error.ToString());
        if (_closed) return;
        var message = error.Message.StartsWith("Reader.", StringComparison.Ordinal) ||
            error.Message.StartsWith("OfficeEditor.", StringComparison.Ordinal)
            ? _l(error.Message) : _l("OfficeEditor.Failed");
        _status.Text = message;
        _status.ToolTip = error.Message;
        if (IsEditing) System.Windows.MessageBox.Show(this, message, _l("OfficeEditor.Title"));
    }

    private OfficeAiJournal CreateReaderAiJournal(string action)
    {
        var revision = _openRevision;
        var sheet = _editor?.CurrentSheetId ?? OfficeSheets.PrimaryId;
        return new(sheet, action, (entry, resultFile) => Dispatcher.InvokeAsync(async () =>
        {
            if (_closed || revision != _openRevision) return;
            if (_editorReady && _editor is not null) await _editor.RecordAiJournalAsync(entry, resultFile);
            else _pendingAiActions[entry.Id] = (entry, resultFile);
            UpdateReaderProjectOffer();
        }).Task.Unwrap());
    }

    private void UpdateReaderProjectOffer()
    {
        var supported = SourcePath is not null;
        _enterEditor.IsEnabled = !_loading && !_speaking && !_translating && (_editorReady || supported && _document is not null);
        var pending = _pendingAiActions.Count > 0 && supported &&
            !string.Equals(Path.GetExtension(SourcePath), OfficePackage.Extension, StringComparison.OrdinalIgnoreCase);
        _saveReadingProject.Visibility = pending || _editor?.ProjectPromptNeeded == true
            ? Visibility.Visible : Visibility.Collapsed;
        _saveReadingProject.IsEnabled = !_speaking && !_translating && !_loading;
    }

    private async Task SaveReadingProjectAsync()
    {
        try
        {
            await SwitchToEditingAsync();
            if (IsEditing && _editorReady) await _editor!.OfferProjectSaveAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { ReportEditorFailure(error); }
    }

    private async void CheckEditorBeforeClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved || _editor is null) return;
        e.Cancel = true;
        await CloseEditorAsync();
    }

    internal async Task<bool> CloseEditorAsync()
    {
        if (_editor is null || _closed) return true;
        if (_checkingClose) return false;
        _checkingClose = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await _modeChange;
            _speechCancellation?.Cancel();
            _translationCancellation?.Cancel();
            await Task.WhenAll(_speechTask, _translationTask);
            if (_editor is not null && !await _editor.ConfirmCloseAsync()) return false;
            _closeApproved = true;
            Close();
            await _cleanup;
            return _closed;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { ReportEditorFailure(error); return false; }
        finally { _checkingClose = false; }
    }
}
