using System.IO;
using System.Windows;
using AIHub.Models;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private Button _translate = null!, _translationToggle = null!;
    private ReaderSpeechSession? _translationSession;
    private ReaderDocument? _translationOriginal, _translatedDocument;
    private ReaderTranslationWindow? _translationWindow;
    private ReaderTranslationResult? _translationResult;
    private CancellationTokenSource? _translationCancellation;
    private Task _translationTask = Task.CompletedTask;
    private bool _translating;
    private string Tr(string key) => _l("Reader.Translation." + key);

    private void TranslationClicked()
    {
        if (_translating) { _translationCancellation?.Cancel(); return; }
        if (ReferenceEquals(_document, _translatedDocument) && _translationResult is not null) { ShowTranslationPanel(); return; }
        _translationTask = TranslateAsync();
    }
    private async Task TranslateAsync(string? editorSelection = null)
    {
        var document = _document;
        if (document is null || _load is null || _loading || _closed || _speaking) return;
        _translating = true; _translate.Content = "■"; _translate.ToolTip = Tr("Stop");
        System.Windows.Automation.AutomationProperties.SetName(_translate, Tr("Stop")); UpdateNavigation();
        ReaderDocument? unbound = null;
        ReaderDocument? selectedCopy = null;
        try
        {
            var library = new ManagedModelLibraryStore();
            var settings = new ReaderAiSettingsStore().Load();
            var model = ReaderAiModelCatalog.TextModels(new StorageSettingsStore().LoadOrCreate(), library.LoadAll()).FirstOrDefault(m => m.Id == settings.TextModel)
                ?? throw new InvalidDataException("Reader.Ai.InvalidChoice");
            var selected = _selection.HasSelection && _selection.Bounds is { } bounds
                ? new ReaderSpeechRange(bounds.Start.Page, bounds.End.Page - bounds.Start.Page + 1, bounds.Start.Offset, bounds.End.Offset) : null;
            var selectionOnlyCopy=false;
            if (!string.IsNullOrWhiteSpace(editorSelection))
            {
                selected = await LocateEditorSelectionAsync(document, editorSelection, _load.Token);
                selectionOnlyCopy = selected == null;
                selected ??= new ReaderSpeechRange(0,1);
            }
            var prompt = new ReaderTranslationPrompt(this, _l, document.Pages.Count, selected, model.Name,selectionCopy:selectionOnlyCopy);
            if (prompt.ShowDialog() != true || prompt.Range is not { } range || _closed || !ReferenceEquals(document, _document)) return;
            if (!ReaderTranslationRuntime.CanAttempt(model.Card)) throw new InvalidDataException("Reader.Translation.UnsupportedModel");
            var session = _translationSession ??= new(ReaderTranslationWork.TemporaryRoot);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token, _load.Token);
            _translationCancellation = cancellation;
            using (ApplicationBackgroundOperations.Current?.BeginAuxiliary())
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                using var prepare = new ReaderTextPreparation(library);
                prepare.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
                var window = new VideoPreparationWindow(this, _l, model.Name,
                    (download, progress, ct) => prepare.PrepareAsync(model.Card, download, progress, ct), cancellation.Token);
                if (window.ShowDialog() != true) { _status.Text = Tr("Cancelled"); return; }
            }
            _status.Text = Tr("Reading");
            var inputDirectory = Path.Combine(session.DirectoryPath, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(inputDirectory);
            var inputDocument=document;
            if(selectionOnlyCopy && ReferenceEquals(range,selected))
            {
                var fragment=Path.Combine(inputDirectory,"selection.docx");
                await File.WriteAllBytesAsync(fragment,Services.OfficeEditor.OfficeTextFormats.PlainDocument(editorSelection!),cancellation.Token);
                selectedCopy=await ReaderPreparation.OpenAsync(fragment,null,cancellation.Token,useCache:false);
                inputDocument=selectedCopy;
                range=new ReaderSpeechRange(0,inputDocument.Pages.Count);
            }
            var layout = await ReaderTranslationLayoutReader.ReadAsync(inputDocument, range, inputDirectory, cancellation.Token);
            var request = new ReaderTranslationRequest(Guid.NewGuid().ToString("N"), layout, model.Card, prompt.Source, prompt.Target, Tr("Task"));
            var progressUpdates = new Progress<(int Done, int Total)>(p => {
                if (!_closed && _translating && !cancellation.IsCancellationRequested && ReferenceEquals(session, _translationSession)) _status.Text = string.Format(Tr("Progress"), p.Done, p.Total);
            });
            string reason = Tr("Title") + " · " + model.Card.DisplayName;
            var journal = CreateReaderAiJournal(reason);
            var result = await new ReaderTranslationWork(session, () => new ReaderTranslationRuntime(journal))
                .GenerateAsync(request, progressUpdates, cancellation.Token);
            if (_closed || !ReferenceEquals(document, _document) || cancellation.IsCancellationRequested) return;
            var workspace = Path.Combine(inputDirectory, "view"); Directory.CreateDirectory(workspace);
            var backend = await ReaderPdfPages.OpenAsync(result.Path, workspace, cancellation.Token);
            try { unbound = new(document.SourcePath, workspace, backend, result.Path); }
            catch { await backend.DisposeAsync(); throw; }
            if (_closed || !ReferenceEquals(document, _document) || cancellation.IsCancellationRequested) return;
            await CloseTranslationPanelAsync();
            if (_translatedDocument is not null) await _translatedDocument.DisposeAsync();
            _translationOriginal = document; _translatedDocument = unbound; unbound = null; _translationResult = result;
            PresentDocument(_translatedDocument, _page);
            _translationToggle.Visibility = Visibility.Visible; ShowTranslationPanel(); _status.Text = Tr("Ready");
            ApplicationBackgroundOperations.Current?.Acknowledge(request.Id);
        }
        catch (OperationCanceledException) { if (!_closed && ReferenceEquals(document, _document)) _status.Text = Tr("Cancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            OwnedProcessRegistry.Log("reader_translation_failed", "Office.Reader.Translation", detail: error.GetType().Name + ": " + error.Message);
            var reason = error is BackgroundOperationFailedException { InnerException: { } inner } ? inner.Message : error.Message;
            if (reason is not ("Reader.Translation.InvalidReply" or "Reader.Translation.ChangedNumbers" or "Reader.Translation.Truncated"
                or "Reader.Translation.ContextLimit" or "Reader.Translation.UnsupportedModel" or "Reader.Translation.NoText"
                or "Reader.Translation.LayoutFailed" or "Reader.Translation.FontMissing" or "Reader.Translation.TooLong"))
                ComponentAuditFailures.Report(error);
            if (!_closed && ReferenceEquals(document, _document)) _status.Text = reason.StartsWith("Reader.", StringComparison.Ordinal)
                ? _l(reason) : reason is "Music.Poetry.OtherOperation" or "Another model operation is active." ? _l("ChatSpeech.Busy") : Tr("Failed");
        }
        finally
        {
            if (selectedCopy != null) await selectedCopy.DisposeAsync();
            if (unbound is not null) await unbound.DisposeAsync();
            _translationCancellation = null; _translating = false;
            if (!_closed) { _translate.Content = "文A"; _translate.ToolTip = Tr("Title"); System.Windows.Automation.AutomationProperties.SetName(_translate, Tr("Title")); UpdateNavigation(); }
        }
    }
    private void ToggleTranslation()
    {
        if (_translationOriginal is null || _translatedDocument is null || _translating || _speaking) return;
        PresentDocument(ReferenceEquals(_document, _translatedDocument) ? _translationOriginal : _translatedDocument, _page);
        _status.Text = Tr(ReferenceEquals(_document, _translatedDocument) ? "TranslatedView" : "OriginalView");
    }

    private static async Task<ReaderSpeechRange?> LocateEditorSelectionAsync(ReaderDocument document, string selected, CancellationToken token)
    {
        var needle = string.Concat(selected.Where(c => !char.IsWhiteSpace(c)));
        if (needle.Length == 0) return null;
        var prefix=new int[needle.Length];
        for(int i=1,length=0;i<needle.Length;i++)
        {
            while(length>0 && needle[i]!=needle[length]) length=prefix[length-1];
            if(needle[i]==needle[length]) length++;
            prefix[i]=length;
        }
        // Keep only enough PDF offsets to identify the beginning of this selection.
        var offsets=new (int Page,int Offset)[needle.Length];
        int ring=0, matched=0;
        ReaderSpeechRange? found=null;
        for (int page=0;page<document.Pages.Count;page++)
        {
            var content = (await document.ReadTextAsync(page,token)).Text;
            for (int i=0;i<content.Length;i++)
            {
                var c=content[i]; if(char.IsWhiteSpace(c)) continue;
                offsets[ring]=(page,i);ring=(ring+1)%offsets.Length;
                while(matched>0 && c!=needle[matched]) matched=prefix[matched-1];
                if(c==needle[matched]) matched++;
                if(matched!=needle.Length) continue;
                if(found!=null) return null;
                var start=offsets[ring];
                found=new(start.Page,page-start.Page+1,start.Offset,i+1);
                matched=prefix[matched-1];
            }
        }
        return found;
    }
    private void ShowTranslationPanel()
    {
        if (_translationResult is null || _translationOriginal is null || _closed) return;
        if (_translationWindow?.IsVisible == true) { _translationWindow.Activate(); return; }
        var panel = new ReaderTranslationWindow(this, _l, _translationResult, _translationOriginal.SourcePath, ToggleTranslation);
        _translationWindow = panel; panel.Show();
    }
    private async Task CloseTranslationPanelAsync()
    {
        if (_translationWindow is not { } window) return;
        _translationWindow = null; if (window.IsVisible) window.Close(); await window.PendingSave;
    }
    private async Task CloseDocumentTranslationAsync(ReaderDocument? keep)
    {
        var session = _translationSession; _translationSession = null;
        _translationCancellation?.Cancel(); session?.Cancel();
        try
        {
            await CloseTranslationPanelAsync(); await _translationTask;
            foreach (var document in new[] { _translationOriginal, _translatedDocument }.Where(d => d is not null).Distinct())
                if (!ReferenceEquals(document, keep)) await document!.DisposeAsync();
        }
        finally
        {
            _translationOriginal = _translatedDocument = null; _translationResult = null;
            _translationToggle.Visibility = Visibility.Collapsed; session?.Dispose();
        }
    }
}
