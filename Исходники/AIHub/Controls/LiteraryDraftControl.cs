using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using DataObject = System.Windows.DataObject;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed class LiteraryDraftControl : UserControl
{
    private readonly TextBox _editor = LiteraryWorkspaceParts.TextArea(false);
    private readonly TextBlock _counts = new(), _status = new(), _hint = new();
    private readonly DispatcherTimer _timer = new();
    private readonly SemaphoreSlim _insertQueue = new(1, 1);
    private Func<string, string> _l;
    private string _accepted = "", _saved = "", _statusKey = "Literary.Draft.Ready";
    private DateTime? _lastSaved;
    private bool _restoring, _loadFailed, _busy, _discarded;
    public LiteraryChapterStore Store { get; }
    public string Text => _editor.Text;
    public string ChapterLabel => _loadFailed ? _l("Literary.Workspace.Chapter") : Path.GetFileNameWithoutExtension(Store.Active.FileName);
    public event Action? ChapterChanged;
    public Func<bool>? CanFixFiles { get; set; }
    private bool _externalBlocked;
    public void BlockActions(bool blocked) { _externalBlocked = blocked; _editor.IsReadOnly = blocked || _loadFailed || _busy; }

    public LiteraryEditorSnapshot Capture(string projectId, string directory)
    {
        Dispatcher.VerifyAccess();
        if (_loadFailed || (_busy && _editor.IsReadOnly))
            throw new LiterarySourceException("The editor is not ready for a consistent snapshot.", new IOException());
        return LiteraryEditorSnapshot.Capture(projectId, directory, Store.Index, Text, Text != _saved);
    }

    public LiteraryDraftControl(string projectDirectory, Func<string, string> localize)
    {
        Store = new LiteraryChapterStore(projectDirectory); _l = localize;
        try { Store.Open(); LoadCurrent(); }
        catch (Exception ex) when (IsStorageError(ex)) { _loadFailed = true; _statusKey = "Literary.Draft.LoadError"; }
        _editor.IsReadOnly = _loadFailed;
        _editor.AllowDrop = true;
        _editor.TextChanged += (_, _) => Changed();
        _editor.PreviewTextInput += (_, e) => { if (WouldOverflow(e.Text)) { e.Handled = true; Blink(); } };
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Tab && WouldOverflow(e.Key == Key.Enter ? "\r\n" : "\t")) { e.Handled = true; Blink(); }
        };
        DataObject.AddPastingHandler(_editor, (_, e) =>
        {
            var text = e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) as string ?? e.DataObject.GetData(System.Windows.DataFormats.Text) as string;
            e.CancelCommand(); if (text is not null) _ = InsertTextAsync(text);
        });
        _editor.PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; };
        _editor.PreviewDrop += (_, e) =>
        {
            e.Handled = true; e.Effects = System.Windows.DragDropEffects.Copy;
            if (e.Data.GetData(System.Windows.DataFormats.UnicodeText) is string text) _ = InsertTextAsync(text);
        };
        _timer.Tick += async (_, _) => await SaveAsync(false);
        Loaded += (_, _) => { SetTimer(); if (_loadFailed) OfferRecovery(); };
        Unloaded += (_, _) => _timer.Stop();
        var panel = new DockPanel();
        _status.TextWrapping = _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 6);
        foreach (var block in new[] { _status, _counts, _hint })
        { block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); block.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize"); }
        _status.ToolTip = _l("Literary.Editor.IntervalHint");
        _status.MouseRightButtonUp += async (_, e) =>
        {
            e.Handled = true; if (_externalBlocked || _busy || _loadFailed) return;
            var menu = new ContextMenu(); var item = new MenuItem { Header = _l("Literary.Editor.Interval") };
            item.Click += async (_, _) =>
            {
                if (LiteraryEditorDialogs.Interval(this, _l, Store.Index.AutosaveSeconds) is { } seconds)
                { await RunAsync(() => Store.SetAutosave(seconds)); SetTimer(); }
            };
            menu.Items.Add(item); _status.ContextMenu = menu; menu.IsOpen = true;
            await Task.CompletedTask;
        };
        DockPanel.SetDock(_hint, Dock.Top); panel.Children.Add(_hint);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        DockPanel.SetDock(_counts, Dock.Bottom); panel.Children.Add(_counts);
        panel.Children.Add(_editor); Content = panel; ApplyLocalization(localize);
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Text.DecoderFallbackException;
    private bool WouldOverflow(string text) => Text.Length - _editor.SelectionLength + text.Length > LiteraryModelPolicy.DraftCharacters;
    private void SetTimer() { _timer.Interval = TimeSpan.FromSeconds(Store.Index.AutosaveSeconds); if (IsLoaded && !_loadFailed) _timer.Start(); }
    private void LoadCurrent()
    {
        var text = Store.Load(); _restoring = true;
        _editor.Text = _accepted = _saved = text; _editor.CaretIndex = text.Length;
        _restoring = false; _lastSaved = Store.LastSaved; _statusKey = "Literary.Draft.Saved";
        _discarded = false; _editor.IsUndoEnabled = false; _editor.IsUndoEnabled = true;
    }
    public void ApplyLocalization(Func<string, string> localize)
    { _l = localize; _hint.Text = _l("Literary.Draft.Hint"); _status.ToolTip = _l("Literary.Editor.IntervalHint"); Refresh(); }
    private void Changed()
    {
        if (_restoring) return;
        if (Text.Length > LiteraryModelPolicy.DraftCharacters && Text.Length > _accepted.Length)
        {
            _restoring = true;
            if (_editor.CanUndo) _editor.Undo();
            else _editor.Text = _accepted;
            _restoring = false; Blink(); return;
        }
        _accepted = Text; _discarded = false; _statusKey = "Literary.Draft.Unsaved"; Refresh();
    }
    private void Blink()
    {
        _counts.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(140)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
        _statusKey = "Literary.Draft.Full"; Refresh();
    }
    public bool Save()
    {
        if (_busy) return false;
        if (_loadFailed) return false;
        if (_discarded || _accepted == _saved) return true;
        try { Store.Save(_accepted); _saved = _accepted; _lastSaved = Store.LastSaved; _statusKey = "Literary.Draft.Saved"; Refresh(); return true; }
        catch (Exception ex) when (IsStorageError(ex)) { _statusKey = "Literary.Draft.SaveError"; Refresh(); return false; }
    }
    public async Task<bool> SaveAsync(bool lockEditor = true)
    {
        if (_busy || _loadFailed) return false;
        if (_discarded || _accepted == _saved) return true;
        var snapshot = _accepted;
        if (!await RunAsync(() => Store.Save(snapshot), lockEditor)) return false;
        _saved = snapshot; _lastSaved = Store.LastSaved;
        _statusKey = _accepted == _saved ? "Literary.Draft.Saved" : "Literary.Draft.Unsaved"; Refresh(); return true;
    }

    public bool CanLeave()
    {
        if (_loadFailed) return true;
        if (_busy) { ShowMessage("Literary.Editor.Busy"); return false; }
        _timer.Stop();
        try { return DecideLeave(); }
        finally { SetTimer(); }
    }
    private bool DecideLeave()
    {
        while (!Save())
        {
            var choice = LiteraryEditorDialogs.Choose(this, _l, "Literary.Draft.SaveError", _l("Literary.Editor.LeaveError"),
                "Literary.Editor.Retry", "Literary.Editor.Stay", "Literary.Editor.Discard");
            if (choice == 0) continue;
            if (choice != 2) return false;
            _restoring = true; _editor.Text = _accepted = _saved; _restoring = false;
            _discarded = true; _statusKey = "Literary.Draft.Saved"; Refresh(); return true;
        }
        return true;
    }

    public async Task InsertTextAsync(string incoming)
    {
        // Native Paste runs inside a text change block with dispatcher processing disabled.
        // Complete that command before modifying the selection or opening a modal dialog.
        await Dispatcher.Yield(DispatcherPriority.Normal);
        await _insertQueue.WaitAsync();
        try { await InsertTextCoreAsync(incoming); }
        finally { _insertQueue.Release(); }
    }

    private async Task InsertTextCoreAsync(string incoming)
    {
        if (_externalBlocked || _busy || _loadFailed) { ShowMessage("Literary.Editor.Busy"); return; }
        // WPF uses CRLF. Count and persist the same representation as the editor.
        incoming = incoming.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        while (WouldOverflow(incoming))
        {
            if (CanFixFiles?.Invoke() == false) { ShowMessage("Literary.Shared.Waiting"); return; }
            var choice = LiteraryEditorDialogs.Choose(this, _l, "Literary.Editor.Overflow", _l("Literary.Editor.OverflowHint"),
                "Literary.Editor.EditPaste", "Literary.Editor.Continue", "Literary.Editor.Cancel");
            if (choice == 0)
            {
                var edited = LiteraryEditorDialogs.Edit(this, _l, "Literary.Editor.EditPaste", incoming, true);
                if (edited is null) return; incoming = edited; continue;
            }
            if (choice != 1) return;
            IReadOnlyList<string> parts;
            try { parts = LiteraryChapterFiles.SplitParagraphs(incoming, LiteraryModelPolicy.DraftCharacters); }
            catch (InvalidOperationException) { ShowMessage("Literary.Editor.LongParagraph"); continue; }
            if (parts.Count > 1 && LiteraryEditorDialogs.Choose(this, _l, "Literary.Editor.Overflow", string.Format(_l("Literary.Editor.SplitHint"), parts.Count), "Literary.Editor.Split", "Literary.Editor.Cancel") != 0) continue;
            if (!await SaveAsync()) { ShowMessage("Literary.Draft.SaveError"); continue; }
            if (!await RunAsync(() => Store.Continue(parts))) continue;
            LoadCurrent(); Refresh(); ChapterChanged?.Invoke(); return;
        }
        _editor.SelectedText = incoming; _editor.CaretIndex = _editor.SelectionStart + _editor.SelectionLength;
        _editor.SelectionLength = 0;
    }

    public async Task FinishAsync()
    {
        if (_externalBlocked || CanFixFiles?.Invoke() == false) { ShowMessage("Literary.Shared.Waiting"); return; }
        if (!await SaveAsync()) { ShowMessage("Literary.Draft.SaveError"); return; }
        if (await RunAsync(Store.Finish)) { LoadCurrent(); Refresh(); ChapterChanged?.Invoke(); }
    }
    public async Task RenameAsync()
    {
        if (_externalBlocked || _busy || _loadFailed) return;
        var name = LiteraryEditorDialogs.Edit(this, _l, "Literary.Workspace.Action.RenameChapter", Store.Active.Title, false);
        if (name is null || !await SaveAsync()) return;
        if (await RunAsync(() => Store.Rename(name))) { Refresh(); ChapterChanged?.Invoke(); }
    }
    private async Task<bool> RunAsync(Action action, bool lockEditor = true)
    {
        if (_busy || _loadFailed) return false;
        _busy = true; if (lockEditor) _editor.IsReadOnly = true;
        _statusKey = "Literary.Editor.Saving"; Refresh();
        try { await Task.Run(action); _statusKey = _accepted == _saved ? "Literary.Draft.Saved" : "Literary.Draft.Unsaved"; return true; }
        catch (Exception ex) when (IsStorageError(ex)) { _statusKey = "Literary.Draft.SaveError"; return false; }
        finally { _busy = false; _editor.IsReadOnly = _externalBlocked || _loadFailed; Refresh(); }
    }
    private void OfferRecovery()
    {
        if (!Store.RecoveryAvailable) return;
        if (LiteraryEditorDialogs.Choose(this, _l, "Literary.Draft.LoadError", _l("Literary.Editor.RecoverHint"), "Literary.Editor.Recover", "Literary.Editor.Cancel") != 0) return;
        try { Store.RestoreBackup(); Store.Open(); LoadCurrent(); _loadFailed = false; _editor.IsReadOnly = false; SetTimer(); Refresh(); ChapterChanged?.Invoke(); }
        catch (Exception ex) when (IsStorageError(ex)) { ShowMessage("Literary.Draft.LoadError"); }
    }
    private void ShowMessage(string key) => System.Windows.MessageBox.Show(Window.GetWindow(this), _l(key), _l("Literary.Workspace.Chapter"));
    private void Refresh()
    {
        _counts.Text = string.Format(_l("Literary.Draft.Counts"), Text.Length, LiteraryModelPolicy.DraftCharacters);
        var saved = _lastSaved.HasValue ? string.Format(_l("Literary.Draft.Saved"), _lastSaved.Value.ToString("HH:mm:ss")) : "";
        _status.Text = _statusKey == "Literary.Draft.Saved" ? saved : _l(_statusKey) + (saved.Length > 0 ? " · " + saved : "");
        if (!_loadFailed && Store.Index.Parts.Count > 0) _editor.ToolTip = Store.FilePath;
    }
}
