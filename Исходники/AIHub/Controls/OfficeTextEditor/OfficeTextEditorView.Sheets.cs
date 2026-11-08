using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.Dictation;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private sealed record TextSheetSurface(OfficeTextBrowser Browser, long Revision);
    private readonly Dictionary<string, TextSheetSurface> _textSheets = [];
    private readonly Grid _sheetSurface = new();
    private readonly ContentControl _sheetPanel = new() { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly DockPanel _sheetStrip = new() { LastChildFill = true };
    private readonly StackPanel _sheetTabs = new() { Orientation = Orientation.Horizontal };
    private readonly List<MenuItem> _textMenus = [];
    private MenuItem _printMenu = null!, _readerMenu = null!, _viewReadMenu = null!, _createProjectMenu = null!, _exportMenu = null!;
    private Button _readButton = null!;
    private OfficeSheet? _currentSheet;
    private bool _switching, _returningFromReading;
    internal Func<Task<bool>>? ReturnFromReading { get; set; }
    internal string? CurrentSheetId => _currentSheet?.Id;
    private bool IsTextSheet => _currentSheet?.Kind == OfficeSheetKind.Text;
    private bool IsBookDraft => _currentSheet is { Kind: OfficeSheetKind.Book, Files.Length: 0 };
    private bool IsWorkbook => _workbook != null;
    internal bool IsProjectWorkspace => _work != null && (HasProject || SourcePath == null && TargetPath == null);
    internal bool CanReadActiveSheet => IsTextSheet || _sections != null || IsWorkbook || (_currentSheet is { Kind: not OfficeSheetKind.Archive, Files.Length: > 0 } sheet
        && ReaderPreparation.SupportedExtensions.Contains(Path.GetExtension(sheet.Files[0].Name), StringComparer.OrdinalIgnoreCase));

    private string SheetText(string key) => T("Sheet." + key);
    private string SheetName(OfficeSheet sheet) => string.IsNullOrWhiteSpace(sheet.Name) ? SheetText(sheet.Kind.ToString()) : sheet.Name;
    private void BuildSheetStrip()
    {
        _sheetStrip.Resources.MergedDictionaries.Add(Resources);
        _sheetStrip.SetResourceReference(BackgroundProperty, "ReaderPanel");
        _sheetStrip.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_sheetStrip, "Office.Editor.SheetStrip");
        var list = Action("☰", "SheetList", ShowSheetList, SheetText("All"));
        DockPanel.SetDock(list, Dock.Left);
        _sheetStrip.Children.Add(list);
        var scroller = new ScrollViewer { Content = _sheetTabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        _sheetStrip.Children.Add(scroller);
        Grid.SetRow(_sheetStrip, 3);
        _root.Children.Add(_sheetStrip);
        PreviewKeyDown += (_, e) =>
        {
            if (!_ready || IsTextSheet || _switching || _closing || _saving ||
                (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            switch (e.Key)
            {
                case Key.S: _ = GuardAsync(() => SaveFromUiAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0)); break;
                case Key.N: NewDocument(); break;
                case Key.O: SelectFile(); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    internal FrameworkElement? DetachProjectStripForReader()
    {
        if (!IsProjectWorkspace || _sheetStrip.Visibility != Visibility.Visible) return null;
        if (_sheetStrip.Parent == _root) _root.Children.Remove(_sheetStrip);
        return _sheetStrip;
    }

    internal void RestoreProjectStrip()
    {
        if (_sheetStrip.Parent == _root) return;
        if (_sheetStrip.Parent is System.Windows.Controls.Panel reader) reader.Children.Remove(_sheetStrip);
        Grid.SetRow(_sheetStrip,3);
        _root.Children.Add(_sheetStrip);
    }

    private async Task FromSheetStripAsync(Func<Task> command)
    {
        if (_returningFromReading || _closed) return;
        if (_sheetStrip.Parent != _root)
        {
            if (ReturnFromReading == null) return;
            _returningFromReading = true;
            try { if (!await ReturnFromReading()) return; }
            finally { _returningFromReading = false; }
            if (_sheetStrip.Parent != _root || _closed) return;
        }
        await command();
    }

    private void ShowSheetList()
    {
        var list = new ContextMenu { PlacementTarget = _sheetStrip };
        foreach (var sheet in Work.Sheets)
        {
            var item = new MenuItem { Header = SheetName(sheet), IsChecked = sheet.Id == CurrentSheetId };
            item.Click += (_, _) => _ = GuardAsync(() => FromSheetStripAsync(() => SwitchSheetAsync(sheet.Id)));
            list.Items.Add(item);
        }
        list.IsOpen = true;
    }

    private void RefreshSheetStrip()
    {
        if (_work == null) return;
        _sheetStrip.Visibility = IsProjectWorkspace ? Visibility.Visible : Visibility.Collapsed;
        _createProjectMenu.Visibility = IsProjectWorkspace ? Visibility.Collapsed : Visibility.Visible;
        if (IsProjectWorkspace)
        {
            var focusedId = _sheetTabs.Children.OfType<Button>().FirstOrDefault(b => b.IsKeyboardFocused)?.Tag as string;
            _sheetTabs.Children.Clear();
            foreach (var sheet in Work.Sheets)
            {
                var selected = sheet.Id == CurrentSheetId;
                var tab = Action(SheetName(sheet), "Sheet." + sheet.Id, () => _ = GuardAsync(() => FromSheetStripAsync(() => SwitchSheetAsync(sheet.Id))));
                tab.Tag = sheet.Id;
                tab.Padding = new Thickness(13, 5, 13, 5);
                tab.Margin = new Thickness(1, 1, 1, 2);
                if (selected)
                {
                    tab.SetResourceReference(BackgroundProperty, "ReaderHover");
                    tab.BorderBrush = new SolidColorBrush(_dark ? System.Windows.Media.Color.FromRgb(111,163,240) : System.Windows.Media.Color.FromRgb(40,103,190));
                    tab.FontWeight = FontWeights.SemiBold;
                }
                _sheetTabs.Children.Add(tab);
                if (focusedId == sheet.Id) tab.Focus();
                if (selected) tab.Loaded += (_, _) => tab.BringIntoView();
            }
            var add = Action("＋", "AddSheet", () => _ = GuardAsync(() => FromSheetStripAsync(AddSheetFromUiAsync)), SheetText("Add"));
            _sheetTabs.Children.Add(add);
        }
        if (_currentSheet?.Kind is OfficeSheetKind.ActivityLog or OfficeSheetKind.AiLog)
            ShowJournal(_currentSheet.Kind);
        if (_currentSheet != null) SetSheetCommands();
    }

    private void SetSheetCommands()
    {
        foreach (var item in _textMenus) item.Visibility = IsTextSheet ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement child in _toolbar.Children)
        {
            var id = AutomationProperties.GetAutomationId(child);
            var visible = IsTextSheet || id is "Office.Editor.New" or "Office.Editor.Save" or "Office.Editor.Read" or "Office.Editor.Symbols"
                || id is "Office.Editor.Translation" or "Office.Editor.Summary" && CanReadActiveSheet;
            child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        _readButton.Visibility = CanReadActiveSheet ? Visibility.Visible : Visibility.Collapsed;
        _readerMenu.IsEnabled = _viewReadMenu.IsEnabled = CanReadActiveSheet;
        _printMenu.IsEnabled = IsTextSheet;
        _exportMenu.IsEnabled = IsTextSheet || IsWorkbook || _sections != null || _currentSheet?.Files.Length == 1;
        _page.Visibility = _statistics.Visibility = IsTextSheet ? Visibility.Visible : Visibility.Collapsed;
    }

    internal OfficeSheetKind? ChooseFirstSheetKind() => ChooseSheet(true)?.Kind;

    private (OfficeSheetKind Kind, string Name, string? SourceFile)? ChooseSheet(bool first)
    {
        var dialog = Dialog(SheetText(first ? "ChooseFirst" : "Add"), out var panel);
        AutomationProperties.SetAutomationId(dialog, "Office.Editor.SheetPicker");
        TextBox? name = first ? null : Field(panel, "Sheet.OptionalName");
        (OfficeSheetKind Kind,string Name,string? SourceFile)? choice = null;
        foreach (var kind in new[] {OfficeSheetKind.Text, OfficeSheetKind.Book, OfficeSheetKind.Table,
                                     OfficeSheetKind.Presentation, OfficeSheetKind.Archive})
        {
            var button = Action(SheetText(kind.ToString()), "SheetType." + kind, () =>
            {
                choice = (kind, name?.Text.Trim() ?? "",null);
                dialog.DialogResult = true;
            });
            panel.Children.Add(button);
        }
        if (!first) panel.Children.Add(Action(SheetText("FromFile"),"SheetType.FromFile",() =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = SheetText("FromFile") };
            if (picker.ShowDialog(dialog) != true) return;
            choice = (OfficeTextWork.ClassifySource(picker.FileName),"",picker.FileName);
            dialog.DialogResult = true;
        }));
        var cancel = Action(T("Cancel"),"SheetType.Cancel",() => dialog.DialogResult = false);
        cancel.IsCancel = true;
        panel.Children.Add(cancel);
        return dialog.ShowDialog() == true ? choice : null;
    }

    private async Task AddSheetFromUiAsync()
    {
        if (!_ready || _switching || _saving || _closing || !IsProjectWorkspace || ChooseSheet(false) is not { } choice) return;
        var count = Work.Sheets.Count(s => s.Kind == choice.Kind);
        var name = choice.Name;
        if (name.Length == 0 && count > 0) name = SheetText(choice.Kind.ToString()) + " " + (count + 1);
        await WithStorageAsync(CheckpointAsync);
        var sheet = choice.SourceFile != null ? await Work.AddSheetFromFileAsync(choice.SourceFile,_lifetime.Token)
            : await Work.AddSheetAsync(choice.Kind, name, _lifetime.Token);
        RefreshProjectNotice();
        await SwitchSheetAsync(sheet.Id);
    }

    private async Task SwitchSheetAsync(string id)
    {
        if (!_ready || _switching || _saving || _closing || CurrentSheetId == id || _speaking) return;
        _switching = true;
        var wasEnabled = IsEnabled;
        try
        {
            await FinishDictationAsync();
            IsEnabled = false;
            StopCapture();
            await _captureTask;
            await WithStorageAsync(async () =>
            {
                await CheckpointAsync();
                var previous = _currentSheet!;
                try { await DisplaySheetAsync(Work.Sheets.Single(s => s.Id == id),_lifetime.Token); }
                catch
                {
                    await DisplaySheetAsync(Work.Sheets.Single(s => s.Id == previous.Id),_lifetime.Token);
                    throw;
                }
            });
        }
        finally { _switching = false; IsEnabled = wasEnabled; RefreshProjectNotice(); }
    }

    private async Task DisplaySheetAsync(OfficeSheet sheet, CancellationToken token)
    {
        if (IsTextSheet && _textSheets.ContainsKey(_currentSheet!.Id))
            _textSheets[_currentSheet.Id] = new(_browser, _checkpoint);
        _currentSheet = sheet;
        _browser.Visibility = Visibility.Collapsed;
        _sheetPanel.Content = null;
        _sheetPanel.Visibility = IsTextSheet ? Visibility.Collapsed : Visibility.Visible;
        _workbook?.Dispose();
        _workbook = null;
        if (_nativeView != null) await _nativeView.DisposeAsync();
        _nativeDocument = null;
        _nativeView = null;
        await CloseDraftCanvasAsync();
        _sections = null;
        _sheetEdits = 0; _savedSheetEdits = 0;
        if (IsTextSheet)
            await ShowTextSheetAsync(sheet,token);
        else if (sheet.Kind is OfficeSheetKind.ActivityLog or OfficeSheetKind.AiLog)
            ShowJournal(sheet.Kind);
        else
            await ShowMaterialSheetAsync(sheet, token);
        SetSheetCommands();
    }

    private async Task ShowTextSheetAsync(OfficeSheet sheet, CancellationToken token)
    {
        if (_textSheets.TryGetValue(sheet.Id, out var cached))
        {
            _browser = cached.Browser;
            _revision = _checkpoint = cached.Revision;
            _browser.Visibility = Visibility.Visible;
            await _browser.RequestAsync(new { action = "theme", dark = _dark }, token);
        }
        else
        {
            if (_textSheets.Count != 0 || _browser.CoreWebView2 != null)
            {
                _browser = new();
                _sheetSurface.Children.Insert(0, _browser);
                RegisteredDictationTarget.Register(_browser, this);
            }
            var browser = _browser;
            browser.Message += message => { if (ReferenceEquals(_browser,browser)) SurfaceMessage(message); };
            browser.SurfaceFailed += Failure;
            browser.Visibility = Visibility.Visible;
            await browser.InitializeSurfaceAsync(token);
            var content = await Work.ReadSheetAsync(sheet.Id,token);
            var primary = sheet.Id == Work.Sheets[0].Id;
            await browser.RequestAsync(new { action = "open", base64 = Convert.ToBase64String(content),
                dark = _dark, readOnly = false, lang = IsRussian ? "ru" : "en",
                compatibilityBaseline = primary ? Work.CompatibilityBaseline : null,
                sourceFormat = primary ? Path.GetExtension(TargetPath ?? SourcePath)?.TrimStart('.').ToLowerInvariant() : null }, token);
            _revision = _checkpoint = 0;
            _textSheets[sheet.Id] = new(browser, 0);
        }
        _checkedRevision = -1;
        SetState(await _browser.RequestAsync(new { action = "state" }, token));
        _checkpoint = _revision;
        _browser.IsEnabled = true;
    }

    private async Task CreateProjectAsync()
    {
        if (!_ready || _saving || _closing) return;
        _saving = true;
        try { await SaveDocumentAsync(false,true); }
        finally { _saving = false; RefreshProjectNotice(); }
    }
}
