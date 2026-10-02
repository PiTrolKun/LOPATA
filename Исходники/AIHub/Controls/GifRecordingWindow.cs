using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AIHub.Services;
using Window = System.Windows.Window;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AIHub.Controls;

public sealed class GifRecordingWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly Func<string, string> _text;
    private bool _finished;
    public Exception? Failure { get; private set; }
    public GifRecordingWindow(Window theme, Func<string, string> text, Action stop, Action cancel, bool video = false, string audioMode = "off")
    {
        _text = text; Title = text(video ? "Capture.Mode.Video" : "Capture.Mode.Gif"); Width = 400; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        Resources.MergedDictionaries.Add(theme.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); FontFamily = theme.FontFamily;
        var panel = new StackPanel { Margin = new(16) }; panel.Children.Add(_status);
        if (video) panel.Children.Add(CaptureUi.Text(text("Capture.Audio") + ": " + text("Capture.Audio." + audioMode)));
        panel.Children.Add(CaptureUi.Button(text("Capture.Stop"), stop));
        var cancelButton = CaptureUi.Button(text("Capture.GifCancelAssembly"), cancel); cancelButton.Visibility = Visibility.Collapsed;
        panel.Children.Add(cancelButton); Content = panel;
        SetAssemblyState = assembling => cancelButton.Visibility = assembling ? Visibility.Visible : Visibility.Collapsed;
        Closing += (_, args) => { if (!_finished) { args.Cancel = true; stop(); } };
        SourceInitialized += (_, _) =>
        {
            // Failure to exclude the indicator is not silently accepted.
            if (!SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11)) Failure = new System.ComponentModel.Win32Exception();
        };
    }
    private Action<bool> SetAssemblyState { get; }
    public void Update(GifRecordingProgress progress)
    {
        _status.Text = progress.Assembling
            ? string.Format(_text("Capture.GifAssembling"), progress.Percent)
            : string.Format(_text("Capture.GifRecording"), progress.Elapsed.ToString(@"mm\:ss"), progress.Width, progress.Height);
        if (progress.ReducedDetail) _status.Text += "\n" + _text("Capture.GifReduced");
        if (progress.MissedFrames > 0) _status.Text += "\n" + string.Format(_text("Capture.GifMissed"), progress.MissedFrames);
        SetAssemblyState(progress.Assembling);
    }
    public void Finish() { _finished = true; Close(); }
    public void UpdateVideo(VideoRecordingProgress progress)
    {
        _status.Text = progress.Assembling ? string.Format(_text("Capture.VideoAssembling"), progress.Percent)
            : string.Format(_text("Capture.VideoRecording"), progress.Elapsed.ToString(@"hh\:mm\:ss"), progress.Width, progress.Height);
        if (progress.ReducedDetail) _status.Text += "\n" + _text("Capture.GifReduced");
        if (progress.MissedFrames > 0) _status.Text += "\n" + string.Format(_text("Capture.GifMissed"), progress.MissedFrames);
        SetAssemblyState(progress.Assembling);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
