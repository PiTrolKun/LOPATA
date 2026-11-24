using System.Windows.Controls;
using AIHub.Controls.AnimatedAssets;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    private AnimationWorkshopControl? _animatedAssets;
    private AnimationPreparationControl? _animationPreparation;
    private bool _returnToAnimationWorkshop;
    internal AnimationWorkshopControl? AnimatedAssetsWorkspace => _animatedAssets;
    internal bool IsAnimatedAssets => _animatedAssets is not null && ReferenceEquals(Content, _animatedAssets);
    internal bool IsAnimatedAssetsPreparation => _animationPreparation is not null && ReferenceEquals(Content, _animationPreparation);

    private Button CreateAnimatedAssetsEntrance() =>
        CreateHomeShortcut("smile", "Animation.Title", "Animation.Open", ScenarioNavigationCatalog.AnimatedAssets);

    internal void ShowAnimatedAssets(AnimationWorkshopControl workshop)
    {
        LeaveAnimatedAssetsPreparation();
        _animatedAssets = workshop;
        ShowAnimatedAssetsPage(workshop);
    }

    internal void ShowAnimatedAssetsPreparation(AnimationPreparationControl preparation, bool returnToWorkshop = false)
    {
        if (!LeaveAnimatedAssets()) return;
        _animationPreparation = preparation;
        _returnToAnimationWorkshop = returnToWorkshop;
        ShowAnimatedAssetsPage(preparation);
    }

    private void ShowAnimatedAssetsPage(System.Windows.Controls.UserControl page)
    {
        SelectedDirectionId = ScenarioNavigationCatalog.Creation;
        PlaceholderScenarioId = null;
        IsSandboxLanding = IsVideoPreparation = IsSongClip = IsShorts = IsLetItOutChat = IsNeuroConstructor = false;
        Content = page;
        Render();
    }

    private void BackFromAnimatedAssetsPreparation()
    {
        if (_returnToAnimationWorkshop && _animatedAssets is { } workshop)
        {
            ShowAnimatedAssets(workshop);
            return;
        }
        LeaveAnimatedAssetsPreparation();
        if (!ReturnFromScenario()) ShowHome();
    }

    private void LeaveAnimatedAssetsPreparation()
    {
        if (_animationPreparation is null) return;
        _animationPreparation.Cancel();
        if (IsAnimatedAssetsPreparation) Content = null;
        _animationPreparation = null;
        _returnToAnimationWorkshop = false;
    }

    private bool LeaveAnimatedAssets()
    {
        LeaveAnimatedAssetsPreparation();
        if (!IsAnimatedAssets) return true;
        if (_animatedAssets!.IsBusy) return false;
        _animatedAssets.Suspend();
        Content = null;
        return true;
    }
}
