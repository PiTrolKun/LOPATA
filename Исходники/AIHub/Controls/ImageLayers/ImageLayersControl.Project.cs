using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.ImageLayers;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private Task OpenAsync() => RunAsync(async token =>
    {
        var dialog = new OpenFileDialog { Filter = T("OpenFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(_owner) != true || !await ConfirmLeaveAsync()) return;
        var path = dialog.FileName;
        var projectFile = Path.GetExtension(path).Equals(LayerProjectArchive.Extension, StringComparison.OrdinalIgnoreCase);
        SetStatus(T("Opening"));
        var loaded = await Task.Run(async () =>
        {
            if (projectFile) return LayerProjectArchive.Open(path);
            await using var file = File.OpenRead(path);
            if (file.Length is 0 or > LayerSource.MaxInputBytes) throw new InvalidDataException("ImageLayers.InvalidImage");
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, token);
            return new LayerDocument(LayerSource.Load(Path.GetFileName(path), bytes));
        }, token);
        token.ThrowIfCancellationRequested();
        SetDocument(loaded, projectFile ? path : null);
    });

    private void SetDocument(LayerDocument loaded, string? projectPath, Guid? selected = null)
    {
        _document = loaded;
        _path = projectPath;
        _selected = selected;
        _region = null;
        _selectedMask = null;
        _dirty = projectPath is null;
        _scope.SelectedIndex = _view.SelectedIndex = 0;
        _canvas.Tool = LayerCanvasTool.Hand;
        _canvas.ShowSource(loaded.Source);
        UpdateTitle();
        RebuildLayers(restorePointSpacing: true, preferVisibleLayer: true);
        SetStatus(string.Format(T("ImageSize"), loaded.Source.Width, loaded.Source.Height));
    }

    private async Task<bool> SaveAsync(bool saveAs, CancellationToken token)
    {
        var current = _document;
        if (current is null) return true;
        var path = _path;
        if (path is null || saveAs)
        {
            var dialog = new SaveFileDialog
            {
                Filter = T("ProjectFilter"), AddExtension = true, DefaultExt = LayerProjectArchive.Extension,
                FileName = Path.GetFileNameWithoutExtension(path ?? current.Source.Name) + LayerProjectArchive.Extension,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(_owner) != true) return false;
            path = dialog.FileName;
            if (!Path.GetExtension(path).Equals(LayerProjectArchive.Extension, StringComparison.OrdinalIgnoreCase))
            {
                // Ask through the Windows save dialog for the actual project filename.
                dialog.FileName = Path.GetFileNameWithoutExtension(path) + LayerProjectArchive.Extension;
                if (dialog.ShowDialog(_owner) != true
                    || !Path.GetExtension(dialog.FileName).Equals(LayerProjectArchive.Extension, StringComparison.OrdinalIgnoreCase))
                    return false;
                path = dialog.FileName;
            }
        }
        SetStatus(T("Saving"));
        try { await Task.Run(() => LayerProjectArchive.Save(path, current), token); }
        catch (IOException error) { throw new IOException("ImageLayers.SaveFailed", error); }
        _path = path;
        _dirty = false;
        UpdateTitle();
        SetStatus(string.Format(T("Saved"), Path.GetFileName(path)));
        return true;
    }

    private void RebuildLayers(bool restorePointSpacing, bool preferVisibleLayer)
    {
        var keep = _selected;
        _rebuildingLayers = true;
        try
        {
            _tree.Items.Clear();
            if (_document is { } document)
            {
                var visible = document.LayersWithCoverage();
                if (preferVisibleLayer && (keep is not Guid current || !visible.Contains(current)))
                    keep = VisibleReplacement(document, visible);
                var ordered = document.Layers.Values.OrderBy(layer => !visible.Contains(layer.Id))
                    .ThenBy(layer => layer.Text?.Order ?? 0).ToArray();
                var nodes = ordered.ToDictionary(layer => layer.Id, layer => new TreeViewItem
                {
                    Header = visible.Contains(layer.Id) ? layer.Name : string.Format(T("EmptyLayer"), layer.Name),
                    Tag = layer.Id,
                    IsExpanded = layer.Text is null || layer.Id == keep || IsSelectedAncestor(keep, layer.Id, document.Layers)
                });
                foreach (var layer in ordered)
                    if (layer.Parent is Guid parent) nodes[parent].Items.Add(nodes[layer.Id]);
                    else _tree.Items.Add(nodes[layer.Id]);

                var candidate = keep is Guid key && nodes.TryGetValue(key, out var prior) ? prior : null;
                if (candidate is null && !preferVisibleLayer)
                    candidate = _tree.Items.OfType<TreeViewItem>().FirstOrDefault();
                if (candidate is not null) candidate.IsSelected = true;
            }
        }
        finally { _rebuildingLayers = false; }
        SelectLayer(restorePointSpacing);
    }

    private Guid? VisibleReplacement(LayerDocument document, HashSet<Guid> visible)
    {
        if (_selectedMask is { Any: true } previous)
        {
            var preferred = _selected is Guid id && document.Layers.TryGetValue(id, out var retired)
                && (retired.Text is not null || LayerRecognizedText.IsTextLabel(retired.Name));
            var match = document.Layers.Values.Where(layer => visible.Contains(layer.Id)
                    && layer.Text?.Kind is not LayerTextKind.Glyph)
                .Select(layer =>
                {
                    var mask = document.Aggregate(layer.Id);
                    var common = mask.Intersect(previous).Count;
                    return (layer.Id, Text: layer.Text is not null, Score: common / (double)(mask.Count + previous.Count - common));
                })
                .Where(layer => layer.Score > 0).OrderByDescending(layer => preferred && layer.Text)
                .ThenByDescending(layer => layer.Score).FirstOrDefault();
            if (match.Id != Guid.Empty) return match.Id;
        }
        return document.Layers.Values.FirstOrDefault(layer => layer.Parent is null && visible.Contains(layer.Id))?.Id;
    }

    private static bool IsSelectedAncestor(Guid? selected, Guid candidate, IReadOnlyDictionary<Guid, ImageLayer> layers)
    {
        while (selected is Guid id && layers.TryGetValue(id, out var layer))
        {
            if (layer.Parent == candidate) return true;
            selected = layer.Parent;
        }
        return false;
    }

    private void SelectLayer(bool restorePointSpacing)
    {
        _selected = (_tree.SelectedItem as TreeViewItem)?.Tag as Guid?;
        _selectedMask = _selected is Guid id ? _document?.Aggregate(id) : null;
        if (!restorePointSpacing && _selected.HasValue && _selectedMask?.Any != true)
            SetStatus(T("EmptyLayerSelected"));
        if (restorePointSpacing && _selected is Guid selectedLayer && _document?.Layers[selectedLayer].Points is { } layout
            && _selectedMask?.Key == layout.Silhouette)
            _spacing.SelectedIndex = Array.IndexOf(LayerPointLayout.Steps, layout.Step);
        _historyTitle.Text = _selected is Guid key ? string.Format(T("NamedHistory"), _document!.Layers[key].Name) : T("History");
        var rows = new List<HistoryRow>();
        if (_selected is Guid selected && _document is not null)
        {
            var sequence = _document.LayerHistory(selected).ToArray();
            rows.AddRange(sequence.Select((step, index) => new HistoryRow(
                step.AtUtc.ToLocalTime().ToString("g") + "  " + HistoryName(step.Kind), step.Id, index == sequence.Length - 1)));
            rows.Reverse();
            if (sequence.Length > 0) rows.Add(new(T("BeforeCreation"), null, false));
        }
        _history.ItemsSource = rows;
        if (rows.Count > 0) _history.SelectedIndex = 0;
        RefreshTools();
    }

    private string HistoryName(string kind) => T(kind switch
    {
        LayerDocument.AnalyzeKind => "HistoryAnalyze", LayerDocument.RefineKind => "HistoryRefine",
        LayerDocument.PointsKind => "HistoryPoints", LayerDocument.PencilKind => "HistoryPencil",
        LayerDocument.AddPointKind => "HistoryAddPoint", LayerDocument.RemovePointsKind => "HistoryRemovePoints",
        LayerDocument.IncludeRegionKind => "HistoryIncludeRegion",
        LayerDocument.ExcludeRegionKind => "HistoryExcludeRegion",
        LayerDocument.RemoveRegionContoursKind => "HistoryRemoveRegionContours",
        LayerDocument.ManualLayerKind => "HistoryManualLayer",
        LayerAdditionalMethods.Matte => "HistoryMatte", LayerAdditionalMethods.Portrait => "HistoryPortrait",
        LayerAdditionalMethods.EdgeDetails => "HistoryEdgeDetails",
        LayerAdditionalMethods.Text => "HistoryText",
        LayerAdditionalMethods.SceneReview => "HistorySceneReview",
        LayerAdditionalMethods.CompleteFrame => "HistoryCompleteFrame",
        LayerAdditionalMethods.RetiredQwen => "HistoryQwen",
        LayerDocument.LassoMergeKind => "HistoryLassoMerge",
        LayerAdditionalMethods.Watershed => "HistoryMarkers", LayerAdditionalMethods.Describe => "HistoryDescribe",
        _ => "HistoryRestore"
    });

    private Task EditAsync(LayerContourEdit edit) => RunAsync(async token =>
    {
        if (_document is not { } document || _selected is not Guid id) return;
        try
        {
            var plan = await Task.Run(() =>
            {
                var next = document.PlanBoundary(id, edit.Mask, allowEmpty: edit.Kind == LayerDocument.RemovePointsKind);
                next[id] = next[id] with { Points = edit.Points };
                return next;
            }, token);
            token.ThrowIfCancellationRequested();
            if (document.Commit(plan, edit.Kind, id))
            {
                Changed();
                _canvas.SelectPoints(edit.Selection ?? []);
                SetStatus(T("ContourUpdated"));
            }
        }
        finally { document.DiscardPreviewMasks(); }
    });

    private Task RestoreAsync() => RunAsync(async token =>
    {
        if (_document is not { } document || _selected is not Guid selected
            || _history.SelectedItem is not HistoryRow { Current: false } row) return;
        try
        {
            var plan = await Task.Run(() => document.PlanRestore(selected, row.Version), token);
            token.ThrowIfCancellationRequested();
            if (document.Commit(plan, LayerDocument.RestoreKind, selected, restored: row.Version))
            {
                Changed(restorePointSpacing: true);
                SetStatus(T("Restored"));
            }
        }
        finally { document.DiscardPreviewMasks(); }
    });
}
