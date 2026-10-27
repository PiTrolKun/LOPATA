using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private void BuildShotReferences(StackPanel editor, VideoShotPlan shot, System.Action edited, bool readOnly)
    {
        var p = Session.Project; var images = p.References?.Images ?? [];
        var names = new List<string>(); var panel = new WrapPanel();
        foreach (var image in images.Where(i => p.References!.Bindings.Any(b => b.ImageId == i.Id) || shot.ReferenceIds.Contains(i.Id)))
        {
            var variant = p.References!.Drawing?.Variants.FirstOrDefault(v => v.ImageId == image.Id);
            var name = p.References.Plan?.Items.FirstOrDefault(i => i.Id == variant?.PromptId)?.Name ?? image.Name;
            if (shot.ReferenceIds.Contains(image.Id)) names.Add(name);
            var choice = new System.Windows.Controls.CheckBox { Content = name, IsChecked = shot.ReferenceIds.Contains(image.Id), IsEnabled = !readOnly,
                Margin = new Thickness(6), MaxWidth = 220 };
            choice.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            choice.Checked += (_, _) => { shot.ReferenceIds = shot.ReferenceIds.Append(image.Id).Distinct().ToArray(); edited(); };
            choice.Unchecked += (_, _) => { shot.ReferenceIds = shot.ReferenceIds.Where(id => id != image.Id).ToArray(); edited(); };
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 160;
                bitmap.UriSource = new Uri(VideoReferences.PathFor(p, image)); bitmap.EndInit(); bitmap.Freeze();
                var content = new StackPanel(); content.Children.Add(new System.Windows.Controls.Image { Source = bitmap, Height = 85, Width = 140 });
                var caption = Text("ShotsReferences"); caption.Text = name; content.Children.Add(caption); choice.Content = content;
            }
            catch (Exception e) when (e is System.IO.IOException or NotSupportedException) { }
            panel.Children.Add(choice);
        }
        editor.Children.Add(Text("ShotsReferences"));
        var selectedNames = Text("ShotsReferences"); selectedNames.Text = string.Join(" · ", names);
        System.Windows.Automation.AutomationProperties.SetAutomationId(selectedNames, "VideoPrep.Shots.ReferenceNames"); editor.Children.Add(selectedNames);
        var expander = new Expander { Header = T("ShotsChooseReferences"), Content = panel }; expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); editor.Children.Add(expander);
        foreach (var missing in shot.ReferenceIds.Where(id => !readOnly && (!images.Any(i => i.Id == id) || !p.References!.Bindings.Any(b => b.ImageId == id))).ToArray())
            editor.Children.Add(Action("ShotsRemoveMissing", () => { shot.ReferenceIds = shot.ReferenceIds.Where(id => id != missing).ToArray(); edited(); Render(); }));
    }
}
