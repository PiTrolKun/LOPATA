using System.Windows;
using AIHub.Services.ImageLayers;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private Task RunSceneAsync(LayerAdditionalMethod operation) => RunAsync(async token =>
    {
        if (_document is not { } document) return;
        var review = operation == LayerAdditionalMethod.SceneReview;
        var hasLayers = document.Layers.Values.Any(layer => layer.Mask is not null);
        if (review && !hasLayers) return;
        var depth = review ? LayerDepth.Objects : (LayerDepth)Math.Max(0, _depth.SelectedIndex);
        var method = LayerAnalysisPreparation.ChooseMethod(false);
        await StartAnalysisAsync(document, new(document.Id, document.History.LastOrDefault()?.Id, document.Source.Frame,
            null, depth, false, _russian(), false, method,
            PortraitParts: !hasLayers && depth == LayerDepth.Parts && method == LayerMaskMethod.Sam2,
            Extra: new(operation), RecognizeText: !hasLayers && depth != LayerDepth.Objects), token);
    }, analysis: true);

    private void ShowEditorPlaceholder() =>
        MessageBox.Show(_owner, T("EditorPlaceholder"), T("SendToEditor"), MessageBoxButton.OK, MessageBoxImage.Information);

    private string FormatSceneReview(LayerSceneReview report)
    {
        var notes = new List<string>();
        Append("ReviewMissing", report.Missing);
        Append("ReviewSplit", report.Split);
        Append("ReviewMerged", report.Merged);
        Append("ReviewBoundary", report.Boundary);
        return notes.Count == 0 ? T("ReviewNoDifferences") : string.Join(Environment.NewLine, notes);

        void Append(string key, string[] names)
        {
            if (names.Length > 0)
                notes.Add(string.Format(T(key), names.Length, string.Join(", ", names.Take(3))));
        }
    }
}
