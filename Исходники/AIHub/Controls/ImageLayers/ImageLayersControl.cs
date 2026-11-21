using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TreeView = System.Windows.Controls.TreeView;
using UserControl = System.Windows.Controls.UserControl;
using PixelRect = System.Drawing.Rectangle;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl : UserControl
{
    private readonly Window _owner;
    private readonly Func<string, string> _l;
    private readonly Func<bool> _russian;
    private readonly IImageLayerAnalyzer _analyzer;
    private readonly Func<LayerAnalysisPreparation> _preparation;
    private readonly LayerCanvas _canvas = new();
    private readonly TreeView _tree = new();
    private readonly ListBox _history = new();
    private readonly ComboBox _scope = new(), _depth = new(), _spacing = new(), _view = new();
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _zoom = new() { MinWidth = 54 };
    private readonly TextBlock _historyTitle = new();
    private readonly TextBlock _caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _welcome = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 3 };
    private Button _save = null!, _saveAs = null!, _open = null!, _analyze = null!, _refine = null!, _reload = null!;
    private Button _handTool = null!, _regionTool = null!, _pointTool = null!, _pencilTool = null!, _cancel = null!;
    private LayerDocument? _document;
    private string? _path;
    private Guid? _selected;
    private LayerMask? _selectedMask;
    private PixelRect? _region;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _workFinished;
    private bool _dirty, _confirming, _analyzing, _rebuildingLayers;
    private sealed record HistoryRow(string Label, Guid? Version, bool Current)
    {
        public override string ToString() => Label;
    }

    internal ImageLayersControl(Window owner, Func<string, string> localize, Func<bool> russian,
        IImageLayerAnalyzer analyzer, Func<LayerAnalysisPreparation> preparation)
    {
        _owner = owner; _l = localize; _russian = russian; _analyzer = analyzer; _preparation = preparation;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/ImageLayers/ImageLayersResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "ImageLayers.Page");
        BuildInterface();
        UpdateTitle();
        _canvas.RegionSelected += area =>
        {
            _region = area;
            _scope.SelectedIndex = 1;
            RefreshTools();
        };
        _canvas.LassoSelected += contours => _ = ApplyLassoAsync(contours);
        _canvas.LassoChanged += RefreshLassoBar;
        _canvas.RegionActionsRequested += OpenRegionActions;
        _canvas.ContourEdited += edit => _ = EditAsync(edit);
        _canvas.ZoomChanged += value => _zoom.Text = value + "%";
        _tree.SelectedItemChanged += (_, _) => { if (!_rebuildingLayers) SelectLayer(restorePointSpacing: true); };
        _tree.PreviewMouseLeftButtonDown += FocusLayerOnDoubleClick;
        _history.SelectionChanged += (_, _) => RefreshTools();
        _scope.SelectionChanged += (_, _) =>
        {
            if (_scope.SelectedIndex == 1) _canvas.Tool = LayerCanvasTool.Region;
            RefreshTools();
        };
        foreach (var field in new[] { _depth, _spacing, _view })
            field.SelectionChanged += (_, _) => RefreshTools();
        _history.MouseDoubleClick += (_, _) => _ = RestoreAsync();
        IsVisibleChanged += (_, _) => ReviewWhenVisible();
        owner.StateChanged += OwnerStateChanged;
        owner.Closed += (_, _) => owner.StateChanged -= OwnerStateChanged;
        PreviewKeyDown += Shortcuts;
        KeyDown += ClearRegionShortcut;
        RefreshTools();
    }

    private string T(string key) => _l("ImageLayers." + key);

    private void FocusLayerOnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _rebuildingLayers || !_tree.IsEnabled) return;
        for (var hit = e.OriginalSource as DependencyObject; hit is not null && hit != _tree;
             hit = hit is Visual or Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is ButtonBase or ItemsPresenter) return;
            if (hit is not TreeViewItem { Tag: Guid id } item) continue;
            item.IsSelected = true;
            if (_selected == id) _canvas.FocusLayer();
            e.Handled = true;
            return;
        }
    }

    private void OwnerStateChanged(object? sender, EventArgs e) => ReviewWhenVisible();

    private async Task RunAsync(Func<CancellationToken, Task> action, bool analysis = false)
    {
        if (_operation is not null || _confirming) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workFinished = finished;
        _analyzing = analysis;
        _operation = lifetime;
        RefreshTools();
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { SetStatus(T("Canceled")); }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("image_layers_error", "ImageLayers", detail: error.ToString());
            var rendered = _l(error.Message);
            SetStatus(rendered != error.Message ? rendered : T("Failed"));
            if (rendered == error.Message) _status.ToolTip = error.Message;
        }
        finally
        {
            _operation = null;
            _analyzing = false;
            _workFinished = null;
            finished.TrySetResult();
            RefreshTools();
            ReviewWhenVisible();
        }
    }

    private void SetStatus(string status) { _status.Text = status; _status.ToolTip = status; }

    private void Shortcuts(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _operation is not null) { _operation.Cancel(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        { _ = OpenAsync(); e.Handled = true; }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        { _ = RunAsync(token => SaveAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0, token)); e.Handled = true; }
    }

    internal async Task<bool> ConfirmExitAsync()
    {
        if (_confirming) return false;
        _confirming = true;
        try
        {
            if (!_analyzing && _workFinished is { } finished) await finished.Task;
            return await ConfirmLeaveAsync();
        }
        finally { _confirming = false; }
    }

    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!_dirty) return true;
        var decision = MessageBox.Show(_owner, T("SavePrompt"), T("Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (decision != MessageBoxResult.Yes) return decision == MessageBoxResult.No;
        try { return await SaveAsync(false, ApplicationBackgroundOperations.ExitToken); }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("image_layers_save_error", "ImageLayers", detail: error.ToString());
            SetStatus(T("SaveFailed"));
            return false;
        }
    }

    private void Changed(bool restorePointSpacing = false, bool preferVisibleLayer = false)
    {
        _dirty = true;
        UpdateTitle();
        RebuildLayers(restorePointSpacing, preferVisibleLayer);
    }

    private void UpdateTitle() => _caption.Text = T("Title") + (_document is null ? "" :
        " · " + (_path is null ? _document.Source.Name : Path.GetFileName(_path))) + (_dirty ? " *" : "");
}
