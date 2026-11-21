using System.Drawing;
using AIHub.Services.ImageLayers;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private Task CreateManualLayerAsync(Rectangle area) => RunAsync(async token =>
    {
        if (_document is not { } document) return;
        var source = document.Source;
        var mask = await Task.Run(() => LayerMask.Area(source.Width, source.Height, area)
            .Intersect(source.VisibleMask(token)), token);
        if (!mask.Any) { SetStatus(T("ManualEmpty")); return; }
        await CreateManualLayerAsync(document, mask, token);
    });

    private async Task<bool> CreateManualLayerAsync(LayerDocument document, LayerMask mask, CancellationToken token)
    {
        var dialog = ImageLayersToolDialogs.LayerName(_owner, T, out var name);
        if (!ReviewAccepted(dialog, token)) { SetStatus(T("Canceled")); return false; }
        var chosenName = name();
        try
        {
            var plan = await Task.Run(() => document.PlanAdditionalLayer(chosenName, mask), token);
            token.ThrowIfCancellationRequested();
            _selected = plan.Keys.Single(id => !document.Layers.ContainsKey(id));
            document.Commit(plan, LayerDocument.ManualLayerKind, _selected, mask.Bounds(document.Source.Width));
            _view.SelectedIndex = 0;
            Changed();
            SetStatus(T("ManualCreated"));
            return true;
        }
        finally { document.DiscardPreviewMasks(); }
    }
}
