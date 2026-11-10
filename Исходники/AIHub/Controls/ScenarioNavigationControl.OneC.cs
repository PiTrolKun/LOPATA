using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    internal OneC.OneCControl? OneCView { get; private set; }
    internal bool IsOneC { get; private set; }
    private bool LeaveOneC()
    {
        if (IsOneC && OneCView?.Checkpoint() == false) return false;
        IsOneC = false; return true;
    }
    internal OneC.OneCControl ShowOneC(Window owner)
    {
        LeaveSpeechAssets();
        OneCView ??= new(owner, _localize);
        IsOneC = true; SelectedDirectionId = ScenarioNavigationCatalog.Creation;
        Content = OneCView; _ = OneCView.EnterAsync(); StateChanged?.Invoke(); return OneCView;
    }
}
