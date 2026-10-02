using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>Shared editors over the same settings; layouts belong to each presentation.</summary>
internal sealed class CaptureSettingsEditor(ScreenCaptureSettings settings, Func<string, string> text,
    Action changed, Action<string> assign, Action openFolder, Action<Exception> error,
    Func<string, string> conflict, Action refresh)
{
    private void Change(Action action) { action(); settings.Normalize(); changed(); }
    private void Choice(StackPanel panel, string key, string value, IEnumerable<string> values, Action<string> edit, bool translate = false)
    {
        var choice = CaptureUi.Choice(panel, text(key), value, values.Append(value).Distinct().Select(v =>
            new CaptureUi.Option(v, translate ? text(key + "." + v) : v)), v => Change(() => edit(v)));
        AutomationProperties.SetAutomationId(choice, key);
    }
    public void Toggle(StackPanel panel, string key, bool value, Action<bool> edit)
    {
        var check = CaptureUi.Check(text(key), value, v => Change(() => edit(v)));
        AutomationProperties.SetAutomationId(check, key); panel.Children.Add(check);
    }
    public void Folder(StackPanel panel)
    {
        panel.Children.Add(CaptureUi.Text(text("Capture.Folder")));
        var path = new TextBox { Text = ScreenshotFiles.Folder(settings), Margin = new(0, 0, 0, 10), MinHeight = 36 };
        AutomationProperties.SetAutomationId(path, "Capture.Folder"); panel.Children.Add(path);
        var row = new WrapPanel();
        row.Children.Add(CaptureUi.Button(text("Capture.ChooseFolder"), () =>
        {
            try
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog { InitialDirectory = System.IO.Directory.Exists(path.Text) ? path.Text : "" };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { path.Text = dialog.SelectedPath; Change(() => settings.Folder = path.Text); }
            }
            catch (Exception failure) { error(failure); }
        }));
        row.Children.Add(CaptureUi.Button(text("Capture.SaveFolder"), () =>
        {
            try { var folder = ScreenshotFiles.Folder(new() { Folder = path.Text }); Change(() => settings.Folder = folder); }
            catch (Exception failure) { error(failure); }
        }));
        row.Children.Add(CaptureUi.Button(text("Capture.OpenFolder"), openFolder)); panel.Children.Add(row);
    }
    public void Images(StackPanel panel)
    {
        Choice(panel, "Capture.ImageFormat", settings.ImageFormat, ["png", "jpeg", "webp"], v => settings.ImageFormat = v);
        Choice(panel, "Capture.ImageQuality", settings.ImageQuality.ToString(), ["60", "75", "85", "90", "95", "100"], v => settings.ImageQuality = int.Parse(v));
        Toggle(panel, "Capture.Lossless", settings.WebPLossless, v => settings.WebPLossless = v);
    }
    public void Gif(StackPanel panel)
    {
        Toggle(panel, "Capture.GifShowControls", settings.GifShowControls, v => settings.GifShowControls = v);
        panel.Children.Add(CaptureUi.Text(text("Capture.GifToggleHint")));
        Choice(panel, "Capture.GifSeconds", settings.GifSeconds.ToString(), Enumerable.Range(1, 12).Select(x => (x * 5).ToString()), v => settings.GifSeconds = int.Parse(v));
        Choice(panel, "Capture.GifFps", settings.GifFps.ToString(), ["5", "10", "15"], v => settings.GifFps = int.Parse(v));
        Toggle(panel, "Capture.GifLoop", settings.GifLoop, v => settings.GifLoop = v);
        Choice(panel, "Capture.GifScale", settings.GifScalePercent.ToString(), ["100", "75", "50", "25"], v => settings.GifScalePercent = int.Parse(v));
    }
    public void Video(StackPanel panel)
    {
        Toggle(panel, "Capture.VideoShowControls", settings.VideoShowControls, v => settings.VideoShowControls = v);
        panel.Children.Add(CaptureUi.Text(text("Capture.GifToggleHint")));
        Choice(panel, "Capture.VideoQuality", settings.VideoQuality, ["original", "720p", "1080p", "1440p", "4K"], v => settings.VideoQuality = v, true);
        Choice(panel, "Capture.VideoFormat", settings.VideoFormat, ["mp4", "mkv", "webm"], v => settings.VideoFormat = v);
        Choice(panel, "Capture.VideoFps", settings.VideoFps.ToString(), ["15", "24", "30", "60"], v => settings.VideoFps = int.Parse(v));
        Choice(panel, "Capture.Compression", settings.VideoCompression.ToString(), ["50", "60", "75", "85", "95", "100"], v => settings.VideoCompression = int.Parse(v));
        Choice(panel, "Capture.Audio", settings.AudioMode, ["off", "pc", "mic", "both"], v => settings.AudioMode = v, true);
        AudioDevice(panel, true); AudioDevice(panel, false);
    }
    private void AudioDevice(StackPanel panel, bool microphone)
    {
        var key = microphone ? "Capture.Microphone" : "Capture.Playback";
        var selected = microphone ? settings.Microphone : settings.PlaybackDevice;
        var options = new List<CaptureUi.Option> { new("default", text(key + ".default")) };
        try { options.AddRange(VideoAudioCapture.Devices(microphone).Select(d => new CaptureUi.Option(d.Id, d.Name))); }
        catch (Exception) { panel.Children.Add(CaptureUi.Text(text("Capture.AudioUnavailable"))); }
        if (options.All(d => d.Id != selected)) options.Add(new(selected, text("Capture.DeviceMissing")));
        var choice = CaptureUi.Choice(panel, text(key), selected, options, v => Change(() =>
        { if (microphone) settings.Microphone = v; else settings.PlaybackDevice = v; }));
        choice.IsEnabled = microphone ? settings.AudioMode is "mic" or "both" : settings.AudioMode is "pc" or "both";
        AutomationProperties.SetAutomationId(choice, key);
        // Rebuild on the next audio-mode change, without altering either selected device.
        var audio = panel.Children.OfType<System.Windows.Controls.ComboBox>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "Capture.Audio");
        if (audio is not null) audio.SelectionChanged += (_, _) => choice.IsEnabled = microphone ? settings.AudioMode is "mic" or "both" : settings.AudioMode is "pc" or "both";
    }
    public void Processing(StackPanel panel) => Choice(panel, "Capture.Processing", settings.Processing,
        ["auto", "cpu", "gpu"], v => settings.Processing = v, true);
    public void Output(StackPanel panel, CaptureSource source)
    {
        var output = settings.Outputs[source]; var row = new WrapPanel();
        var warning = CaptureUi.Text(text("Capture.NoOutput"));
        void Save() { warning.Visibility = !output.Clipboard && !output.File ? Visibility.Visible : Visibility.Collapsed; changed(); }
        var clipboard = CaptureUi.Check(text("Capture.ToClipboard"), output.Clipboard, v => { output.Clipboard = v; Save(); });
        var file = CaptureUi.Check(text("Capture.ToFile"), output.File, v => { output.File = v; Save(); });
        AutomationProperties.SetAutomationId(clipboard, "Capture.Output." + source + ".Clipboard");
        AutomationProperties.SetAutomationId(file, "Capture.Output." + source + ".File");
        row.Children.Add(clipboard); row.Children.Add(file); panel.Children.Add(row);
        warning.Visibility = !output.Clipboard && !output.File ? Visibility.Visible : Visibility.Collapsed; panel.Children.Add(warning);
    }
    public void Binding(StackPanel panel, string command)
    {
        var row = new WrapPanel(); var keys = settings.Hotkeys.GetValueOrDefault(command) ?? [];
        var button = CaptureUi.Button(keys.Length == 0 ? text("Capture.Assign") : CaptureHotkeys.Display(keys), () => assign(command));
        button.MaxWidth = 300;
        panel.SizeChanged += (_, _) => button.MaxWidth = Math.Max(100, panel.ActualWidth - 16);
        AutomationProperties.SetAutomationId(button, "Capture.Binding." + command); row.Children.Add(button);
        row.Children.Add(CaptureUi.Button(text("Capture.Reset"), () => { Change(() => settings.Hotkeys.Remove(command)); refresh(); }));
        panel.Children.Add(row); var message = conflict(command);
        if (message.Length > 0) panel.Children.Add(CaptureUi.Text(message));
    }
}
