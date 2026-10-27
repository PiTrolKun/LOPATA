using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private ScrollViewer? _shotsListScroll;
    private string _shotsListProject = "";
    private string ShotSceneLabel(VideoScenePlan scene, int index) => string.IsNullOrWhiteSpace(scene.Name) || Guid.TryParse(scene.Name.Trim(), out _)
        ? string.Format(T("ShotsSceneNumber"), index + 1) : scene.Name;

    private void FitShotsList(ScrollViewer list, FrameworkElement body, FrameworkElement commands)
    {
        var offset = _shotsListProject == Session.Project.Id ? _shotsListScroll?.VerticalOffset ?? 0 : 0;
        _shotsListProject = Session.Project.Id; _shotsListScroll = list;
        // The page's StackPanel measures children with unlimited height. Bound the list
        // to the actual viewport, independently of a long editor or expanded details.
        void Resize(object? sender, EventArgs e)
        {
            if (!list.IsLoaded || _pageScroll is not { ViewportHeight: > 0 } page) return;
            var before = body.TranslatePoint(new System.Windows.Point(), page).Y + page.VerticalOffset;
            var height = Math.Max(120, page.ViewportHeight - Math.Max(0, before) - commands.ActualHeight - 24);
            if (!double.IsFinite(list.MaxHeight) || Math.Abs(list.MaxHeight - height) > .5) list.MaxHeight = height;
        }
        list.Loaded += (_, _) => { list.LayoutUpdated += Resize; Resize(null, EventArgs.Empty); list.ScrollToVerticalOffset(offset); };
        list.Unloaded += (_, _) => list.LayoutUpdated -= Resize;
    }
}
