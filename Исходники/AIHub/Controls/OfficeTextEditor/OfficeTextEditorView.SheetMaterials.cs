using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private List<OfficeSheetSection>? _sections;
    private OfficeWorkbookView? _workbook;
    private OfficeNativeDocument? _nativeDocument;
    private OfficeNativeEditorView? _nativeView;
    internal string? NativeChapterTitle => _currentSheet?.Kind == OfficeSheetKind.Book &&
        _nativeView is { SelectedPage: >= 0 } view ? _nativeDocument?.Pages[view.SelectedPage].Title : null;

    internal void FollowNativeReadingPosition(int page, IReadOnlyList<AIHub.Services.OfficeReader.ReaderHeading> headings)
    {
        if (_draftSlideList is { Items.Count: > 0 } list) { list.SelectedIndex = Math.Clamp(page, 0, list.Items.Count - 1); return; }
        if (_nativeView == null || _nativeDocument == null) return;
        if (_currentSheet?.Kind == OfficeSheetKind.Presentation) { _nativeView.GoToPage(page); return; }
        foreach (var heading in headings.Where(h => h.Page <= page).OrderByDescending(h => h.Page))
        {
            var matches = _nativeDocument.Pages.Select((p, i) => (p.Title, i)).Where(p =>
                string.Equals(p.Title.Trim(), heading.Title.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count == 1) { _nativeView.GoToPage(matches[0].i); return; }
        }
    }
    private long _sheetEdits, _savedSheetEdits;
    private static string PrivateSheetPath(string extension) =>
        Path.Combine(Path.GetTempPath(),"LOPATA-office-" + Guid.NewGuid().ToString("N") + extension);

    private async Task ShowMaterialSheetAsync(OfficeSheet sheet, CancellationToken token)
    {
        CurrentPage = 0;
        if (Work.IsWorkbookSheet(sheet.Id))
        {
            var workbook = new OfficeWorkbookView(_l);
            await workbook.LoadWorkbookAsync(await Work.ReadWorkbookAsync(sheet.Id,token));
            workbook.Changed += SheetEdited;
            _workbook = workbook;
            _sheetPanel.Content = workbook;
        }
        else if (sheet.Kind is OfficeSheetKind.Book or OfficeSheetKind.Presentation && sheet.Files.Length == 0)
        {
            var bytes = await Work.ReadSheetAsync(sheet.Id,token);
            _sections = OfficeSectionContent.Read(bytes);
            BuildSectionEditor(sheet.Kind == OfficeSheetKind.Book);
        }
        else if (Work.NativeSectionExtension(sheet.Id) is { } extension)
        {
            var bytes = await Work.ReadNativeSectionAsync(sheet.Id, token);
            OfficeNativeDocument document;
            try { document = await Task.Run(() => OfficeNativeDocument.Open(bytes, extension), token); }
            catch (InvalidDataException) { ShowFiles(sheet, T("NativeInvalid")); return; }
            if (!document.CanEdit)
            {
                ShowFiles(sheet, document.ReadOnlyReason is { } reason ? _l(reason) : T("NativeNoText"));
                return;
            }
            if (!await Work.CanEditNativeSheetAsync(sheet.Id, token)) { ShowFiles(sheet, T("NativeInvalid")); return; }
            MountNativeDocument(sheet, document);
        }
        else ShowFiles(sheet);
    }

    private void MountNativeDocument(OfficeSheet sheet, OfficeNativeDocument document)
    {
        _nativeDocument = document;
        var editor = _nativeView = new OfficeNativeEditorView(document, sheet.Kind == OfficeSheetKind.Book, _l);
        editor.Changed += SheetEdited;
        editor.AddRequested += () => _ = GuardAsync(() => AppendNativePageAsync(sheet, document));
        if (sheet.Kind == OfficeSheetKind.Presentation) editor.PageChanged += index => CurrentPage = index;
        _sheetPanel.Content = editor;
    }

    private async Task AppendNativePageAsync(OfficeSheet sheet, OfficeNativeDocument document)
    {
        if (_switching || _saving || _closing || _closed || _nativeDocument != document) return;
        _switching = true;
        var enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            await WithStorageAsync(async () =>
            {
                var index = document.Pages.Count + 1;
                var prefix = SheetText(sheet.Kind == OfficeSheetKind.Book ? "Chapter" : "Slide") + " ";
                while (document.Pages.Any(p => p.Title == prefix + index)) index++;
                var title = prefix + index;
                var updated = await document.AppendAsync(title, _lifetime.Token);
                var showPreview = _nativeView?.PreviewActive == true;
                if (_nativeView != null) await _nativeView.DisposeAsync();
                MountNativeDocument(sheet, updated);
                _nativeView!.GoToPage(updated.Pages.Select((p, i) => (p, i)).Where(pair => pair.p.Title == title)
                    .Select(pair => pair.i).DefaultIfEmpty(updated.Pages.Count - 1).First());
                _nativeView.SetPreview(showPreview);
                SheetEdited();
                await SaveSheetContentAsync(_lifetime.Token);
            });
        }
        finally { _switching = false; IsEnabled = enabled; }
    }

    private void BuildSectionEditor(bool book)
    {
        var sections = _sections!;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new());
        var side = new DockPanel { Margin = new Thickness(0,0,12,0) };
        var list = new ListBox();
        list.SetResourceReference(ForegroundProperty,"ReaderText");
        list.SetResourceReference(BackgroundProperty,"ReaderPanel");
        AutomationProperties.SetAutomationId(list,"Office.Editor.Sheet.Sections");
        string Name(int i) => string.IsNullOrWhiteSpace(sections[i].Title)
            ? SheetText(book ? "Chapter" : "Slide") + " " + (i+1) : sections[i].Title;
        for (var i = 0; i < sections.Count; i++) list.Items.Add(new ListBoxItem {Content = Name(i)});
        var add = Action(SheetText(book ? "AddChapter" : "AddSlide"),"Sheet.AddSection",() =>
        {
            sections.Add(new());
            _draftCanvas?.InvalidateContent();
            list.Items.Add(new ListBoxItem {Content = Name(sections.Count - 1)});
            list.SelectedIndex = sections.Count - 1;
            SheetEdited();
        });
        DockPanel.SetDock(add,Dock.Bottom); side.Children.Add(add); side.Children.Add(list);
        grid.Children.Add(side);
        var editor = new DockPanel();
        var title = new TextBox { Margin = new Thickness(0,4,0,6), Padding = new Thickness(10), FontSize = 20 };
        AutomationProperties.SetAutomationId(title,"Office.Editor.Sheet.SectionTitle");
        AutomationProperties.SetName(title,SheetText("Title"));
        var body = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12), FontSize = 16 };
        AutomationProperties.SetAutomationId(body,"Office.Editor.Sheet.SectionBody");
        AutomationProperties.SetName(body,SheetText(book ? "Chapter" : "Slide"));
        DockPanel.SetDock(title,Dock.Top); editor.Children.Add(title); editor.Children.Add(body);
        UIElement material = book ? editor : WithDraftSlideCanvas(sections, list, editor);
        Grid.SetColumn(material,1); grid.Children.Add(material); _sheetPanel.Content = grid;
        var loading = false;
        list.SelectionChanged += (_,_) =>
        {
            var index = list.SelectedIndex;
            if (index < 0) return;
            if (!book) { CurrentPage = index; _draftCanvas?.ShowPage(index); }
            loading = true; title.Text = sections[index].Title; body.Text = sections[index].Body;
            title.Visibility = sections[index].HideTitle ? Visibility.Collapsed : Visibility.Visible;
            body.Visibility = sections[index].HideBody ? Visibility.Collapsed : Visibility.Visible;
            loading = false;
        };
        title.TextChanged += (_,_) =>
        {
            var index = list.SelectedIndex;
            if (loading || index < 0) return;
            sections[index].Title = title.Text;
            ((ListBoxItem)list.Items[index]).Content = Name(index);
            _draftCanvas?.InvalidateContent();
            SheetEdited();
        };
        body.TextChanged += (_,_) =>
        {
            if (loading || list.SelectedIndex < 0) return;
            sections[list.SelectedIndex].Body = body.Text;
            _draftCanvas?.InvalidateContent();
            SheetEdited();
        };
        if (_draftCanvas != null) _draftCanvas.StructureChanged += () =>
        {
            var index = list.SelectedIndex;
            if (index < 0) return;
            ((ListBoxItem)list.Items[index]).Content = Name(index);
            loading = true;
            title.Text = sections[index].Title; body.Text = sections[index].Body;
            loading = false;
            title.Visibility = sections[index].HideTitle ? Visibility.Collapsed : Visibility.Visible;
            body.Visibility = sections[index].HideBody ? Visibility.Collapsed : Visibility.Visible;
        };
        list.SelectedIndex = 0;
    }

    private void SheetEdited()
    {
        _sheetEdits++;
        if (_ready && !_closing && !_autosave.IsEnabled) _autosave.Start();
    }

    private async Task SaveSheetContentAsync(CancellationToken token)
    {
        if (IsWorkbook)
        {
            // Autosave must leave the user's in-progress cell edit and caret alone.
            if (_saving || _closing || _switching) _workbook!.CommitEdit();
            if (!_workbook!.HasChanges) return;
            var workbook = _workbook;
            var revision = _sheetEdits;
            var bytes = await workbook.ExportWorkbookAsync();
            await Work.WriteWorkbookAsync(CurrentSheetId!,bytes,token);
            if (revision == _sheetEdits) workbook.MarkSaved();
            else if (!_closing) _autosave.Start();
        }
        else if (_nativeDocument != null && _sheetEdits != _savedSheetEdits)
        {
            var revision = _sheetEdits;
            var bytes = await _nativeDocument.SaveAsync(token);
            await Work.WriteNativeSectionAsync(CurrentSheetId!, bytes, token);
            _savedSheetEdits = revision;
            if (_sheetEdits != revision && !_closing) _autosave.Start();
        }
        else if (_sections != null && _sheetEdits != _savedSheetEdits)
        {
            var revision = _sheetEdits;
            await Work.WriteSheetAsync(CurrentSheetId!, JsonSerializer.SerializeToUtf8Bytes(new OfficeSectionDraft(1,_sections)),token);
            _savedSheetEdits = revision;
            if (_sheetEdits != revision && !_closing) _autosave.Start();
        }
    }

    private void ShowFiles(OfficeSheet sheet, string? notice = null)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        if (notice != null) panel.Children.Add(new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3,3,3,12) });
        if (sheet.Kind == OfficeSheetKind.Archive)
        {
            var add = Action(T("AddMaterial"),"Sheet.AddFiles",() => _ = GuardAsync(async () =>
            {
                var picker = new OpenFileDialog { Multiselect = true, Title = T("AddMaterial") };
                if (picker.ShowDialog(_owner) != true) return;
                try
                {
                    foreach (var file in picker.FileNames) await Work.AddSheetFileAsync(sheet.Id,file,_lifetime.Token);
                }
                finally
                {
                    if (CurrentSheetId == sheet.Id)
                    {
                        _currentSheet = Work.Sheets.Single(s => s.Id == sheet.Id);
                        ShowFiles(_currentSheet);
                    }
                    RefreshProjectNotice();
                }
            }));
            add.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            panel.Children.Add(add);
            if (sheet.Files.Length == 0) panel.Children.Add(new TextBlock { Text = T("NoMaterials"), Margin = new Thickness(4,12,4,4) });
        }
        foreach (var file in sheet.Files)
        {
            var row = new DockPanel { Margin = new Thickness(3,4,3,4) };
            var extract = Action(SheetText("Extract"),"Sheet.Extract." + file.Entry,
                () => _ = GuardAsync(() => ExtractFileAsync(file)),SheetText("Extract") + " · " + file.Name);
            DockPanel.SetDock(extract,Dock.Right); row.Children.Add(extract);
            row.Children.Add(new TextBlock { Text = file.Name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
        _sheetPanel.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task ExtractFileAsync(OfficeMaterial file)
    {
        var dialog = new SaveFileDialog { FileName = file.Name, Title = SheetText("Extract"), OverwritePrompt = true };
        if (dialog.ShowDialog(_owner) == true) await Work.ExportMaterialAsync(file,dialog.FileName,_lifetime.Token);
    }

    private byte[] SectionDocument() => OfficeTextFormats.PlainDocument(string.Join(Environment.NewLine + Environment.NewLine,
        _sections!.Select((s,i) => (string.IsNullOrWhiteSpace(s.Title) ? SheetText(IsBookDraft ? "Chapter" : "Slide") + " " + (i+1) : s.Title)
             + Environment.NewLine + OfficeSectionContent.BodyText(s))));

    private async Task<string> ActiveSheetPreviewAsync()
    {
        var sheet = _currentSheet!;
        var extension = IsTextSheet ? ".docx" : IsWorkbook ? ".xlsx" : _sections != null
            ? IsBookDraft ? ".epub" : ".pptx" : Path.GetExtension(sheet.Files[0].Name);
        var path = PrivateSheetPath(extension);
        try
        {
            await WithStorageAsync(async () =>
            {
                _workbook?.CommitEdit();
                await CheckpointAsync();
                var token = _lifetime.Token;
                if (IsTextSheet) await File.WriteAllBytesAsync(path, await Work.ReadSheetAsync(sheet.Id,token),token);
                else if (IsWorkbook) await File.WriteAllBytesAsync(path,await Work.ReadWorkbookAsync(sheet.Id,token),token);
                else if (_sections != null)
                    await File.WriteAllBytesAsync(path, OfficeNativeDocument.Create(extension, _sections), token);
                else if (Work.NativeSectionExtension(sheet.Id) != null)
                    await File.WriteAllBytesAsync(path, await Work.ReadNativeSectionAsync(sheet.Id, token), token);
                else await Work.ExportMaterialAsync(sheet.Files[0],path,token);
            });
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    private async Task ExportActiveSheetFromUiAsync()
    {
        var sheet = _currentSheet!;
        if (!IsTextSheet && !IsWorkbook && _sections == null && _nativeDocument == null)
        {
            if (sheet.Files.Length == 1) await ExtractFileAsync(sheet.Files[0]);
            return;
        }
        var ext = _nativeDocument != null ? Work.NativeSectionExtension(sheet.Id)! : IsWorkbook ? ".xlsx" :
            _sections != null ? IsBookDraft ? ".epub" : ".pptx" : ".docx";
        var filter = _sections != null
            ? (IsBookDraft ? "EPUB|*.epub|FictionBook|*.fb2" : "PowerPoint|*.pptx|OpenDocument|*.odp") + "|Word|*.docx"
            : NativeFileFilter(ext);
        var picker = new SaveFileDialog { Title = T("Export"), Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(SheetName(sheet)), DefaultExt = ext, AddExtension = true, OverwritePrompt = true };
        if (picker.ShowDialog(_owner) != true) return;
        await WithStorageAsync(async () =>
        {
            _workbook?.CommitEdit();
            await CheckpointAsync();
            if (IsWorkbook) await Work.ExportWorkbookAsync(sheet.Id,picker.FileName,_lifetime.Token);
            else if (_nativeDocument != null || _sections != null && OfficeNativeDocument.Supports(Path.GetExtension(picker.FileName)))
                await Work.ExportNativeSectionAsync(sheet.Id, picker.FileName, _lifetime.Token);
            else if (IsTextSheet) await Work.ExportSheetDocumentAsync(sheet.Id,picker.FileName,_lifetime.Token);
            else await Work.ExportDocumentBytesAsync(SectionDocument(),picker.FileName,_lifetime.Token,sheet.Id);
        });
    }

    private static string NativeFileFilter(string extension) => extension.TrimStart('.').ToUpperInvariant() + "|*" + extension;
}
