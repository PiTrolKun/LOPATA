using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VectorGraphics;

namespace AIHub.Controls;

public sealed partial class VectorGenerationControl
{
    private VectorLiveState? _liveState;
    private VectorLivePreview? _liveView;
    private string _liveJobId = "";
    private IProgress<VectorStreamChunk> BeginLive(VectorRequest request)
    {
        var state = new VectorLiveState(); _liveState = state; _liveJobId = request.Id;
        return new InlineProgress<VectorStreamChunk>(state.Append);
    }
    private void EndLive() { _liveState = null; _liveJobId = ""; }
    private bool ShowLive(Grid center, VectorResult? selected)
    {
        if (!_busy || _exporting || _liveState is null || selected?.Request.Id != _liveJobId) return false;
        _liveView = new(_liveState, L); center.Children.Add(_liveView); return true;
    }
    private void CloseLiveView() { _liveView?.Dispose(); _liveView = null; }
}
