using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private void TransformClip()
    {
        var clip = Selected(Project);
        if (_busy || _timeline.Selection.Count != 1 || clip?.AssetId is null || Project.Tracks.Single(t => t.Clips.Contains(clip)) is not { Kind: "video", Locked: false, Hidden: false }) { _status.Text = T("SelectTransformClip"); return; }
        var dialog = new Window { Owner = this, Title = T("Transform"), Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources.MergedDictionaries.Add(Resources); dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(18) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = Project.Asset(clip.AssetId).Name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        TextBox Field(string key, double value) {
            panel.Children.Add(new TextBlock { Text = T(key), Margin = new(0, 10, 0, 3) });
            var input = new TextBox { Text = VideoExport.N(value), MinHeight = 30 };
            AutomationProperties.SetName(input, T(key)); AutomationProperties.SetAutomationId(input, "VideoEditor.Transform." + key); panel.Children.Add(input); return input;
        }
        var size = Field("SizePercent", clip.Scale * 100); var angle = Field("Rotation", clip.Rotation);
        var x = Field("X", clip.X); var y = Field("Y", clip.Y);
        var buttons = new WrapPanel { Margin = new(0, 8, 0, 4) }; panel.Children.Add(buttons);
        buttons.Children.Add(B("RotateLeft", () => angle.Text = VideoExport.N(Number(angle.Text) - 90)));
        buttons.Children.Add(B("RotateRight", () => angle.Text = VideoExport.N(Number(angle.Text) + 90)));
        buttons.Children.Add(B("ResetTransform", () => { size.Text = "100"; angle.Text = x.Text = y.Text = "0"; }));
        panel.Children.Add(new TextBlock { Text = T("TransformHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed }; panel.Children.Add(error);
        panel.Children.Add(B("Apply", () => {
            try {
                var scale = Number(size.Text) / 100; var rotation = Number(angle.Text) % 360; var dx = Number(x.Text); var dy = Number(y.Text);
                var check = Project.Clone(); var target = Selected(check, clip.Id)!; target.Scale = scale; target.Rotation = rotation; target.X = dx; target.Y = dy; check.Validate();
                Edit(p => { var c = Selected(p, clip.Id)!; c.Scale = scale; c.Rotation = rotation; c.X = dx; c.Y = dy; }); dialog.DialogResult = true;
            }
            catch (Exception e) when (e is FormatException or OverflowException or System.IO.InvalidDataException) { error.Text = T("TransformInvalid"); }
        }));
        dialog.ShowDialog();
    }
    private void InsertSelectedMaterial(VideoProject project, VideoAsset asset, double at)
    {
        var before = project.Tracks.SelectMany(t => t.Clips).Select(c => c.Id).ToHashSet();
        VideoImport.Insert(project, asset, at);
        var inserted = project.Tracks.Where(t => t.Kind == (asset.Kind == "audio" ? "audio" : "video")).SelectMany(t => t.Clips).First(c => !before.Contains(c.Id));
        _timeline.SelectItems([new(inserted.Id)]);
        if (asset.Kind is "image" or "svg") { _framing.IsChecked = true; _position = at; }
    }
}
