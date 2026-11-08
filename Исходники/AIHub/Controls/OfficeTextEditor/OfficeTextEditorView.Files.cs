using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private bool _saving;
    private static bool Project(string? path) => string.Equals(Path.GetExtension(path), OfficePackage.Extension, StringComparison.OrdinalIgnoreCase);
    private bool HasProject => Project(TargetPath ?? SourcePath);
    private void NewDocument() => OpenRequested?.Invoke(null, null);

    private void SelectFile()
    {
        var picker = new OpenFileDialog { Multiselect = true,
            Filter = T("Files") + "|*.LOPATAdoxAI;*.docx;*.doc;*.odt;*.rtf;*.txt;*.md;*.markdown;*.html;*.htm;*.xlsx;*.xlsm;*.xls;*.ods;*.csv;*.pptx;*.ppt;*.odp;*.epub;*.fb2;*.pdf|" + SheetText("AllFiles") + "|*.*" };
        if (picker.ShowDialog(_owner) == true)
            foreach (var name in picker.FileNames) OpenRequested?.Invoke(name,null);
    }

    private void ShowDrafts()
    {
        var drafts = EditorDraft.Recoverable();
        if (drafts.Count == 0) { MessageBox.Show(_owner, T("NoDrafts"), T("Drafts")); return; }
        var dialog = Dialog(T("Drafts"), out var panel); dialog.Width = 640;
        foreach (var draft in drafts.Take(50))
        {
            var label = (Path.GetFileName(draft.Target ?? draft.Source) ?? T("NewDocument")) + " · " + draft.UpdatedUtc.ToLocalTime().ToString("g");
            panel.Children.Add(Action(label,"Draft." + draft.Id, () =>
            {
                dialog.Close();
                OpenRequested?.Invoke(draft.Source, draft.Id);
            }));
        }
        dialog.ShowDialog();
    }

    private string? ProjectDestination()
    {
        var name = (HasProject && ProtectedTarget ? DefaultOutputName()
            : Path.GetFileNameWithoutExtension(TargetPath ?? SourcePath) ?? T("NewDocument")) + ".LOPATAdoxAI";
        var directory = Path.GetDirectoryName(TargetPath ?? SourcePath);
        var picker = new SaveFileDialog { Title = T("SaveProject"), FileName = Directory.Exists(directory) ? Path.Combine(directory,name) : name,
            Filter = T("Project") + "|*.LOPATAdoxAI", DefaultExt = ".LOPATAdoxAI", AddExtension = true, OverwritePrompt = true };
        return picker.ShowDialog(_owner) == true ? picker.FileName : null;
    }
    private string? PickDocumentDestination(bool selectProject)
    {
        var picker = new SaveFileDialog { FileName = DefaultOutputName(),
            Filter = (selectProject ? T("Project") + "|*.LOPATAdoxAI|" : "")
                + "Word|*.docx|OpenDocument|*.odt|RTF|*.rtf|Markdown|*.md|" + T("Text") + "|*.txt|PDF|*.pdf",
            AddExtension = true, OverwritePrompt = true, DefaultExt = selectProject ? ".LOPATAdoxAI" : ".docx" };
        return picker.ShowDialog(_owner) == true ? picker.FileName : null;
    }
    private bool ProtectedTarget => Work.SourceNeedsCopy && SourcePath != null &&
        string.Equals(TargetPath ?? SourcePath, SourcePath, StringComparison.OrdinalIgnoreCase);
    private string DefaultOutputName()
    {
        var name = Path.GetFileNameWithoutExtension(TargetPath ?? SourcePath) ?? T("NewDocument");
        return ProtectedTarget ? name + " · " + T("CopySuffix") : name;
    }

    private async Task SaveFromUiAsync(bool saveAs)
    {
        if (_saving || !_ready || _closed || _closing) return;
        _saving = true;
        var enabled = _sheetSurface.IsEnabled;
        _sheetSurface.IsEnabled = false;
        try { await SaveDocumentAsync(saveAs, false); }
        finally { _saving = false; _sheetSurface.IsEnabled = enabled; }
    }

    private async Task<bool> SaveDocumentAsync(bool saveAs, bool requireProject)
    {
        var path = TargetPath ?? SourcePath;
        if (requireProject) path = HasProject && !ProtectedTarget ? path : ProjectDestination();
        else if (saveAs && !HasProject && _nativeDocument != null && CurrentSheetId == Work.Sheets[0].Id)
        {
            var extension = Work.NativeSectionExtension(CurrentSheetId)!;
            var picker = new SaveFileDialog { Title = T("Save"), FileName = DefaultOutputName(), DefaultExt = extension,
                Filter = NativeFileFilter(extension) + "|" + T("Project") + "|*.LOPATAdoxAI", AddExtension = true, OverwritePrompt = true };
            path = picker.ShowDialog(_owner) == true ? picker.FileName : null;
        }
        else if (saveAs) path = IsTextSheet && CurrentSheetId == Work.Sheets[0].Id ? PickDocumentDestination(true) : ProjectDestination();
        else if (path is null) path = ProjectDestination();
        else if (!Project(path) && Work.Sheets[0].Kind != OfficeSheetKind.Text && !Work.IsWorkbookSheet(Work.Sheets[0].Id)
            && (Work.NativeSectionExtension(Work.Sheets[0].Id) == null || Work.SourceNeedsCopy)) path = ProjectDestination();
        else if (Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase)) path = PickDocumentDestination(true);
        else if (ProtectedTarget)
        {
            MessageBox.Show(_owner, T("SourceNeedsCopy"), T("Save"));
            path = PickDocumentDestination(true);
        }
        if (path is null) return false;
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || !Project(path) && (HasProject || saveAs && !SameFormat(path, TargetPath ?? SourcePath)))
        {
            await ExportPathAsync(path);
            _status.Text = T("Saved");
            return false; // An export never resolves an unsaved project.
        }
        await WithStorageAsync(CheckpointAsync);
        var partial = !Project(path) && Work.NeedsProject;
        if (partial && !ConfirmPartial()) return false;
        await WithStorageAsync(async () =>
        {
            await Work.SaveAsync(path, _lifetime.Token);
        });
        _status.Text = T(partial ? "PartialSaved" : "Saved");
        RefreshProjectNotice();
        TitleChanged?.Invoke();
        return !partial;
    }
    private static bool SameFormat(string path, string? baseline)
    {
        static string? Format(string? file) => Path.GetExtension(file)?.ToLowerInvariant() switch
        {
            ".markdown" => ".md", ".htm" => ".html", var actual => actual
        };
        return Format(path) == Format(baseline);
    }
    private bool ConfirmPartial()
    {
        var dialog = Dialog(T("Save"), out var panel);
        panel.Children.Add(new TextBlock { Text = T("PartialSaveQuestion") + Environment.NewLine + ProjectReasons(),
            TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Action(T("Save"), "Partial.Save", () => dialog.DialogResult = true));
        panel.Children.Add(Action(T("Cancel"), "Partial.Cancel", () => dialog.DialogResult = false));
        return dialog.ShowDialog() == true;
    }
    private string ProjectReasons() => string.Join(Environment.NewLine, Work.Reasons
        .Concat(Work.AiEntries.Count == 0 ? [] : new[] { SheetText("AiLog") })
        .Concat(Work.Sheets.Skip(1).Where(s => s.Kind is not (OfficeSheetKind.ActivityLog or OfficeSheetKind.AiLog))
            .Select(s => T("MaterialReason") + " · " + SheetName(s)))
        .Concat(Work.Materials.Select(m => T("MaterialReason") + " · " + m.Name)).Distinct(StringComparer.Ordinal));

    private async Task ExportFromUiAsync()
    {
        if (!_ready || _closed || _closing) return;
        if (!IsTextSheet || CurrentSheetId != Work.Sheets[0].Id)
        {
            await ExportActiveSheetFromUiAsync();
            return;
        }
        var path = PickDocumentDestination(false);
        if (path is null) return;
        if (Work.SourceNeedsCopy && string.Equals(Path.GetFullPath(path), SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_owner,T("SourceNeedsCopy"), T("Export")); return;
        }
        await ExportPathAsync(path);
        _status.Text = T("Saved");
    }
    private Task ExportPathAsync(string path) => WithStorageAsync(async () =>
    {
        await CheckpointAsync();
        await Work.ExportAsync(path, _lifetime.Token);
    });

    internal async Task<string> CreateReaderSnapshotAsync()
    {
        StopCapture();
        await _captureTask;
        if (IsTextSheet && CurrentSheetId == Work.Sheets[0].Id)
        {
            var path = PrivateSheetPath(".docx");
            await ExportPathAsync(path);
            return path;
        }
        if (!CanReadActiveSheet) throw new InvalidOperationException("Reader.Unsupported");
        return await ActiveSheetPreviewAsync();
    }

    internal async Task<bool> ConfirmCloseAsync()
    {
        if (_closed) return true;
        if (_selectionAction) return false;
        if (_summaryView != null) await _summaryView.StopAsync();
        if (_reviewOpening || _review?.Finishing == true) return false;
        if (_review is {} review)
        {
            if (!ConfirmDiscardReview()) return false;
            await CloseReviewAsync(review);
        }
        if (_closing || _saving) return false;
        await FinishDictationAsync();
        _closing = true;
        var previouslyEnabled = IsEnabled;
        IsEnabled = false;
        try
        {
            await _captureTask;
            if (_speaking) { _speechCancellation?.Cancel(); await _speechTask; }
            if (!_ready) { _discardOnDispose = _work is { IsDirty: false }; return true; }
            await WithStorageAsync(CheckpointAsync);
            if (Work.IsDirty)
            {
                var project = Work.NeedsProject && !HasProject || SourcePath is null && TargetPath is null;
                var dialog = Dialog(T(project ? "CloseProjectQuestion" : "CloseDocumentQuestion"), out var panel);
                var reasons = ProjectReasons();
                panel.Children.Add(new TextBlock { Text = T(project ? "CloseProjectQuestion" : "CloseDocumentQuestion")
                    + (reasons.Length == 0 ? "" : Environment.NewLine + reasons), TextWrapping = TextWrapping.Wrap });
                var discard = false;
                panel.Children.Add(Action(T(project ? "SaveProject" : "Save"),"Close.Save", () => dialog.DialogResult = true));
                panel.Children.Add(Action(T("CloseWithoutSaving"),"Close.Discard", () => { discard = true; dialog.DialogResult = true; }));
                panel.Children.Add(Action(T("Cancel"),"Close.Cancel", () => dialog.DialogResult = false));
                if (dialog.ShowDialog() != true) return false;
                if (!discard && (!await SaveDocumentAsync(false, project) || Work.IsDirty)) return false;
            }
            _discardOnDispose = true;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Failure(error); return false; }
        finally
        {
            _closing = false;
            IsEnabled = previouslyEnabled;
        }
    }

    private async Task PrintDocumentAsync()
    {
        if (!_ready || _closed || _closing) return;
        var path = Path.Combine(Path.GetDirectoryName(Work.TemporaryProjectPath)!, "print-"+Guid.NewGuid().ToString("N")+".pdf");
        await ExportPathAsync(path);
        var dialog = Dialog(T("Print"), out _); dialog.SizeToContent = SizeToContent.Manual; dialog.Width = 860; dialog.Height = 660;
        using var viewer = new WebView2();
        dialog.Content = viewer;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                await viewer.EnsureCoreWebView2Async(_browser.CoreWebView2.Environment);
                if (!dialog.IsVisible) return;
                var file = new Uri(path).AbsoluteUri;
                viewer.CoreWebView2.NavigationStarting += (_, args) => args.Cancel = args.Uri != file;
                viewer.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
                viewer.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess && dialog.IsVisible) viewer.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                };
                viewer.Source = new Uri(file);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { if (dialog.IsVisible) { Failure(error); dialog.Close(); } }
        };
        try { dialog.ShowDialog(); }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}
