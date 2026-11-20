using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TreeView = System.Windows.Controls.TreeView;

namespace AIHub.Controls;

internal sealed class ModelStorageRelocationControl : System.Windows.Controls.UserControl
{
    private sealed record Selection(ModelStorageScenario Scenario, ModelStorageGroup? Group = null);
    private sealed record Disk(string Name, long Occupied, long? Free);

    private readonly Func<StorageSettings> _getStorage;
    private readonly Action _saveStorage;
    private readonly Func<bool> _externalWork;
    private readonly CancellationToken _exit;
    private readonly ManagedModelLibraryStore _store = new();
    private readonly ModelStorageTransfer _transfer;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 18 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _blocked = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _disks = new();
    private readonly StackPanel _card = new();
    private readonly TreeView _tree = new() { MinHeight = 140, MaxHeight = 460, BorderThickness = new(1), Background = System.Windows.Media.Brushes.Transparent };
    private readonly ComboBox _locations = new() { MinWidth = 180, MaxWidth = 540, Margin = new(0, 0, 8, 4) };
    private readonly List<Button> _transferButtons = [];
    private readonly Button _addFolder, _setDefault, _refresh, _recover;
    private Func<string, string> _translate;
    private ModelStorageScenario[] _scenarios = [];
    private Disk[] _totals = [];
    private Selection? _selected;
    private bool _busy, _reading, _didRead;
    internal bool IsTransferring => _busy;
    internal event Action? AddressesChanged;
    private string _statusKey = "";

    internal ModelStorageRelocationControl(Func<string, string> translate, Func<StorageSettings> getStorage,
        Action saveStorage, Func<bool> externalWork, CancellationToken exit)
    {
        _translate = translate;
        _getStorage = getStorage;
        _saveStorage = saveStorage;
        _externalWork = externalWork;
        _exit = exit;
        _transfer = new(_store.Addresses);
        AutomationProperties.SetAutomationId(this, "ModelStorage.Workspace");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _blocked.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _tree.SetResourceReference(BorderBrushProperty, "LineBrush");
        AutomationProperties.SetAutomationId(_tree, "ModelStorage.Tree");
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is TreeViewItem { Tag: Selection selection })
            { _selected = selection; BuildCard(); }
        };

        _addFolder = ActionButton("AddFolder", async (_, _) =>
        {
            if (PickFolder() is not { } path) return;
            RegisterLocation(path);
            await RefreshAsync();
        });
        _setDefault = ActionButton("SetDefault", async (_, _) =>
        {
            var locations = _getStorage().Models.Locations;
            if (_locations.SelectedItem is not string path || locations.Find(location =>
                    ManagedModelPathIdentity.SameDirectory(location.Path, path)) is not { } chosen) return;
            locations.Remove(chosen);
            locations.Insert(0, chosen);
            _saveStorage();
            await RefreshAsync();
        });
        _refresh = ActionButton("Refresh", async (_, _) => await RefreshAsync());
        _recover = ActionButton("Recover", async (_, _) => await RecoverAsync());
        var toolbar = new WrapPanel { Margin = new(0, 8, 0, 6) };
        foreach (var element in new FrameworkElement[] { _locations, _addFolder, _setDefault, _refresh, _recover }) toolbar.Children.Add(element);
        _locations.SelectionChanged += (_, _) => UpdateActions();
        AutomationProperties.SetAutomationId(_locations, "ModelStorage.Locations");
        var layout = new Grid { Margin = new(0, 8, 0, 4) };
        layout.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        layout.Children.Add(_tree);
        _card.Margin = new(12, 0, 0, 0);
        Grid.SetColumn(_card, 1);
        layout.Children.Add(_card);
        Content = new StackPanel { Children = { _title, _disks, toolbar, _blocked, _status, layout } };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                UpdateActions();
                if (!_didRead) await RefreshAsync();
            }
            else _timer.Stop();
        };
        _timer.Tick += (_, _) => UpdateActions();
        Localize(translate);
    }

    internal void Localize(Func<string, string> translate)
    {
        _translate = translate;
        _title.Text = T("Title");
        _addFolder.Content = T("AddFolder");
        _setDefault.Content = T("SetDefault");
        _refresh.Content = T("Refresh");
        _recover.Content = T("Recover");
        AutomationProperties.SetName(_locations, T("Locations"));
        _blocked.Text = T("Busy");
        if (_statusKey.Length > 0) _status.Text = T(_statusKey);
        BuildTree();
        BuildDiskSummary();
    }

    private async Task RefreshAsync()
    {
        if (_reading) return;
        _reading = true;
        if (!_didRead) Status("Loading");
        try
        {
            var configured = _getStorage().Models.Locations.Select(location => location.Path).ToArray();
            var storage = new StorageSettings { Models = new() { Locations = configured.Select(path => new StorageLocationSettings { Path = path }).ToList() } };
            var snapshot = await Task.Run(() =>
            {
                var scenarios = ModelStorageCatalog.Create(storage, _store);
                return (Scenarios: scenarios, Disks: ScanDisks(configured.Concat(_store.Addresses.Read().Select(item => item.ModelsRoot)), _exit));
            }, _exit);
            if (!_didRead) Status("");
            _didRead = true;
            _scenarios = snapshot.Scenarios; _totals = snapshot.Disks;
            var selected = _locations.SelectedItem as string;
            _locations.ItemsSource = configured;
            _locations.SelectedItem = configured.Contains(selected) ? selected : configured.FirstOrDefault();
            BuildDiskSummary();
            BuildTree();
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { _reading = false; UpdateActions(); }
    }

    private void BuildTree()
    {
        _tree.Items.Clear();
        var root = Node(T("Directions"), "ModelStorage.Directions", true);
        _tree.Items.Add(root);
        TreeViewItem? keep = null;
        foreach (var direction in _scenarios.GroupBy(value => value.DirectionId))
        {
            var parent = root;
            if (!string.IsNullOrEmpty(direction.Key))
            {
                parent = Node(_translate(ScenarioNavigationCatalog.Get(direction.Key).TitleKey), "ModelStorage.Direction." + direction.Key, false);
                root.Items.Add(parent);
            }
            foreach (var scenario in direction)
            {
                var item = Node(_translate(scenario.TitleKey), "ModelStorage.Scenario." + scenario.Id, false);
                item.Tag = new Selection(scenario);
                parent.Items.Add(item);
                if (_selected?.Scenario.Id == scenario.Id && _selected.Group is null) keep = item;
                foreach (var group in scenario.Groups)
                {
                    var leaf = Node(group.Title, "ModelStorage.Group." + group.Id, false);
                    leaf.Tag = new Selection(scenario, group);
                    item.Items.Add(leaf);
                    if (_selected?.Scenario.Id == scenario.Id && _selected.Group?.Id == group.Id)
                    { keep = leaf; item.IsExpanded = true; }
                }
                if (_selected?.Scenario.Id == scenario.Id) parent.IsExpanded = true;
            }
        }
        if (keep is not null) keep.IsSelected = true;
        else _selected = null;
        BuildCard();
    }

    private void BuildCard()
    {
        _card.Children.Clear();
        _transferButtons.Clear();
        if (_selected is not { } selected)
        {
            _card.Children.Add(Label(_scenarios.Length == 0 ? T("Empty") : T("ChooseScenario")));
            return;
        }
        if (selected.Scenario.Groups.Length == 0)
        {
            _card.Children.Add(Label(_translate(selected.Scenario.TitleKey), bold: true));
            _card.Children.Add(Label(T("NoTransferable")));
            return;
        }
        var header = new DockPanel { Margin = new(0, 0, 0, 6) };
        var all = ActionButton("MoveAll", async (_, _) => await MoveAsync(selected.Scenario.Groups));
        DockPanel.SetDock(all, Dock.Right);
        header.Children.Add(all);
        header.Children.Add(Label(_translate(selected.Scenario.TitleKey), bold: true));
        _transferButtons.Add(all);
        _card.Children.Add(header);
        AddColumns(Label(T("Contents"), bold: true), Label(T("Address"), bold: true));
        var shown = selected.Group is { } single ? new[] { single } : selected.Scenario.Groups;
        foreach (var group in shown)
        {
            var contents = new StackPanel();
            contents.Children.Add(Label(group.Title + "\n" + Bytes(group.Bytes), bold: true));
            if (group.Id == MiniMaxImageRuntime.ModelId)
            {
                foreach (var file in group.Artifacts.SelectMany(artifact => artifact.Files))
                    contents.Children.Add(Label(T("MiniMaxPart." + file.Purpose) + " · " + Bytes(file.SizeBytes)));
                var references = Label(T("MiniMaxVideoReferences"));
                AutomationProperties.SetAutomationId(references, "ModelStorage.MiniMaxVideoReferences");
                contents.Children.Add(references);
                if (group.VideoEncoderCopy is { } encoder)
                {
                    var related = Label(VideoEncoderNote(encoder));
                    related.ToolTip = encoder.Path;
                    contents.Children.Add(related);
                }
            }
            else if (group.Artifacts.Length > 1)
            {
                foreach (var artifact in group.Artifacts)
                    contents.Children.Add(Label(artifact.DisplayName));
            }
            var destination = new StackPanel();
            foreach (var address in group.Artifacts.Select(card => card.InstallDirectory).Distinct(ModelStorageAddresses.Paths))
                destination.Children.Add(Label(address));
            var move = ActionButton("ChangeFolder", async (_, _) => await MoveAsync([group]));
            AutomationProperties.SetAutomationId(move, "ModelStorage.Move." + group.Id);
            destination.Children.Add(move);
            _transferButtons.Add(move);
            AddColumns(contents, destination);
        }
        UpdateActions();
    }

    private void AddColumns(FrameworkElement left, FrameworkElement right)
    {
        var row = new Grid { Margin = new(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        left.Margin = new(0, 0, 8, 0);
        Grid.SetColumn(right, 1);
        row.Children.Add(left); row.Children.Add(right); _card.Children.Add(row);
    }

    private async Task MoveAsync(ModelStorageGroup[] groups)
    {
        if (_busy || BackgroundBusy()) { Status("Busy"); return; }
        if (PickFolder() is not { } destination) return;
        var cards = groups.SelectMany(group => group.Artifacts).ToArray();
        _busy = true;
        UpdateActions();
        try
        {
            var plan = await Task.Run(() => _transfer.Plan(cards, destination), _exit);
            if (plan.Items.Length == 0) { Status("SameFolder"); return; }
            var affected = _scenarios.Where(scenario => scenario.Groups.Any(group => group.Artifacts.Any(card =>
                plan.Items.Any(item => ModelStorageAddresses.Matches(item.Address, card.ModelArtifactId, card.Revision)))))
                .Select(scenario => _translate(scenario.TitleKey));
            var miniMax = groups.FirstOrDefault(group => group.Id == MiniMaxImageRuntime.ModelId
                && group.Artifacts.Any(card => plan.Items.Any(item =>
                    ModelStorageAddresses.Matches(item.Address, card.ModelArtifactId, card.Revision))));
            var detail = miniMax is null ? "" : "\n\n" + T("MiniMaxVideoReferences");
            if (miniMax?.VideoEncoderCopy is { } encoder) detail += "\n" + VideoEncoderNote(encoder);
            var sources = string.Join("\n", plan.Items.Select(item => item.SourceRoot).Distinct(ModelStorageAddresses.Paths));
            var confirmation = string.Format(T("Confirm"), string.Join(", ", groups.Select(group => group.Title)), sources,
                destination, Bytes(plan.Bytes), string.Join(", ", affected), detail);
            var window = Window.GetWindow(this);
            if (MessageBox.Show(window, confirmation, T("Title"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            if (BackgroundBusy()) { Status("Busy"); return; }
            using var background = ApplicationBackgroundOperations.Current?.BeginAuxiliary("ModelStorage.Busy");
            await ApplicationBackgroundOperations.RetireModelsAsync();
            using var lease = ModelStorageWorkGate.Current.TryBeginTransfer();
            if (lease is null) { Status("Busy"); return; }
            var owner = window is null ? 0 : new WindowInteropHelper(window).Handle;
            Status("Copying");
            var result = await Task.Run(() => _transfer.MoveAsync(plan, owner,
                () => Dispatcher.InvokeAsync(() => Status("Verifying")), _exit));
            RegisterLocation(destination);
            AddressesChanged?.Invoke();
            if (result.CleanupPending) Status("RecoveryPending");
            else { _statusKey = ""; _status.Text = string.Format(T("Released"), Bytes(result.ReleasedBytes)); }
        }
        catch (OperationCanceledException) { Status("Canceled"); }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await RefreshAsync(); UpdateActions(); }
    }

    private async Task RecoverAsync()
    {
        if (BackgroundBusy()) { Status("Busy"); return; }
        try
        {
            using var background = ApplicationBackgroundOperations.Current?.BeginAuxiliary("ModelStorage.Busy");
            using var lease = ModelStorageWorkGate.Current.TryBeginTransfer();
            if (lease is null) { Status("Busy"); return; }
            _busy = true; UpdateActions();
            Status("Verifying");
            var result = await Task.Run(() => _transfer.TryRecover());
            Status(result.CleanupPending ? "RecoveryPending" : "Recovered");
        }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await RefreshAsync(); UpdateActions(); }
    }

    private bool BackgroundBusy()
    {
        if (_externalWork() || ApplicationBackgroundOperations.Current?.BlocksNewWork == true) return true;
        try
        {
            using var probe = ModelStorageWorkGate.Current.TryBeginTransfer();
            return probe is null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private void UpdateActions()
    {
        var background = !_busy && IsVisible && BackgroundBusy();
        var pending = File.Exists(_store.Addresses.JournalPath);
        _blocked.Visibility = background ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _transferButtons) button.IsEnabled = !_busy && !_reading && !background && !pending;
        _addFolder.IsEnabled = _refresh.IsEnabled = !_busy && !_reading;
        _setDefault.IsEnabled = !_busy && !_reading && _locations.SelectedIndex > 0;
        _recover.IsEnabled = pending && !_busy && !_reading && !background;
        _recover.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("PickFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null;
        if (ManagedModelPathIdentity.SameDirectory(Path.GetPathRoot(dialog.FolderName), dialog.FolderName))
        { Status("ChooseSubfolder"); return null; }
        return dialog.FolderName;
    }

    private void RegisterLocation(string path)
    {
        var locations = _getStorage().Models.Locations;
        if (locations.Any(location => ManagedModelPathIdentity.SameDirectory(location.Path, path))) return;
        locations.Add(new() { Path = path });
        _saveStorage();
    }

    private static Disk[] ScanDisks(IEnumerable<string> locations, CancellationToken token)
    {
        var candidates = locations.Where(path => Path.IsPathFullyQualified(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(ModelStorageAddresses.Paths).ToArray();
        var roots = candidates.Where(path => !candidates.Any(other => ModelStorageAddresses.IsBelow(other, path)));
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return roots.GroupBy(path => Path.GetPathRoot(path)!, ModelStorageAddresses.Paths).Select(group =>
        {
            long occupied = 0;
            long? available = null;
            try { available = new DriveInfo(group.Key).AvailableFreeSpace; } catch (IOException) { }
            foreach (var folder in group.Where(folder => !ManagedModelPathIdentity.SameDirectory(folder, group.Key) && Directory.Exists(folder)))
            {
                try
                {
                    foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                    { token.ThrowIfCancellationRequested(); occupied += file.Length; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return new Disk(group.Key.TrimEnd(Path.DirectorySeparatorChar), occupied, available);
        }).ToArray();
    }

    private void BuildDiskSummary()
    {
        _disks.Children.Clear();
        foreach (var disk in _totals)
            _disks.Children.Add(Label(disk.Free is { } free
                ? string.Format(T("Disk"), disk.Name, Bytes(disk.Occupied), Bytes(free))
                : string.Format(T("DiskOffline"), disk.Name)));
    }

    private Button ActionButton(string key, RoutedEventHandler action)
    {
        var button = new Button { Content = T(key), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 0, 8, 4) };
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        AutomationProperties.SetAutomationId(button, "ModelStorage." + key);
        button.Click += action;
        return button;
    }

    private static TreeViewItem Node(string text, string id, bool expanded)
    {
        var item = new TreeViewItem { Header = text, IsExpanded = expanded };
        item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(item, id);
        return item;
    }

    private static TextBlock Label(string text, bool bold = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 3) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        if (bold) label.FontWeight = FontWeights.SemiBold;
        return label;
    }

    private void ShowError(Exception error)
    {
        OwnedProcessRegistry.Log("model_storage_error", "ModelStorage", detail: error.GetType().Name + ": " + error.Message);
        if (error is ModelStorageSpaceException space)
        { _statusKey = ""; _status.Text = string.Format(T("Space"), Bytes(space.Required), Bytes(space.Available)); }
        else Status(error.Message.StartsWith("ModelStorage.", StringComparison.Ordinal) ? error.Message["ModelStorage.".Length..] : "Failed");
    }

    private void Status(string key) { _statusKey = key; _status.Text = key.Length == 0 ? "" : T(key); }
    private string VideoEncoderNote(ModelStorageVideoEncoderCopy encoder) => string.Format(T("MiniMaxVideoEncoderCopy"), Bytes(encoder.Bytes));
    private string Bytes(long bytes) => string.Format(T("Bytes"), bytes / 1_000_000_000d);
    private string T(string key) => _translate("ModelStorage." + key);
}
