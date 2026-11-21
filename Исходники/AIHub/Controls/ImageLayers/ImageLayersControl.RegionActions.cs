using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AIHub.Services.ImageLayers;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Point = System.Windows.Point;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private ContextMenu? _regionActions;
    private bool HasActiveRegion => _scope.SelectedIndex == 1 && _region.HasValue;

    private void OpenRegionActions(Point position)
    {
        if (_document is null || _operation is not null || _confirming || !HasActiveRegion) return;
        var area = _region!.Value;
        var menu = _regionActions ??= CreateActionMenu();
        menu.Items.Clear();
        AutomationProperties.SetAutomationId(menu, "ImageLayers.Region.Actions");
        var hasLayer = _selected.HasValue;
        var hasContour = _selectedMask?.Any == true;
        Add("Analyze", () => _ = AnalyzeAsync(false));
        Add("EdgeDetails", () => _ = RefineEdgesAsync(), hasContour);
        Add("SelectByMarkers", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Watershed));
        Add("FindByDescription", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Describe));
        Add("RecognizeText", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Text));
        Add("CutOutPerson", () => _ = RunAdditionalAsync(LayerAdditionalMethod.Portrait));
        AddMenuSeparator(menu);
        Add("CreateManualLayer", () => _ = CreateManualLayerAsync(area));
        Add("IncludeRegion", () => _ = EditRegionAsync(LayerDocument.IncludeRegionKind), hasLayer);
        Add("ExcludeRegion", () => _ = EditRegionAsync(LayerDocument.ExcludeRegionKind), hasContour);
        Add("RemoveRegionContours", () => _ = EditRegionAsync(LayerDocument.RemoveRegionContoursKind), hasContour);
        AddMenuSeparator(menu);
        Add("ClearRegion", () => ClearRegion());
        menu.PlacementTarget = _canvas;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = position.X;
        menu.VerticalOffset = position.Y;
        menu.IsOpen = true;

        void Add(string key, Action action, bool enabled = true)
        {
            AddMenuAction(menu, key, () =>
            {
                menu.IsOpen = false;
                if (HasActiveRegion && _region == area) action();
            }, "ImageLayers.Region.").IsEnabled = enabled;
        }
    }

    private Task EditRegionAsync(string kind) => RunAsync(async token =>
    {
        if (_document is not { } document || _selected is not Guid layer || !HasActiveRegion) return;
        var area = _region!.Value;
        try
        {
            var plan = await Task.Run(() => document.PlanRegionEdit(layer, area, kind), token);
            token.ThrowIfCancellationRequested();
            if (document.Commit(plan, kind, layer, area))
            {
                Changed();
                SetStatus(T("ContourUpdated"));
            }
            else SetStatus(T(kind == LayerDocument.RemoveRegionContoursKind ? "NoClosedContoursInRegion" : "NoDifference"));
        }
        finally { document.DiscardPreviewMasks(); }
    });

    private void ClearRegionShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.IsRepeat && _regionActions?.IsOpen != true && ClearRegion())
            e.Handled = true;
    }

    private bool ClearRegion()
    {
        if (!HasActiveRegion || _operation is not null || _confirming) return false;
        _region = null;
        _scope.SelectedIndex = 0;
        SetStatus(T("RegionCleared"));
        return true;
    }
}
