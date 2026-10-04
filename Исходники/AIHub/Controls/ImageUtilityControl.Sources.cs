using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using TextBox = System.Windows.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using TreeView = System.Windows.Controls.TreeView;
using Point = System.Windows.Point;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl
{
    private UIElement BuildSources()
    {
        var dock = new DockPanel(); var buttons = new StackPanel(); _sourceButtons = buttons;
        buttons.Children.Add(ImageUtilityUi.Text(L("Sources"), true));
        buttons.Children.Add(ActionButton("AddLinks", AddLinksAsync));
        buttons.Children.Add(ActionButton("AddFiles", () => PickFilesAsync(false)));
        buttons.Children.Add(ActionButton("AddFolder", PickFolderAsync));
        buttons.Children.Add(ActionButton("Generations", () => PickFilesAsync(true)));
        buttons.Children.Add(ActionButton("Paste", PasteAsync));
        buttons.Children.Add(ImageUtilityUi.Text(L("TreeHint"))); DockPanel.SetDock(buttons, Dock.Top); dock.Children.Add(buttons);
        var tree = new TreeView { BorderThickness = new(0) }; tree.SetResourceReference(BackgroundProperty, "PanelBrush");
        tree.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(tree, "ImageUtility.Sources");
        foreach (var drive in DriveInfo.GetDrives()) tree.Items.Add(FileNode(drive.RootDirectory.FullName, true));
        dock.Children.Add(tree); return dock;
    }
    private TreeViewItem FileNode(string path, bool directory)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var item = new TreeViewItem { Header = (directory ? "▸ " : "▧ ") + (string.IsNullOrEmpty(name) ? path : name), Tag = path, ToolTip = path };
        item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        if (directory)
        {
            item.Items.Add(new TreeViewItem { Header = "…" });
            item.Expanded += async (_, e) =>
            {
                if (e.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
                try
                {
                    var children = await Task.Run(() => Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false })
                        .Select(child => (Path: child, Directory: Directory.Exists(child))).OrderByDescending(x => x.Directory).ThenBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToArray());
                    item.Items.Clear(); foreach (var child in children) item.Items.Add(FileNode(child.Path, child.Directory));
                }
                catch (Exception error) { item.Items.Clear(); Error(error); }
            };
        }
        var menu = new ContextMenu();
        menu.Items.Add(Menu("ShowExplorer", () => ShowExplorer(path)));
        if (!directory) menu.Items.Add(Menu("ViewFile", () => ViewFileRequested?.Invoke(path)));
        item.ContextMenu = menu;
        Point? pressed = null;
        item.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Bubbling from a child row must never drag an ancestor directory.
            if (FindNode(e.OriginalSource as DependencyObject) == item) pressed = e.GetPosition(item);
        };
        item.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed || IsBusy) { pressed = null; return; }
            var point = e.GetPosition(item);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null; e.Handled = true; DragDrop.DoDragDrop(item, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
        };
        return item;
    }
    private static TreeViewItem? FindNode(DependencyObject? value)
    {
        while (value is not null && value is not TreeViewItem) value = VisualTreeHelper.GetParent(value);
        return value as TreeViewItem;
    }
    private MenuItem Menu(string key, Action action)
    {
        var item = new MenuItem { Header = L(key) }; item.Click += (_, _) => { try { action(); } catch (Exception error) { Error(error); } }; return item;
    }
    private async Task PickFilesAsync(bool generations)
    {
        if (IsBusy || HasPendingOperation()) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = L("AddFiles"), Filter = L("ImageFilter"),
            InitialDirectory = generations && Directory.Exists(_generationFolder) ? _generationFolder : "" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await AddSourcesAsync(picker.FileNames);
    }
    private async Task PickFolderAsync()
    {
        if (IsBusy || HasPendingOperation()) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("AddFolder"), Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await AddSourcesAsync(picker.FolderNames);
    }
    private async Task AddLinksAsync()
    {
        if (IsBusy || HasPendingOperation()) return;
        var window = new Window { Owner = Window.GetWindow(this), Title = L("AddLinks"), Width = 660, Height = 450, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Resources.MergedDictionaries.Add(Resources); window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        var dock = new DockPanel { Margin = new(18) }; var hint = ImageUtilityUi.Text(L("LinksHint")); DockPanel.SetDock(hint, Dock.Top); dock.Children.Add(hint);
        var input = ImageUtilityUi.Input("", "Links"); input.AcceptsReturn = true; input.AcceptsTab = false; input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var add = ActionButton("Add", () => { window.DialogResult = true; }); DockPanel.SetDock(add, Dock.Bottom); dock.Children.Add(add); dock.Children.Add(input); window.Content = dock;
        if (window.ShowDialog() == true) await AddSourcesAsync(input.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
    private async Task PasteAsync()
    {
        if (IsBusy || HasPendingOperation()) return;
        if (Clipboard.ContainsFileDropList()) { await AddSourcesAsync(Clipboard.GetFileDropList().Cast<string>()); return; }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
        {
            var folder = Path.Combine(AppDataPaths.BaseDirectory, "image-utility", "clipboard"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "Clipboard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".png");
            using (var output = File.Create(path)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output); }
            await AddSourcesAsync([path]); return;
        }
        if (Clipboard.ContainsText()) { await AddSourcesAsync(Clipboard.GetText().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); return; }
        AppendLog(L("ClipboardEmpty"));
    }
    private void QueueDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsBusy && !HasPendingOperation() && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private async void QueueDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (IsBusy || HasPendingOperation()) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddSourcesAsync(files);
            else if (e.Data.GetData(DataFormats.UnicodeText) is string text) await AddSourcesAsync(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (Exception error) { Error(error); }
    }
    private async Task AddSourcesAsync(IEnumerable<string> sources)
    {
        if (IsBusy || HasPendingOperation()) return;
        _importCancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        var token = _importCancel.Token;
        _adding = true; RefreshBackgroundStatus();
        try
        {
            if (_job.Finished) BeginNewBatch(_job.Items.Where(x => x.Status is ImageUtilityItemStatus.Pending or ImageUtilityItemStatus.Cancelled));
            var previousIds = _job.Items.Select(x => x.Id).ToHashSet();
            await ImageUtilitySources.AddAsync(_job, sources, Options.IncludeSubfolders, Progress(), token);
            foreach (var item in _job.Items.Where(x => !previousIds.Contains(x.Id) && x.Status != ImageUtilityItemStatus.Duplicate && !x.IsUrl))
            {
                token.ThrowIfCancellationRequested();
                try { var info = await _processor.InspectAsync(item.LocalPath ?? item.Source, token); item.Width = info.Width; item.Height = info.Height; item.Frames = info.Frames; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Inspection is advisory. The processing queue owns attempts and the final failure status.
                    item.Status = ImageUtilityItemStatus.Pending; item.Error = error.Message;
                    item.ErrorKey = (error as ImageUtilityException)?.MessageKey;
                    AppendLog(item.DisplayName + " · " + ImageUtilityUi.ErrorText(L, item.ErrorKey, error.Message), true);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { AppendLog(L("Stopped")); }
        finally
        {
            _adding = false; _importCancel.Dispose(); _importCancel = null;
            try { _store.SaveJob(_job); } finally { RefreshRows(true); }
        }
    }
    private static void TryThumbnail(ImageUtilityRow row)
    {
        var path = row.Item.LocalPath ?? (row.Item.IsUrl ? null : row.Item.Source);
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); row.Thumbnail = bitmap;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or ArgumentException) { }
    }
    private void QueueContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_list?.SelectedItem is not ImageUtilityRow row) { e.Handled = true; return; }
        _list.ContextMenu.Items.Clear(); _list.ContextMenu.Items.Add(Menu("ViewFile", () => OpenItem(row.Item)));
        var path = row.Item.OutputPath ?? row.Item.LocalPath ?? (row.Item.IsUrl ? null : row.Item.Source);
        if (path is not null) _list.ContextMenu.Items.Add(Menu("ShowExplorer", () => ShowExplorer(path)));
        _list.ContextMenu.Items.Add(Menu("CopySource", () => Clipboard.SetText(row.Item.Source)));
        if (!IsBusy && !HasPendingOperation()) _list.ContextMenu.Items.Add(Menu("RemoveSelected", RemoveSelected));
    }
    private void OpenItem(ImageUtilityItem item)
    {
        var path = item.OutputPath ?? item.LocalPath ?? (item.IsUrl ? null : item.Source);
        if (path is not null && File.Exists(path)) ViewFileRequested?.Invoke(path);
    }
    private static void ShowExplorer(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (Directory.Exists(path)) info.ArgumentList.Add(path); else info.Arguments = "/select,\"" + path + "\"";
        Process.Start(info);
    }
    private void OpenOutput() { var path = _job.OutputFolder ?? _previousOutputFolder ?? Options.ExportFolder; if (Directory.Exists(path)) ShowExplorer(path); }
}
