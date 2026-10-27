using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

/// <summary>Manual mode is deliberately read/copy only; the normal generator has no project linkage.</summary>
public sealed class VideoReferencePromptsWindow : Window
{
    public VideoReferencePromptsWindow(Window? owner, Func<string, string> localize, VideoPreparationProject project)
    {
        string T(string key) => localize("VideoPrep." + key);
        VideoReferenceWindowUi.Configure(this, owner, T("RefManualPrompts"));
        AutomationProperties.SetAutomationId(this, "VideoPrep.ManualPrompts.Window");
        var page = new StackPanel { Margin = new Thickness(16) };
        page.Children.Add(VideoReferenceWindowUi.Text(T("RefManualImportHint")));
        foreach (var item in VideoReferencePlans.Ensure(project).Items.Where(i => i.Selected))
        {
            page.Children.Add(VideoReferenceWindowUi.Text(item.Name));
            page.Children.Add(VideoReferenceWindowUi.Editor(item.Native, true));
            page.Children.Add(VideoReferenceWindowUi.Editor(item.English, true));
            page.Children.Add(VideoReferenceWindowUi.Button(T("RefCopyPrompt"), "RefCopyPrompt." + item.Id,
                () => { try { System.Windows.Clipboard.SetText(item.English); } catch (System.Runtime.InteropServices.ExternalException) { } }));
        }
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
