using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

internal sealed partial class VideoVaceWorkspaceControl
{
    private sealed record ResultItem(VideoVaceResult Result, VideoVaceRun Run, string Caption) { public override string ToString() => Caption; }
    private ListBox _timeline = new();
    private TextBlock _resultMode = new();
    private System.Windows.Controls.Button _previousResult = new(), _nextResult = new();
    private bool _filtering;
    private ResultItem? SelectedResult => _timeline.SelectedItem as ResultItem;
    private string? SelectedVideo => SelectedResult is { } item
        ? _project.Attachments.SingleOrDefault(a => a.Role == item.Result.VideoRole)?.Path : null;
    internal void ReloadResults()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ReloadResults); return; }
        _state.Runs = VideoShots.Copy(_project.Generator?.Vace?.Runs ?? []);
        var selected = SelectedResult?.Result.Id ?? _state.SelectedResultId;
        var items = _state.Runs.Where(run => run.Complete).SelectMany(run => run.Results.Select(result => (run, result)))
            .Select((pair, index) => new ResultItem(pair.result, pair.run, (index + 1) + ". " + T("WholeVideo") + " · " +
                pair.result.Seconds.ToString("0.##") + " " + T("SecondsUnit"))).ToArray();
        _filtering = true; _timeline.ItemsSource = items;
        _timeline.SelectedItem = items.FirstOrDefault(item => item.Result.Id == selected) ?? items.LastOrDefault();
        _filtering = false; _resultMode.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedResult is { } current) _state.SelectedResultId = current.Result.Id;
        SelectResult(); RefreshResultActions(); RefreshResultInfo();
    }
    private void RefreshResultActions()
    {
        var valid = SelectedVideo is { } path && File.Exists(path);
        _exportVideo.IsEnabled = valid; _sendEditor.IsEnabled = valid && SendEditorRequested is not null;
        _previousResult.IsEnabled = _timeline.SelectedIndex > 0;
        _nextResult.IsEnabled = _timeline.SelectedIndex >= 0 && _timeline.SelectedIndex < _timeline.Items.Count - 1;
    }
    private void RefreshResultInfo() => _resultInfo.Text = SelectedResult is { } selected
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, T("ResultInfo"), selected.Result.Seconds.ToString("0.##"), Duration(selected.Run.GenerationSeconds))
        : T("NoResult");
    private void MoveResult(int offset)
    { if (!IsBusy && _timeline.Items.Count > 0) _timeline.SelectedIndex = Math.Clamp(_timeline.SelectedIndex + offset, 0, _timeline.Items.Count - 1); }
    private void BuildTimeline(Grid root)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _previousResult = ActionButton("PreviousResult", "PreviousResult", () => MoveResult(-1)); _previousResult.Content = "◀";
        DockPanel.SetDock(_previousResult, Dock.Left); row.Children.Add(_previousResult);
        _nextResult = ActionButton("NextResult", "NextResult", () => MoveResult(1)); _nextResult.Content = "▶";
        DockPanel.SetDock(_nextResult, Dock.Right); row.Children.Add(_nextResult);
        var host = new Grid(); _timeline = new ListBox { BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        AutomationProperties.SetAutomationId(_timeline, "VideoVace.Workspace.Timeline");
        _timeline.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); _timeline.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _timeline.SetResourceReference(BorderBrushProperty, "LineBrush");
        var factory = new FrameworkElementFactory(typeof(VirtualizingStackPanel)); factory.SetValue(VirtualizingStackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        _timeline.ItemsPanel = new ItemsPanelTemplate(factory); VirtualizingPanel.SetIsVirtualizing(_timeline, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_timeline, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(_timeline, ScrollBarVisibility.Disabled);
        _timeline.SelectionChanged += (_, _) =>
        {
            if (_filtering) return;
            if (SelectedResult is { } item) _state.SelectedResultId = item.Result.Id;
            SelectResult(); RefreshResultActions(); RefreshResultInfo();
            if (!_building && !IsBusy) TryCommit();
            if (_timeline.SelectedItem is not null) _timeline.ScrollIntoView(_timeline.SelectedItem);
        };
        host.Children.Add(_timeline); _resultMode = Label(T("EmptyWholeVideo")); _resultMode.Margin = new Thickness(14);
        _resultMode.VerticalAlignment = VerticalAlignment.Center; _resultMode.IsHitTestVisible = false;
        AutomationProperties.SetAutomationId(_resultMode, "VideoVace.Workspace.ResultMode"); host.Children.Add(_resultMode);
        row.Children.Add(host); Grid.SetRow(row, 2); root.Children.Add(row);
    }
}
