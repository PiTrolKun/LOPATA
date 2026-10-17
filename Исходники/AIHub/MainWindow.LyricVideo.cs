using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.LyricVideo;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private readonly ClipWork _clipWork = new();
    private readonly List<LyricVideoWindow> _clipWindows = [];
    private void OpenLyricVideoUtility()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = L("LyricVideo.ChooseFile"),
            Filter = L("LyricVideo.AudioFilter"), CheckFileExists = true, Multiselect = false };
        if (Directory.Exists(_appSettings.MusicOutputFolder)) picker.InitialDirectory = _appSettings.MusicOutputFolder;
        if (picker.ShowDialog(this) == true) OpenLyricVideo(picker.FileName);
    }
    private void OpenMusicLyricVideo(MusicWorkspaceControl workspace)
    {
        var tracks = workspace.Tracks.SessionTracks.Where(t => !t.IsExample && File.Exists(t.Path)).ToArray();
        if (tracks.Length == 0) { MessageBox.Show(this, L("LyricVideo.NoTracks"), L("LyricVideo.Title")); return; }
        var chooser = new Window { Owner = this, Title = L("LyricVideo.ChooseTrack"), Width = 620, Height = 410,
            MinWidth = 420, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        MusicWishUi.PrepareWindow(chooser, L("LyricVideo.ChooseTrack"), "Music.Video.ChooseTrack", 620);
        chooser.Height = 410; chooser.MinHeight = 280;
        chooser.Resources.MergedDictionaries.Add(Resources);
        var panel = new DockPanel { Margin = new(18) }; chooser.Content = panel;
        var use = new Button { Content = L("LyricVideo.Continue"), Padding = new(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(use, Dock.Bottom); panel.Children.Add(use);
        var list = new ListBox { DisplayMemberPath = "Title", ItemsSource = tracks, SelectedIndex = 0 };
        panel.Children.Add(list);
        use.Click += (_, _) => { if (list.SelectedItem is not null) chooser.DialogResult = true; };
        if (chooser.ShowDialog() == true && list.SelectedItem is AIHub.Models.MusicTrack track)
        { workspace.Player.Pause(); OpenLyricVideo(track.Path!); }
    }
    private void OpenLyricVideo(string path)
    {
        path = Path.GetFullPath(path);
        var previous = _clipWindows.FirstOrDefault(w => w.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (previous is not null) { previous.Show(); previous.Activate(); return; }
        var window = new LyricVideoWindow(path, _clipWork, L, _localizationService.CurrentLanguageCode) { Owner = this };
        window.Resources.MergedDictionaries.Add(Resources);
        window.ViewFileRequested += result => _fileViewerService.Open(this, result, _appSettings.FileViewer, _isDarkTheme, _localizationService);
        window.Closed += (_, _) => _clipWindows.Remove(window);
        _clipWindows.Add(window); window.Show();
    }
    private void RegisterLyricVideoBackgroundOperation()
    {
        _backgroundOperations!.Register(ClipWork.Kind, async (state, token) => await _clipWork.ResumeAsync(state, token));
    }
}
