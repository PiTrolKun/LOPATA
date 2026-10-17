using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataGrid=System.Windows.Controls.DataGrid;

namespace AIHub.Controls;

internal static class FileMetadataScroll
{
    internal static void Configure(DataGrid grid)
    {
        // A lyrics field may occupy much more than one viewport. Item scrolling
        // cannot reach its middle; keep virtualization but measure offsets in pixels.
        VirtualizingPanel.SetScrollUnit(grid,ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(grid,true);
        Attach(grid);
    }
    internal static void Attach(FrameworkElement control)=>control.PreviewMouseWheel+=OnWheel;
    private static void OnWheel(object sender,MouseWheelEventArgs e)
    {
        if(e.Handled||e.Delta==0||sender is not FrameworkElement control)return;
        var inner=Viewer(control);if(inner is null)return;
        var target=CanScroll(inner,e.Delta)?inner:ParentViewer(control);
        if(target is null)return;
        var lines=SystemParameters.WheelScrollLines;
        var step=lines<0?target.ViewportHeight:16d*lines;
        target.ScrollToVerticalOffset(Math.Clamp(target.VerticalOffset-e.Delta/120d*step,0,target.ScrollableHeight));
        // The nested ScrollViewer would otherwise consume the same gesture again.
        e.Handled=true;
    }
    private static bool CanScroll(ScrollViewer viewer,int delta)=>delta>0?viewer.VerticalOffset>.1:viewer.VerticalOffset<viewer.ScrollableHeight-.1;
    private static ScrollViewer? ParentViewer(DependencyObject child)
    {
        for(var parent=VisualTreeHelper.GetParent(child);parent is not null;parent=VisualTreeHelper.GetParent(parent))
            if(parent is ScrollViewer viewer)return viewer;
        return null;
    }
    private static ScrollViewer? Viewer(DependencyObject parent)
    {
        if(parent is ScrollViewer viewer)return viewer;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(Viewer(VisualTreeHelper.GetChild(parent,i)) is { } found)return found;
        return null;
    }
}
