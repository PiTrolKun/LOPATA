using System.Windows;

namespace AIHub;

public partial class MainWindow
{
    private void RefreshWorkStartWorkspaceLayout()
    {
        // Local visibility preserves the selected workspace layout while the window is hidden.
        var useWorkspaceLayout = ImageUtilityPage.Visibility == Visibility.Visible ||
            (ScenarioNavigationPage.Visibility == Visibility.Visible && ScenarioNavigationPage.IsSongClip);
        if (ScenarioNavigationPage.Visibility == Visibility.Visible && ScenarioNavigationPage.IsLetItOutChat)
        {
            WorkStartPage.MaxWidth = double.PositiveInfinity;
            var pageMargin = (Thickness)FindResource("UiPageMargin");
            WorkStartPage.Margin = new Thickness(pageMargin.Left, pageMargin.Top, pageMargin.Right, 8);
        }
        else if (useWorkspaceLayout)
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
