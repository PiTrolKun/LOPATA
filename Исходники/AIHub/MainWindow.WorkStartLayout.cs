using System.Windows;

namespace AIHub;

public partial class MainWindow
{
    private void RefreshWorkStartWorkspaceLayout()
    {
        // Local visibility preserves the selected workspace layout while the window is hidden.
        var useWorkspaceLayout = ImageUtilityPage.Visibility == Visibility.Visible ||
            (ScenarioNavigationPage.Visibility == Visibility.Visible && ScenarioNavigationPage.IsSongClip);
        if (useWorkspaceLayout)
        {
            WorkStartPage.MaxWidth = double.PositiveInfinity;
            WorkStartPage.Margin = new Thickness(18, 16, 18, 16);
        }
        else
        {
            WorkStartPage.SetResourceReference(MaxWidthProperty, "UiMainContentMaxWidth");
            WorkStartPage.SetResourceReference(MarginProperty, "UiPageMargin");
        }
    }
}
