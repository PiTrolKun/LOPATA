using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private string? FramingClipId()
    {
        var c = Selected(Project); if (_framing.IsChecked != true || _busy || _timeline.Selection.Count > 1 || c?.AssetId is null) return null;
        var t = Project.Tracks.Single(t => t.Clips.Contains(c));
        return t.Kind == "video" && !t.Locked && !t.Hidden ? c.Id : null;
    }
    private async Task UpdateFramingAsync()
    {
        var id = FramingClipId();
        if (id is not null)
        {
            _playing = false; await _preview.PlayAsync(false);
            UpdateVoiceSummary();
            var c = Selected(Project, id)!; if (_position < c.Start || _position >= c.Start + c.Duration) await SeekAsync(c.Start);
            _status.Text = T("FrameHint");
        }
        else if (_framing.IsChecked == true) _status.Text = T("SelectClip");
        await _preview.SetFramingAsync(id, _framing.IsChecked == true && !_busy);
    }
    private void SelectFramingClip(string id)
    {
        if (_busy || _playing || _framing.IsChecked != true) return;
        var c = Selected(Project, id);
        if (c?.AssetId is null || _position < c.Start || _position >= c.Start + c.Duration || Project.Tracks.Single(t => t.Clips.Contains(c)) is not { Kind: "video", Locked: false, Hidden: false }) return;
        _timeline.SelectItems([new(id)]);
    }
    private void ApplyFraming(string id, double x, double y, double scale, double rotation)
    {
        if (id != FramingClipId()) { Safe(RefreshPreviewAsync); return; }
        try {
            _editing.Edit(p => { var c = Selected(p, id)!; c.X = x; c.Y = y; c.Scale = scale; c.Rotation = rotation; });
            _dirty = true; _saveTimer.Stop(); _saveTimer.Start(); ShowProperties(); UpdateTitle();
        }
        catch (Exception e) { _status.Text = Error(e); Safe(RefreshPreviewAsync); }
    }
    private void FrameFit(bool fill)
    {
        var c = Selected(Project); if (_timeline.Selection.Count > 1 || c?.AssetId is null || Project.Tracks.Single(t => t.Clips.Contains(c)) is not { Kind: "video", Locked: false }) return;
        Edit(p => { var clip = Selected(p, c.Id)!; clip.Scale = fill ? VideoFraming.FillScale(p, p.Asset(clip.AssetId!), clip) : 1; clip.X = clip.Y = 0; });
    }
    private void CanvasSettings()
    {
        var window = new Window { Owner = this, Title = T("Canvas"), Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Resources.MergedDictionaries.Add(Resources); window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(18) }; window.Content = panel;
        var choices = new[] { "16:9", "9:16", "1:1", "4:3", "3:4", "21:9", T("CustomRatio") };
        panel.Children.Add(new TextBlock { Text = T("AspectRatio") }); var ratios = new ComboBox { ItemsSource = choices, SelectedIndex = choices.Length - 1, Margin = new(0, 6, 0, 10) }; panel.Children.Add(ratios);
        Help(ratios, "AspectRatio");
        TextBox Field(string key, string value) { panel.Children.Add(new TextBlock { Text = T(key) }); var field = new TextBox { Text = value, MinHeight = 30, Margin = new(0, 4, 0, 10) }; Help(field, key); panel.Children.Add(field); return field; }
        var width = Field("Width", Project.Width.ToString()); var height = Field("Height", Project.Height.ToString()); var fps = Field("Fps", VideoExport.N(Project.Fps));
        ratios.SelectionChanged += (_, _) => {
            var parts = (ratios.SelectedItem as string)?.Split(':'); if (parts?.Length != 2) return;
            var a = Number(parts[0]); var b = Number(parts[1]);
            var longer = Math.Max(Project.Width, Project.Height);
            if (double.TryParse(width.Text, out var currentWidth) && double.TryParse(height.Text, out var currentHeight) && double.IsFinite(currentWidth) && double.IsFinite(currentHeight)) longer = (int)Math.Max(currentWidth, currentHeight);
            width.Text = (Math.Round(longer * a / Math.Max(a, b) / 2) * 2).ToString(); height.Text = (Math.Round(longer * b / Math.Max(a, b) / 2) * 2).ToString();
        };
        panel.Children.Add(new TextBlock { Text = T("CanvasFramingHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 10) });
        panel.Children.Add(B("Apply", () => { var w = (int)Number(width.Text); var h = (int)Number(height.Text); var f = Number(fps.Text); var check = Project.Clone(); check.Width = w; check.Height = h; check.Fps = f; check.Validate(); Edit(p => { p.Width = w; p.Height = h; p.Fps = f; }); window.DialogResult = true; }));
        window.ShowDialog();
    }
}
