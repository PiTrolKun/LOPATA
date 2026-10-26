using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private readonly CheckBox _linkedSelection = new() { IsChecked = true, Margin = new(7) };
    private readonly CheckBox _seriesSelection = new() { Margin = new(7) };
    private readonly CheckBox _stretchAudio = new() { Margin = new(7) };
    private void BuildSelectionControls(WrapPanel toolbar, WrapPanel audio)
    {
        ToggleStyle(_linkedSelection, "LinkedSelection");
        _linkedSelection.Click += (_, _) => {
            var enabled = _linkedSelection.IsChecked == true;
            Edit(p => { p.LinkedSelectionEnabled = enabled; if (enabled) p.SelectBySeries = false; });
        };
        toolbar.Children.Add(_linkedSelection); AutomationProperties.SetAutomationId(_linkedSelection, "VideoEditor.LinkedSelection");
        ToggleStyle(_seriesSelection, "SeriesSelection");
        _seriesSelection.Click += (_, _) => { var enabled = _seriesSelection.IsChecked == true; Edit(p => { p.SelectBySeries = enabled; if (enabled) p.LinkedSelectionEnabled = false; }); };
        var menu = new ContextMenu();
        foreach (var layer in new[] { VideoSeriesLayers.Video, VideoSeriesLayers.OriginalAudio, VideoSeriesLayers.Narration, VideoSeriesLayers.Subtitles, VideoSeriesLayers.OtherAudio }) {
            var item = new MenuItem { Header = T("Series" + layer), ToolTip = string.Format(T("SeriesLayerTip"), T("Series" + layer)), IsCheckable = true, StaysOpenOnClick = true };
            AutomationProperties.SetAutomationId(item, "VideoEditor.SeriesLayer." + layer);
            menu.Opened += (_, _) => item.IsChecked = Project.SeriesLayers.HasFlag(layer);
            item.Click += (_, _) => { var enabled = item.IsChecked; Edit(p => p.SeriesLayers = enabled ? p.SeriesLayers | layer : p.SeriesLayers & ~layer); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = T("SeriesSharedVoiceHint"), IsEnabled = false });
        _seriesSelection.ContextMenu = menu; toolbar.Children.Add(_seriesSelection); AutomationProperties.SetAutomationId(_seriesSelection, "VideoEditor.SeriesSelection");
        ToggleStyle(_stretchAudio, "StretchAudio");
        _stretchAudio.Click += (_, _) => { var enabled = _stretchAudio.IsChecked == true; Edit(p => p.StretchAudio = enabled); };
        audio.Children.Add(_stretchAudio); AutomationProperties.SetAutomationId(_stretchAudio, "VideoEditor.StretchAudio");
        RefreshSelectionControls();
    }
    private void RefreshSelectionControls()
    {
        if (_spacingSession is {} session && !session.BelongsTo(_editing)) CommitSpacing();
        _seriesSelection.IsChecked = Project.SelectBySeries; _stretchAudio.IsChecked = Project.StretchAudio;
        _timeline.SynchronizeSelection = !Project.SelectBySeries && Project.LinkedSelectionEnabled;
        _linkedSelection.IsChecked = _timeline.SynchronizeSelection;
    }
}
