using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.IO;
using AIHub.Services.VideoPreparation;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

internal sealed partial class VideoH3WorkspaceControl
{
    private TextBlock _resultMode = new();
    private sealed record ResultItem(VideoH3Result Result, string Caption) { public override string ToString() => Caption; }
    private bool _filtering;
    private System.Windows.Controls.Button _previousResult = new(), _nextResult = new();
    private string? SelectedVideo => _timeline.SelectedItem is ResultItem item
        ? _project.Attachments.SingleOrDefault(a => a.Role == item.Result.VideoRole)?.Path : null;
    internal void ReloadResults()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ReloadResults); return; }
        _state.Runs = VideoShots.Copy(VideoH3WorkspaceState.Get(_project, _modelId)?.Runs ?? []); FilterResults();
    }
    private void FilterResults()
    {
        var selected = (_timeline.SelectedItem as ResultItem)?.Result.Id;
        var kind = S.RunMode == "review" ? "fragment" : "whole";
        var items = _state.Runs.SelectMany(r => r.Results).Where(r => r.Kind == kind)
            .Select((r, i) => new ResultItem(r, (i + 1) + ". " + T(kind == "fragment" ? "H3Fragment" : "H3Whole") + " · " + r.Seconds.ToString("0.##") + " " + T("H3SecondsUnit"))).ToArray();
        _filtering = true;
        _timeline.ItemsSource = items;
        _timeline.SelectedItem = items.FirstOrDefault(i => i.Result.Id == selected) ?? items.LastOrDefault();
        _filtering = false;
        _resultMode.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _previousResult.IsEnabled = _timeline.SelectedIndex > 0;
        _nextResult.IsEnabled = _timeline.SelectedIndex >= 0 && _timeline.SelectedIndex < items.Length - 1;
        SelectResult(); RefreshResultActions();
    }
    private void MoveResult(int offset)
    {
        if (_timeline.Items.Count > 0) _timeline.SelectedIndex = Math.Clamp(_timeline.SelectedIndex + offset, 0, _timeline.Items.Count - 1);
    }

    private void BuildTimeline(Grid root)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var back = _previousResult = ActionButton("H3PreviousResult", "PreviousResult", () => MoveResult(-1)); back.Content = "◀"; back.IsEnabled = false;
        DockPanel.SetDock(back, Dock.Left); row.Children.Add(back);
        var next = _nextResult = ActionButton("H3NextResult", "NextResult", () => MoveResult(1)); next.Content = "▶"; next.IsEnabled = false;
        DockPanel.SetDock(next, Dock.Right); row.Children.Add(next);
        var host = new Grid();
        // Only explicit inference receipts enter the strip; preparation cannot create an item.
        _timeline = new ListBox { BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        AutomationProperties.SetAutomationId(_timeline, "VideoH3.Workspace.Timeline");
        _timeline.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); _timeline.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); _timeline.SetResourceReference(BorderBrushProperty, "LineBrush");
        var factory = new FrameworkElementFactory(typeof(VirtualizingStackPanel)); factory.SetValue(VirtualizingStackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        _timeline.ItemsPanel = new ItemsPanelTemplate(factory); VirtualizingPanel.SetIsVirtualizing(_timeline, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_timeline, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(_timeline, ScrollBarVisibility.Disabled);
        _timeline.SelectionChanged += (_, _) =>
        {
            if (_filtering) return; SelectResult(); RefreshResultActions();
            _previousResult.IsEnabled = _timeline.SelectedIndex > 0;
            _nextResult.IsEnabled = _timeline.SelectedIndex < _timeline.Items.Count - 1;
            if (_timeline.SelectedItem is not null) _timeline.ScrollIntoView(_timeline.SelectedItem);
        };
        host.Children.Add(_timeline);
        _resultMode = Label(""); _resultMode.Margin = new Thickness(14); _resultMode.VerticalAlignment = VerticalAlignment.Center;
        _resultMode.IsHitTestVisible = false; AutomationProperties.SetAutomationId(_resultMode, "VideoH3.Workspace.ResultMode");
        host.Children.Add(_resultMode); row.Children.Add(host); Grid.SetRow(row, 2); root.Children.Add(row);
    }
}
