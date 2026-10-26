using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private readonly ComboBox _subtitleTracks = new() { Width = 170, DisplayMemberPath = "Value", SelectedValuePath = "Key", Margin = new(5) };
    private bool _changingTrack;
    private string? _subtitleFilter;
    private static string TrackKey(NarrationSegment n) => n.SubtitleTrackId + "|" + n.Language;
    private (bool Continue, MediaStreamChoice[] Subtitles) ChooseStreams(VideoAsset asset)
    {
        if (asset.AudioStreams.Count < 2 && asset.SubtitleStreams.Count == 0) return (true, []);
        var window = new Window { Owner = this, Title = T("ChooseStreams"), Width = 520, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Resources.MergedDictionaries.Add(Resources); window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var outer = new DockPanel { Margin = new(18) }; window.Content = outer; var accept = B("Apply", () => window.DialogResult = true); DockPanel.SetDock(accept, Dock.Bottom); outer.Children.Add(accept);
        var panel = new StackPanel(); outer.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = T("AudioStream"), Margin = new(0, 8, 0, 4) });
        var audio = new ComboBox { ItemsSource = asset.AudioStreams, DisplayMemberPath = "Name", SelectedItem = asset.AudioStreams.FirstOrDefault(s => s.Index == asset.AudioStream), MinHeight = 30 }; panel.Children.Add(audio);
        panel.Children.Add(new TextBlock { Text = T("SubtitleStreams"), Margin = new(0, 12, 0, 4) }); var checks = new List<(CheckBox Box, MediaStreamChoice Stream)>();
        foreach (var s in asset.SubtitleStreams) { var box = new CheckBox { Content = s.Name + " · " + s.Codec, Margin = new(0, 5, 0, 5), IsChecked = false, IsEnabled = s.Codec is "subrip" or "mov_text" or "webvtt" or "ass" or "ssa" }; panel.Children.Add(box); checks.Add((box, s)); }
        if (window.ShowDialog() != true) return (false, []);
        if (audio.SelectedItem is MediaStreamChoice chosen) asset.AudioStream = chosen.Index;
        return (true, checks.Where(c => c.Box.IsChecked == true).Select(c => c.Stream).ToArray());
    }
    private async Task<List<NarrationSegment>> ImportSubtitlesAsync(VideoAsset asset, MediaStreamChoice[] streams, double position)
    {
        var result = new List<NarrationSegment>(); var directory = VideoPreviewAssets.DirectoryFor(Project); Directory.CreateDirectory(directory);
        foreach (var stream in streams)
        {
            var path = Path.Combine(directory, asset.Id + "-sub-" + stream.Index + ".srt");
            await VideoRuntime.RunAsync(false, ["-v", "error", "-nostdin", "-n", "-i", asset.Path, "-map", "0:" + stream.Index, "-c:s", "subrip", path], MediaToken);
            var cues = VideoSubtitles.Parse(await File.ReadAllTextAsync(path, MediaToken), stream.Language);
            foreach (var n in cues) { n.Start += position; n.End += position; n.SubtitleTrackId = asset.Id + "-" + stream.Index; n.SubtitleTitle = stream.Name; n.SubtitleDefault = stream.Default; n.SubtitleForced = stream.Forced; }
            result.AddRange(cues);
        }
        return result;
    }
}
