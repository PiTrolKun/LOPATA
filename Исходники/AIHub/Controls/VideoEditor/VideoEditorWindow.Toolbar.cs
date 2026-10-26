using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private Button? _playButton;
    private bool? _playIconState;
    private void Help(FrameworkElement control, string key)
    {
        control.ToolTip = VideoEditorCommands.Tip(key, T); AutomationProperties.SetName(control, T(key)); ToolTipService.SetShowOnDisabled(control, true);
        if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control))) AutomationProperties.SetAutomationId(control, "VideoEditor." + key);
    }
    private void ToggleStyle(CheckBox control, string key)
    {
        control.Content = T(key); control.Margin = new(4, 0, 7, 4); control.FontSize = 12;
        control.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; control.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center; Help(control, key);
    }
    private Border Group(params UIElement[] controls)
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in controls) panel.Children.Add(control);
        var border = new Border { Child = panel, BorderThickness = new(0, 0, 1, 0), Padding = new(0, 0, 6, 0), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    private TextBlock GroupName(string key) => new() { Text = T(key), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new(3, 0, 7, 4) };
    private WrapPanel BuildTimelineToolbar()
    {
        var toolbar = new WrapPanel();
        var edit = new WrapPanel(); edit.Children.Add(B("Undo", () => Undo(false))); edit.Children.Add(B("Redo", () => Undo(true))); edit.Children.Add(B("Split", SplitSelection));
        edit.Children.Add(B("Delete", DeleteClip)); edit.Children.Add(B("Copy", CopyClips)); edit.Children.Add(B("Paste", PasteClips)); edit.Children.Add(B("Duplicate", DuplicateClip)); edit.Children.Add(B("Unlink", UnlinkSelection));
        edit.Children.Add(B("CloseGaps", () => Edit(p => { var t = p.Tracks.FirstOrDefault(t => t.Id == _timeline.SelectedTrack); if (t is null || t.Locked) return; double at = 0; foreach (var c in t.Clips.OrderBy(c => c.Start).ToArray()) { VideoEditing.Move(p, c.Id, at); at += c.Duration; } })));
        var snap = new CheckBox { IsChecked = true }; ToggleStyle(snap, "Snap"); snap.Click += (_, _) => _timeline.Snap = snap.IsChecked == true; edit.Children.Add(snap); toolbar.Children.Add(Group(edit));
        var additions = new WrapPanel(); additions.Children.Add(B("Freeze", () => Edit(p => { var c = Selected(p); if (c?.AssetId is {} id && p.Asset(id).Kind == "video") { c.SourceIn += Math.Clamp(_position - c.Start, 0, c.Duration) * c.Speed; c.Freeze = true; } })));
        additions.Children.Add(B("TitleClip", () => Edit(p => { var t = new VideoTrack { Kind = "video", Name = T("TitleClip") }; t.Clips.Add(new() { Text = T("TitleText"), Start = _position }); p.Tracks.Add(t); })));
        additions.Children.Add(B("AddVideo", () => Edit(p => p.Tracks.Add(new() { Kind = "video", Name = T("VideoTrack") + " " + (p.Tracks.Count(t => t.Kind == "video") + 1) })))); toolbar.Children.Add(Group(additions));
        var audio = new WrapPanel(); audio.Children.Add(B("AddAudio", () => Edit(p => p.Tracks.Add(new() { Kind = "audio", Name = T("AudioTrack") + " " + (p.Tracks.Count(t => t.Kind == "audio") + 1) }))));
        audio.Children.Add(B("CreateBackgroundMusic", () => Safe(async () => await RunWorkAsync("music"))));
        var selection = new WrapPanel(); BuildSelectionControls(selection, audio); toolbar.Children.Add(Group(audio));
        var box = new CheckBox(); ToggleStyle(box, "BoxSelect"); box.Click += (_, _) => _timeline.BoxSelectionMode = _segments.BoxSelectionMode = box.IsChecked == true; selection.Children.Add(box); AutomationProperties.SetAutomationId(box, "VideoEditor.BoxSelect");
        ToggleStyle(_spacing, "SeparateSeries"); _spacing.Click += (_, _) => ToggleSpacing(); selection.Children.Add(_spacing); AutomationProperties.SetAutomationId(_spacing, "VideoEditor.SeparateSeries"); toolbar.Children.Add(Group(selection));
        var zoom = new System.Windows.Controls.Slider { Minimum = 5, Maximum = 250, Value = 55, Width = 100, Margin = new(4), VerticalAlignment = VerticalAlignment.Center }; Help(zoom, "Zoom"); AutomationProperties.SetAutomationId(zoom, "VideoEditor.Zoom");
        zoom.ValueChanged += (_, _) => { _timeline.Zoom = zoom.Value; _timeline.Refresh(); }; toolbar.Children.Add(Group(GroupName("Zoom"), zoom)); return toolbar;
    }
    private void UpdatePlayIcon()
    {
        if (_playButton is null || _playIconState == _playing) return;
        _playIconState = _playing; _playButton.Content = VideoEditorCommands.Icon(_playing ? "PauseGlyph" : "Play", _playButton);
    }
}
