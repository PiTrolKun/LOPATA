using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using PixelPoint = System.Drawing.PointF;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private LayerLassoDestination _lassoDestination;
    private Guid? _lassoTarget;
    private WrapPanel _lassoBar = null!;
    private TextBlock _lassoMode = null!;
    private CheckBox _lassoMagnet = null!;
    private Button _lassoNext = null!, _lassoFinish = null!, _lassoClear = null!;

    private WrapPanel BuildLassoBar()
    {
        _lassoMode = new TextBlock
        {
            MaxWidth = 350, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 5)
        };
        _lassoMagnet = new CheckBox
        {
            Content = T("LassoMagnetic"), ToolTip = T("LassoMagneticHelp"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 5)
        };
        AutomationProperties.SetAutomationId(_lassoMagnet, "ImageLayers.LassoMagneticToggle");
        _lassoMagnet.Click += (_, _) =>
        {
            _canvas.LassoMagnetic = _lassoMagnet.IsChecked == true;
            _canvas.Focus();
        };
        _lassoNext = Command("LassoNext", () => { _canvas.CloseLassoContour(); _canvas.Focus(); });
        _lassoFinish = Command("LassoFinish", () => { _canvas.Focus(); _canvas.FinishLasso(); });
        _lassoClear = Command("LassoClear", () => { _canvas.ResetLasso(); _canvas.Focus(); });
        return _lassoBar = Row(_lassoMode, _lassoMagnet, _lassoNext, _lassoFinish, _lassoClear);
    }

    private void RefreshLassoBar()
    {
        var active = _document is not null && _canvas.Tool == LayerCanvasTool.Lasso;
        _lassoBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;
        var ready = _operation is null;
        _lassoMagnet.IsEnabled = ready;
        _lassoMagnet.IsChecked = _canvas.LassoMagnetic;
        _lassoNext.IsEnabled = ready && _canvas.CanCloseLassoContour;
        _lassoFinish.IsEnabled = ready && _canvas.CanFinishLasso;
        _lassoClear.IsEnabled = ready && _canvas.HasLassoSelection;
        _lassoMode.Text = _lassoDestination switch
        {
            LayerLassoDestination.SelectedLayer => _lassoTarget is Guid id && _document!.Layers.TryGetValue(id, out var layer)
                ? string.Format(T("LassoSelectedLayer"), layer.Name) : T("LassoSelectedUnavailable"),
            LayerLassoDestination.IntersectingLayer => T("LassoIntersectingLayer"),
            _ => T("LassoNewLayer")
        };
        _lassoMode.ToolTip = _lassoMode.Text;
    }

    private void ChooseLasso()
    {
        if (_document is not { } document || _operation is not null || _confirming) return;
        var selectedId = _selected;
        var selected = selectedId is Guid id ? document.Layers.GetValueOrDefault(id) : null;
        var dialog = ImageLayersToolDialogs.LassoOptions(_owner, T, selected?.Name,
            document.Layers.Values.Any(layer => layer.Mask is not null),
            new(_lassoDestination, _canvas.LassoMagnetic), out var value);
        _confirming = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
            var options = value();
            _lassoDestination = options.Destination;
            _lassoTarget = options.Destination == LayerLassoDestination.SelectedLayer ? selectedId : null;
            _canvas.LassoMagnetic = options.Magnetic;
            SetTool(LayerCanvasTool.Lasso);
        }
        finally
        {
            _confirming = false;
            ReviewWhenVisible();
        }
    }

    private Task ApplyLassoAsync(PixelPoint[][] contours) => RunAsync(async token =>
    {
        if (_document is not { } document) return;
        try
        {
            var selection = await Task.Run(() => LayerLasso.Select(document.Source, contours, token), token);
            if (!selection.Any) { SetStatus(T("ManualEmpty")); return; }
            if (_lassoDestination == LayerLassoDestination.NewLayer)
            {
                if (await CreateManualLayerAsync(document, selection, token)) _canvas.ResetLasso();
                return;
            }

            var target = _lassoTarget;
            if (_lassoDestination == LayerLassoDestination.IntersectingLayer)
            {
                var hits = await Task.Run(() => LayerLasso.FindTargets(document, selection, token), token);
                if (hits.Length == 0) { SetStatus(T("LassoNoIntersection")); return; }
                target = hits[0].Id;
                if (hits.Length > 1)
                {
                    var dialog = ImageLayersToolDialogs.LassoTarget(_owner, T, hits, out var value);
                    if (!ReviewAccepted(dialog, token)) { SetStatus(T("Canceled")); return; }
                    target = value();
                }
            }
            if (target is not Guid id || !document.Layers.ContainsKey(id))
            { SetStatus(T("LassoTargetMissing")); return; }

            try
            {
                var plan = await Task.Run(() => document.PlanBoundary(id, document.Aggregate(id).Union(selection)), token);
                token.ThrowIfCancellationRequested();
                var changed = document.Commit(plan, LayerDocument.LassoMergeKind, id, selection.Bounds(document.Source.Width));
                _canvas.ResetLasso();
                if (changed)
                {
                    _selected = id;
                    Changed();
                }
                SetStatus(T(changed ? "LassoMerged" : "NoDifference"));
            }
            finally { document.DiscardPreviewMasks(); }
        }
        finally { if (_canvas.Tool == LayerCanvasTool.Lasso) _canvas.Focus(); }
    });
}
