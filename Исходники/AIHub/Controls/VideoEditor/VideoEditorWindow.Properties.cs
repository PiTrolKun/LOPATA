using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private static double Number(string text) => double.Parse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
    private Dictionary<string, string>? Ask(string title, (string Key, string Value)[] fields, string[]? checks = null)
    {
        var window = new Window { Owner = this, Title = T(title), Width = 520, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 70), SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        window.Resources.MergedDictionaries.Add(Resources); window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(18) }; window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; var inputs = new Dictionary<string, TextBox>(); var flags = new Dictionary<string, CheckBox>();
        foreach (var (key, value) in fields) { panel.Children.Add(new TextBlock { Text = T(key), Margin = new(0, 6, 0, 3) }); var input = new TextBox { Text = value, MinHeight = 30 }; Help(input, key); System.Windows.Automation.AutomationProperties.SetAutomationId(input, "VideoEditor.Dialog." + key); panel.Children.Add(input); inputs[key] = input; }
        foreach (var key in checks ?? []) { var check = new CheckBox { Content = key.StartsWith('@') ? key[1..] : T(key), Margin = new(0, 10, 0, 0), IsChecked = key.StartsWith('@') }; Help(check, key.StartsWith('@') ? "ExportTrack" : key); panel.Children.Add(check); flags[key] = check; }
        panel.Children.Add(B("Apply", () => window.DialogResult = true));
        if (window.ShowDialog() != true) return null;
        var values = inputs.ToDictionary(p => p.Key, p => p.Value.Text); foreach (var flag in flags) values[flag.Key] = flag.Value.IsChecked == true ? "true" : "false"; return values;
    }
    private void ShowProperties()
    {
        _properties.Children.Clear(); _properties.Margin = new(10);
        if (_timeline.Selection.Count > 1) {
            _properties.Children.Add(new TextBlock { Text = string.Format(T("SelectedElements"), _timeline.Selection.Count), FontWeight = FontWeights.SemiBold });
            _properties.Children.Add(new TextBlock { Text = T("MultiSelectionHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) }); return;
        }
        if (_timeline.Selection.Primary is { Subtitle: true }) { _properties.Children.Add(B("FragmentSettings", FragmentSettings)); return; }
        var track = Project.Tracks.FirstOrDefault(t => t.Id == _timeline.SelectedTrack); var clip = Selected(Project);
        if (track is null) { _properties.Children.Add(new TextBlock { Text = T("SelectClip"), TextWrapping = TextWrapping.Wrap }); return; }
        if (clip?.AssetId is not null && track.Kind == "video") _properties.Children.Add(B("Transform", TransformClip));
        var fields = new Dictionary<string, TextBox>();
        TextBox Input(string key, string value) { _properties.Children.Add(new TextBlock { Text = T(key), Margin = new(0, 7, 0, 2) }); var input = new TextBox { Text = value, MinHeight = 28 }; Help(input, key); _properties.Children.Add(input); fields[key] = input; return input; }
        Input("TrackName", track.Name); if (track.Kind == "audio") Input("TrackVolume", VideoExport.N(track.Volume * 100));
        var mute = new CheckBox { Content = T(track.Kind == "audio" ? "Mute" : "Hide"), IsChecked = track.Kind == "audio" ? track.Muted : track.Hidden, Margin = new(0, 8, 0, 0) }; _properties.Children.Add(mute);
        var locked = new CheckBox { Content = T("Lock"), IsChecked = track.Locked, Margin = new(0, 8, 0, 0) }; _properties.Children.Add(locked);
        Help(mute, track.Kind == "audio" ? "Mute" : "Hide"); Help(locked, "Lock");
        if (clip is not null)
        {
            Input("Start", VideoExport.N(clip.Start)); Input("SourceIn", VideoExport.N(clip.SourceIn)); Input("Duration", VideoExport.N(clip.Duration)); Input("Speed", VideoExport.N(clip.Speed));
            if (track.Kind == "audio") Input("ClipVolume", VideoExport.N(clip.Volume * 100));
            else { Input("X", VideoExport.N(clip.X)); Input("Y", VideoExport.N(clip.Y)); Input("Scale", VideoExport.N(clip.Scale)); Input("Rotation", VideoExport.N(clip.Rotation)); Input("Opacity", VideoExport.N(clip.Opacity));
                Input("Brightness", VideoExport.N(clip.Brightness)); Input("Contrast", VideoExport.N(clip.Contrast)); Input("Saturation", VideoExport.N(clip.Saturation));
                Input("CropLeft", VideoExport.N(clip.CropLeft)); Input("CropTop", VideoExport.N(clip.CropTop)); Input("CropRight", VideoExport.N(clip.CropRight)); Input("CropBottom", VideoExport.N(clip.CropBottom)); }
            Input("FadeIn", VideoExport.N(clip.FadeIn)); Input("FadeOut", VideoExport.N(clip.FadeOut)); if (clip.Text is not null) Input("TitleText", clip.Text);
            if (VideoEditing.TooLong(Project, clip)) _properties.Children.Add(new TextBlock { Text = T("TooLong"), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed });
        }
        _properties.Children.Add(B("Apply", () => Edit(p => {
            var t = p.Tracks.Single(t => t.Id == track.Id); t.Name = fields["TrackName"].Text;
            if (t.Kind == "audio") { var volume = Number(fields["TrackVolume"].Text); if (volume != t.Volume * 100) VideoAudioVolume.Set(p, t.Id, null, volume); t.Muted = mute.IsChecked == true; } else t.Hidden = mute.IsChecked == true;
            var wasLocked = t.Locked; t.Locked = locked.IsChecked == true; if (clip is null || wasLocked) return;
            var c = Selected(p, clip.Id)!; var speed = Number(fields["Speed"].Text);
            VideoEditing.SetTiming(p, c.Id, Number(fields["Start"].Text), Number(fields["SourceIn"].Text), Number(fields["Duration"].Text) * c.Speed / speed, speed);
            if (t.Kind == "audio") { var volume = Number(fields["ClipVolume"].Text); if (volume != c.Volume * 100) VideoAudioVolume.Set(p, t.Id, c.Id, volume); }
            else { c.X = Number(fields["X"].Text); c.Y = Number(fields["Y"].Text); c.Scale = Number(fields["Scale"].Text); c.Rotation = Number(fields["Rotation"].Text); c.Opacity = Number(fields["Opacity"].Text);
                c.Brightness = Number(fields["Brightness"].Text); c.Contrast = Number(fields["Contrast"].Text); c.Saturation = Number(fields["Saturation"].Text);
                c.CropLeft = Number(fields["CropLeft"].Text); c.CropTop = Number(fields["CropTop"].Text); c.CropRight = Number(fields["CropRight"].Text); c.CropBottom = Number(fields["CropBottom"].Text); }
            c.FadeIn = Number(fields["FadeIn"].Text); c.FadeOut = Number(fields["FadeOut"].Text); if (c.Text is not null) c.Text = fields["TitleText"].Text;
        })));
    }
}
