using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using PixelRect = System.Drawing.Rectangle;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private Button _additional = null!;
    private Button _edgeDetails = null!;
    private MenuItem _matteAction = null!, _portraitAction = null!, _markerAction = null!, _describeAction = null!,
        _textAction = null!, _exportCutout = null!;

    private Button BuildAdditionalMenu()
    {
        _additional = ToolCommand("AdditionalTools", () =>
        {
            _additional.ContextMenu!.PlacementTarget = _additional;
            _additional.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _additional.ContextMenu.IsOpen = true;
        });
        var menu = CreateActionMenu();
        _matteAction = AddMenuAction(menu, "SoftenSelected", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Matte));
        _portraitAction = AddMenuAction(menu, "CutOutPerson", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Portrait));
        _markerAction = AddMenuAction(menu, "SelectByMarkers", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Watershed));
        _describeAction = AddMenuAction(menu, "FindByDescription", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Describe));
        _textAction = AddMenuAction(menu, "RecognizeText", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Text));
        AddMenuSeparator(menu);
        _exportCutout = AddMenuAction(menu, "ExportCutout", () => _ = ExportCutoutAsync());
        _additional.ContextMenu = menu;
        return _additional;
    }

    private static ContextMenu CreateActionMenu()
    {
        var menu = new ContextMenu { Template = FlatListTemplates.Menu };
        menu.SetResourceReference(BackgroundProperty, "PanelBrush");
        menu.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        return menu;
    }

    private MenuItem AddMenuAction(ContextMenu menu, string key, Action action, string prefix = "ImageLayers.")
    {
        var item = new MenuItem { Header = T(key), Template = FlatListTemplates.Item };
        AutomationProperties.SetAutomationId(item, prefix + key);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private static void AddMenuSeparator(ContextMenu menu) =>
        menu.Items.Add(new System.Windows.Controls.Separator { Template = FlatListTemplates.Separator });

    private void RefreshAdditionalTools(bool ready, bool hasLayer, bool cutoutReady)
    {
        _additional.IsEnabled = ready;
        _matteAction.IsEnabled = ready && hasLayer;
        var hasArea = _scope.SelectedIndex switch { 1 => _region is not null, 2 => hasLayer, _ => true };
        _portraitAction.IsEnabled = _markerAction.IsEnabled = _describeAction.IsEnabled = _textAction.IsEnabled = ready && hasArea;
        _exportCutout.IsEnabled = ready && cutoutReady;
        _edgeDetails.IsEnabled = ready && hasLayer && (_scope.SelectedIndex != 1 || _region is not null);
    }

    private Task RefineEdgesAsync() => RunAsync(async token =>
    {
        if (_document is not { } document || _selected is not Guid selected || _selectedMask is not { Any: true } shape) return;
        var area = LayerEdgeDetails.WorkingArea(document.Source, shape);
        if (_scope.SelectedIndex == 1)
        {
            area = PixelRect.Intersect(area, _region ?? PixelRect.Empty);
            if (area.Width < 2 || area.Height < 2) { SetStatus(T("EdgeRegionEmpty")); return; }
        }
        var input = new LayerAnalysisInput(document.Id, document.History.LastOrDefault()?.Id,
            area, selected, LayerDepth.Objects, false, _russian(), false, Extra: new(LayerAdditionalMethod.EdgeDetails));
        await StartAnalysisAsync(document, input, token);
    }, analysis: true);

    private Task RunAdditionalAsync(LayerAdditionalMethod method) => RunAsync(async token =>
    {
        if (_document is not { } document) return;
        var area = AnalysisArea(document, false, out var scopedTarget);
        Guid? target = null;
        var options = new LayerAdditionalOptions(method);
        if (method == LayerAdditionalMethod.Matte)
        {
            if (_selected is not Guid selected || _selectedMask?.Any != true) return;
            target = selected;
            area = PixelRect.Intersect(document.Source.Frame, PixelRect.Inflate(_selectedMask.Bounds(document.Source.Width), 8, 8));
        }
        else if (scopedTarget is Guid id && _selectedMask is { Any: true } selectedMask)
        {
            area = method == LayerAdditionalMethod.Text ? TextAnalysisArea(document, id)
                : LayerPortraitParts.Crop(selectedMask, document.Source.Width, document.Source.Frame);
        }
        if (area.Width < 2 || area.Height < 2) { SetStatus(T("NoResult")); return; }
        if (method == LayerAdditionalMethod.Watershed)
        {
            target = scopedTarget;
            var dialog = ImageLayersToolDialogs.Markers(_owner, T, document.Source, area, out var markers);
            if (!ReviewAccepted(dialog, token)) { SetStatus(T("Canceled")); return; }
            options = options with { Markers = markers() };
        }
        else if (method == LayerAdditionalMethod.Describe)
        {
            var dialog = ImageLayersToolDialogs.Description(_owner, T, out var description);
            if (!ReviewAccepted(dialog, token)) { SetStatus(T("Canceled")); return; }
            options = options with { Description = description() };
        }
        token.ThrowIfCancellationRequested();
        var input = new LayerAnalysisInput(document.Id, document.History.LastOrDefault()?.Id,
            area, target, LayerDepth.Objects, false, _russian(), false, Extra: options);
        await StartAnalysisAsync(document, input, token);
    }, analysis: true);

    private Task ExportCutoutAsync() => RunAsync(async token =>
    {
        if (_document is not { } document || _selected is not Guid id || document.ActiveMatte(id) is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T("ExportCutout"), Filter = T("CutoutFilter"), DefaultExt = ".png", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(document.Source.Name) + "-cutout.png"
        };
        if (dialog.ShowDialog(_owner) != true) return;
        await Task.Run(() => LayerMatteExport.Save(document, id, dialog.FileName, token), token);
        SetStatus(T("CutoutSaved"));
    });
}
