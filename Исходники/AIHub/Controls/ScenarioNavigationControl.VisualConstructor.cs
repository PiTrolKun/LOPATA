using AIHub.Controls.VisualConstructor;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    internal ConstructorEditor? VisualConstructorView { get; private set; }
    internal bool IsVisualConstructor { get; private set; }

    internal void ShowVisualConstructor(string? stateDirectory = null)
    {
        if (VisualConstructorView is null)
        {
            VisualConstructorView = new ConstructorEditor(_localize, stateDirectory);
            VisualConstructorView.BackRequested += () => GoBack();
        }
        SelectedDirectionId = ScenarioNavigationCatalog.Creation;
        IsVisualConstructor = true;
        Render();
    }
}
