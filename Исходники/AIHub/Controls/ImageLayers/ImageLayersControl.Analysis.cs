using System.Drawing;
using System.IO;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using System.Windows;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private readonly LayerAnalysisWork _analysisWork = LayerAnalysisWork.Default;
    private (BackgroundOperationState State, LayerAnalysisResult Result)? _pendingReview;
    private bool CanReview => !_confirming && IsVisible && _owner is { IsVisible: true, WindowState: not WindowState.Minimized }
        && _owner is not MainWindow { ImageLayersExitPending: true };

    internal bool CanReceiveResult(LayerAnalysisInput input) => _operation is null && !_confirming
        && (_document is null || input.Matches(_document));

    private Task AnalyzeAsync(bool refine) => RunAsync(async token =>
    {
        if (_document is not { } document) return;
        var area = AnalysisArea(document, refine, out var target);
        if (area.IsEmpty) return;
        var modelTarget = target is Guid selected ? document.Layers[selected] : null;
        if (modelTarget?.Text is not null)
        {
            await StartAnalysisAsync(document, new(document.Id, document.History.LastOrDefault()?.Id,
                TextAnalysisArea(document, modelTarget.Id), null, LayerDepth.Objects, false, _russian(), false,
                Extra: new(LayerAdditionalMethod.Text)), token);
            return;
        }
        var bounds = _selectedMask?.Bounds(document.Source.Width) ?? Rectangle.Empty;
        var keepContext = area == document.Source.Frame && modelTarget?.Parent is null
            && bounds.Width >= area.Width * .3 && bounds.Height >= area.Height * .6;
        var method = LayerAnalysisPreparation.ChooseMethod(refine);
        var depth = refine ? LayerDepth.Objects : (LayerDepth)Math.Max(0, _depth.SelectedIndex);
        var input = new LayerAnalysisInput(document.Id, document.History.LastOrDefault()?.Id, area, target,
            depth, refine, _russian(), keepContext, method,
            PortraitParts: !refine && depth == LayerDepth.Parts && method == LayerMaskMethod.Sam2,
            RecognizeText: !refine && target is null && depth != LayerDepth.Objects);
        await StartAnalysisAsync(document, input, token);
    }, analysis: true);

    private async Task StartAnalysisAsync(LayerDocument document, LayerAnalysisInput input, CancellationToken token)
    {
        LayerAnalysisInput? next = input;
        while (next is not null)
            next = await RunAnalysisAsync(document, next, token);
    }

    private async Task<LayerAnalysisInput?> RunAnalysisAsync(LayerDocument document, LayerAnalysisInput input, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        try
        {
            SetStatus(T("Preparing"));
            var preparation = _preparation();
            Task<bool> Prepare(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken ct) =>
                input.CompleteFromCurrentLayers(document) ? Task.FromResult(true)
                    : preparation.PrepareAsync(input.Refine, download, progress, ct, input.MaskMethod, input.PortraitParts,
                        input.Extra, input.RecognizeText);
            if (!await Task.Run(() => Prepare(false, null, token), token))
            {
                var components = !input.Extra.UsesScene() || input.PortraitParts || input.RecognizeText ? "ExtraComponents"
                    : input.MaskMethod == LayerMaskMethod.Sam2 ? "SamComponents" : input.Refine ? "GemmaComponents" : "GammaComponents";
                var setup = new VideoPreparationWindow(_owner, _l, T(components), Prepare, token);
                if (setup.ShowDialog() != true) { SetStatus(T("Canceled")); return null; }
            }

            var title = T("Title");
            var state = await Task.Run(() => _analysisWork.Capture(title, document, input), token);
            var controller = ApplicationBackgroundOperations.Current;
            var finished = false;
            var listening = true;
            void UpdatePauseStatus() => Dispatcher.BeginInvoke(() =>
            {
                if (listening && controller!.PausedOperations.Any(job => job.Id == state.Id))
                    SetStatus(T("Paused"));
            });
            if (controller is not null) controller.Changed += UpdatePauseStatus;
            var engine = new Progress<LayerAnalysisProgress>(value =>
            {
                if (!listening || token.IsCancellationRequested || controller?.PausedOperations.Any(job => job.Id == state.Id) == true) return;
                SetStatus(value.Total == 0 ? T(value.Key) : string.Format(T(value.Key), value.Current, value.Total));
            });
            try
            {
                var result = await _analysisWork.RunAsync(state, document, _analyzer, engine, token);
                finished = true;
                token.ThrowIfCancellationRequested();
                return await PresentOrQueueAsync(state, result, token);
            }
            finally
            {
                listening = false;
                if (controller is not null) controller.Changed -= UpdatePauseStatus;
                if (!finished && !ApplicationBackgroundOperations.ExitToken.IsCancellationRequested
                    && controller?.PendingOperations.Any(job => job.Id == state.Id) != true)
                    _analysisWork.Forget(state.Id);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ComponentAuditFailures.Report(error);
            throw;
        }
    }

    internal Task ViewResultAsync(BackgroundOperationState state) => RunAsync(async token =>
    {
        var result = await Task.Run(() => _analysisWork.OpenResult(state), token);
        await ReviewAndRetryAsync(state, result, token);
    });

    private async Task ReviewAndRetryAsync(BackgroundOperationState state, LayerAnalysisResult result, CancellationToken token)
    {
        if (await PresentOrQueueAsync(state, result, token) is { } retry && _document is { } document)
            await StartAnalysisAsync(document, retry, token);
    }

    private async Task<LayerAnalysisInput?> PresentOrQueueAsync(BackgroundOperationState state, LayerAnalysisResult result, CancellationToken token)
    {
        if (!CanReview)
        {
            _pendingReview = (state, result);
            SetStatus(T("ReviewReady"));
            return null;
        }
        _pendingReview = null;
        var input = LayerAnalysisWork.ReadInput(state);
        var previous = input.Matches(_document) ? _document! : null;
        if (previous is null)
        {
            if (!await ConfirmLeaveAsync()) return null;
            previous = await Task.Run(() => _analysisWork.OpenSource(state), token);
            token.ThrowIfCancellationRequested();
            // Restore the exact working snapshot and let the user choose where to save the recovered project.
            SetDocument(previous, null, input.Target);
        }
        var preview = result.Preview;
        var draft = preview.History.Count == previous.History.Count + 1 ? preview.History[^1] : null;
        if (preview.Id != previous.Id || (draft is null && !input.Matches(preview))
            || draft is not null && (draft.Kind != input.Operation || draft.Target != input.Target || draft.Area != input.Area
                || (preview.History.Count > 1 ? preview.History[^2].Id : (Guid?)null) != input.Version))
            throw new InvalidDataException("ImageLayers.InvalidProject");

        var area = input.Area;
        var completeFrame = input.Extra?.Method == LayerAdditionalMethod.CompleteFrame;
        var summary = result.Review is { } sceneReview ? FormatSceneReview(sceneReview)
            : completeFrame ? T("FrameCoverageReady") : null;
        var wasReviewed = false;
        try
        {
            if (draft is null && input.Refine && result.Skipped > 0)
            {
                SetStatus(T("RefineNoSecond"));
                wasReviewed = true;
                return null;
            }
            foreach (var change in draft?.Changes ?? [])
            {
                previous.Remember(preview.Direct(change.After));
                if (change.After.Matte is { } matte) previous.Remember(preview.Matte(matte.Key));
            }
            var analyzedRegion = LayerMask.Area(previous.Source.Width, previous.Source.Height, area);
            var alreadyMarked = input.Refine || previous.Layers.Values.Any(layer => previous.Direct(layer).Meets(analyzedRegion));
            IReadOnlyDictionary<Guid, ImageLayer>? adopted = preview.Layers;
            if (input.Extra is { Method: LayerAdditionalMethod.EdgeDetails } edges)
            {
                var review = ImageLayersToolDialogs.EdgeReview(_owner, T, previous, preview, input.Target!.Value,
                    area, edges.Strokes ?? [], out var revision);
                if (!ReviewAccepted(review, token))
                {
                    SetStatus(T("Kept"));
                    wasReviewed = !token.IsCancellationRequested;
                    return null;
                }
                if (revision() is { } strokes)
                {
                    token.ThrowIfCancellationRequested();
                    wasReviewed = true;
                    return input with { Extra = edges with { Strokes = strokes } };
                }
            }
            else if (draft is not null && input.Extra?.Method == LayerAdditionalMethod.Matte)
            {
                var review = ImageLayersToolDialogs.CutoutReview(_owner, T, previous, preview, input.Target!.Value, area);
                if (!ReviewAccepted(review, token))
                {
                    SetStatus(T("Kept"));
                    wasReviewed = !token.IsCancellationRequested;
                    return null;
                }
            }
            else if ((alreadyMarked || completeFrame || result.Review is not null
                || input.Extra?.Method == LayerAdditionalMethod.RetiredQwen) && draft is not null)
            {
                SetStatus(T("PreparingComparison"));
                var proofs = await Task.Run(() => (Before: LayerComparisonWindow.Proof(previous, previous.Layers, area, token),
                    After: LayerComparisonWindow.Proof(preview, preview.Layers, area, token)), token);
                token.ThrowIfCancellationRequested();
                if (!CanReview)
                {
                    _pendingReview = (state, result);
                    SetStatus(T("ReviewReady"));
                    return null;
                }
                var review = new LayerComparisonWindow(_owner, _l, previous, preview.Layers, area, proofs.Before, proofs.After,
                    summary, allowPartial: !completeFrame);
                if (!ReviewAccepted(review, token))
                {
                    SetStatus(T("Kept"));
                    wasReviewed = !token.IsCancellationRequested;
                    return null;
                }
                adopted = review.AcceptedPlan;
                area = review.AcceptedArea;
            }
            token.ThrowIfCancellationRequested();
            if (draft is not null && adopted is not null && previous.Commit(adopted, draft.Kind, draft.Target, area, draft.Depth, draft.Models))
            {
                if (input.Extra is not null)
                {
                    var added = draft.Changes.FirstOrDefault(change => change.Before is null && adopted.ContainsKey(change.Id));
                    _selected = input.Target ?? added?.Id ?? _selected;
                    if (input.Extra.Method is LayerAdditionalMethod.Matte or LayerAdditionalMethod.Portrait or LayerAdditionalMethod.EdgeDetails)
                        _view.SelectedIndex = 1;
                }
                Changed(preferVisibleLayer: true);
                SetStatus(completeFrame ? T("FrameCoverageReady")
                    : result.Skipped > 0 ? string.Format(T("AnalysisPartial"), result.Skipped) : T("AnalysisReady"));
            }
            else SetStatus(summary ?? T("NoDifference"));
            wasReviewed = true;
            return null;
        }
        finally
        {
            previous.DiscardPreviewMasks();
            if (wasReviewed)
            {
                ApplicationBackgroundOperations.Current?.Acknowledge(state.Id);
                _analysisWork.Forget(state.Id);
            }
        }
    }

    private static Rectangle TextAnalysisArea(LayerDocument document, Guid id)
    {
        var root = LayerDocument.TextAreaOf(document.Layers[id], document.Layers);
        var bounds = document.Aggregate(root.Id).Bounds(document.Source.Width);
        var margin = Math.Max(6, bounds.Height / 5);
        return Rectangle.Intersect(document.Source.Frame, Rectangle.Inflate(bounds, margin, margin));
    }

    private bool ReviewAccepted(Window review, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var close = token.Register(() => Dispatcher.BeginInvoke(() => { if (review.IsVisible) review.Close(); }));
        return review.ShowDialog() == true;
    }

    internal void ReviewWhenVisible()
    {
        if (_pendingReview is { } ready && _operation is null && !_confirming && CanReview)
            _ = RunAsync(token => ReviewAndRetryAsync(ready.State, ready.Result, token));
    }

    private Rectangle AnalysisArea(LayerDocument document, bool refine, out Guid? target)
    {
        target = null;
        if (refine && (_selected is not Guid id || _selectedMask?.Any != true || document.RefinableVersion(id) is null))
            return Rectangle.Empty;
        if (refine || _scope.SelectedIndex == 2)
        {
            if (_selected is not Guid selected || _selectedMask?.Any != true) return Rectangle.Empty;
            target = selected;
            if (document.Layers[selected].Parent is null) return document.Source.Frame;
            var box = _selectedMask.Bounds(document.Source.Width);
            var area = Rectangle.Inflate(box, Math.Max(5, box.Width / 4), Math.Max(5, box.Height / 4));
            return Rectangle.Intersect(area, document.Source.Frame);
        }
        return _scope.SelectedIndex == 1 ? _region ?? Rectangle.Empty : document.Source.Frame;
    }
}
