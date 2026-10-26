using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Orientation = System.Windows.Controls.Orientation;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using TabControl = System.Windows.Controls.TabControl;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private void BuildLayout()
    {
        var container = new Grid(); Content = container;
        var root = _workspace = new Grid { Margin = new(14) }; container.Children.Add(root);
        _expandedHost = new Grid { Visibility = Visibility.Collapsed }; container.Children.Add(_expandedHost);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(3, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1.6, GridUnitType.Star), MinHeight = 300 }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new WrapPanel(); root.Children.Add(top);
        top.Children.Add(B("Import", () => Safe(ImportAsync))); top.Children.Add(B("OpenProject", () => Safe(OpenProjectAsync))); top.Children.Add(B("Save", () => Save(false))); top.Children.Add(B("SaveAs", () => Save(true))); top.Children.Add(B("Recovery", () => Safe(RecoverAsync)));
        top.Children.Add(B("Canvas", CanvasSettings)); top.Children.Add(B("Export", () => Safe(ExportAsync)));
        var upper = new Grid { Margin = new(0, 5, 0, 10) }; Grid.SetRow(upper, 1); root.Children.Add(upper);
        upper.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) }); upper.ColumnDefinitions.Add(new() { Width = new(7) }); upper.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star), MinWidth = 330 });
        var video = _previewDock = new DockPanel { Margin = new(0, 0, 8, 0) }; upper.Children.Add(video);
        var transportArea = new StackPanel { Margin = new(0, 6, 0, 0) }; DockPanel.SetDock(transportArea, Dock.Bottom); video.Children.Add(transportArea);
        var transport = new WrapPanel(); transportArea.Children.Add(transport);
        transport.Children.Add(B("Beginning", () => Safe(async () => await SeekAsync(0)))); transport.Children.Add(B("PreviousFrame", () => Safe(async () => await SeekAsync(_position - 1 / Project.Fps)))); transport.Children.Add(B("Play", () => Safe(ToggleAsync)));
        transport.Children.Add(B("NextFrame", () => Safe(async () => await SeekAsync(_position + 1 / Project.Fps)))); transport.Children.Add(_clock);
        var view = new WrapPanel(); transportArea.Children.Add(view);
        ToggleStyle(_subtitles, "PreviewSubtitles"); Help(_subtitleTracks, "SubtitleTrackChoice"); view.Children.Add(Group(_subtitles, _subtitleTracks));
        _subtitleTracks.SelectionChanged += (_, _) => { if (_changingTrack) return; _subtitleFilter = _subtitleTracks.SelectedValue as string; Safe(RefreshPreviewAsync); };
        ToggleStyle(_framing, "FrameMode");
        AutomationProperties.SetAutomationId(_framing, "VideoEditor.FrameMode");
        view.Children.Add(Group(_framing, B("FrameFill", () => FrameFit(true)), B("FrameFit", () => FrameFit(false)), B("Transform", TransformClip)));
        var frame = _previewFrame = new Border { Background = Brushes.Black, Child = _preview, BorderThickness = new(1) }; frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); video.Children.Add(frame);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); upper.Children.Add(splitter);
        var tabs = new TabControl(); Grid.SetColumn(tabs, 2); upper.Children.Add(tabs);
        var narration = new Grid { Margin = new(10) }; narration.RowDefinitions.Add(new() { Height = GridLength.Auto }); narration.RowDefinitions.Add(new() { Height = GridLength.Auto }); narration.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 }); narration.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var tools = new WrapPanel(); narration.Children.Add(tools);
        tools.Children.Add(B("Detect", () => Safe(async () => await RunWorkAsync("detect")))); tools.Children.Add(B("AddFragment", AddFragment)); tools.Children.Add(B("SplitFragment", SplitFragment)); tools.Children.Add(B("MergeFragment", MergeFragment));
        ToggleStyle(_follow, "FollowText"); tools.Children.Add(Group(_follow));
        Grid.SetRow(_segments, 1); narration.Children.Add(_segments); Help(_segments, "Segments"); AutomationProperties.SetAutomationId(_segments, "VideoEditor.Segments");
        Grid.SetRow(_script, 2); narration.Children.Add(_script); Help(_script, "Script"); AutomationProperties.SetAutomationId(_script, "VideoEditor.Script");
        var speech = new StackPanel { Margin = new(0, 8, 0, 0) }; Grid.SetRow(speech, 3); narration.Children.Add(speech);
        speech.Children.Add(new TextBlock { Text = T("Brief") }); speech.Children.Add(_brief); Help(_brief, "Brief");
        var ai = new WrapPanel { Margin = new(0, 6, 0, 0) }; speech.Children.Add(ai);
        ai.Children.Add(Group(GroupName("TextTools"), B("AnalyzeSelected", () => Safe(async () => await RunWorkAsync("analyze", selected: true))), B("AnalyzeAll", () => Safe(async () => await RunWorkAsync("analyze")))));
        ai.Children.Add(Group(GroupName("SpeechTools"), B("VoiceSettings", VoiceSettings), B("SpeakSelected", () => Safe(async () => await RunWorkAsync("speech", selected: true))), B("SpeakFragments", () => Safe(async () => await RunWorkAsync("speech"))), B("SpeakWhole", () => Safe(async () => await RunWorkAsync("speech", whole: true)))));
        AutomationProperties.SetAutomationId(_voiceSummary, "VideoEditor.SelectedNarrator");
        ai.Children.Add(_voiceSummary); UpdateVoiceSummary();
        tabs.Items.Add(new TabItem { Header = T("Narration"), Content = new ScrollViewer { Content = narration, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = T("Properties"), Content = new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var materials = new DockPanel { Margin = new(8) }; var add = new WrapPanel(); DockPanel.SetDock(add, Dock.Top); materials.Children.Add(add);
        add.Children.Add(B("InsertMaterial", InsertMaterial)); add.Children.Add(B("GenerateSvg", () => Safe(GenerateSvgAsync))); add.Children.Add(B("EditSvg", () => { if (_materials.SelectedItem is VideoAsset { Kind: "svg" } asset) EditSvgRequested?.Invoke(asset.Path); })); add.Children.Add(B("ImportSrt", ImportSrt)); add.Children.Add(B("Relink", () => Safe(RelinkAsync)));
        _materials.MouseDoubleClick += (_, _) => InsertMaterial(); Help(_materials, "Materials"); materials.Children.Add(_materials); tabs.Items.Add(new TabItem { Header = T("Materials"), Content = materials });
        var toolbar = BuildTimelineToolbar(); Grid.SetRow(toolbar, 2); root.Children.Add(toolbar);
        var timelineScroll = new ScrollViewer { Content = _timeline, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(timelineScroll, 3); root.Children.Add(timelineScroll);
        _timeline.AttachViewport(timelineScroll);
        var bottom = new StackPanel { Margin = new(0, 5, 0, 0) }; Grid.SetRow(bottom, 4); root.Children.Add(bottom); bottom.Children.Add(_progress);
        var operation = new WrapPanel(); bottom.Children.Add(operation); operation.Children.Add(B("Pause", () => Safe(async () => { if (ApplicationBackgroundOperations.Current is {} c && c.State?.Kind == VideoWork.Kind) await c.PauseAsync(); })));
        operation.Children.Add(B("Resume", () => Safe(async () => { if (ApplicationBackgroundOperations.Current is {} c && c.State?.Kind == VideoWork.Kind) await c.ResumeAsync(_lifetime.Token); })));
        operation.Children.Add(B("Cancel", () => Safe(async () => { _localOperationCancel?.Cancel(); if (ApplicationBackgroundOperations.Current is {} c && c.State?.Kind == VideoWork.Kind) { await c.PauseAsync(); c.DiscardPending(); } }))); bottom.Children.Add(_status);
    }
}
