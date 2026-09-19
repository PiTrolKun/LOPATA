using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private Border BuildRouteCard()
    {
        var root = new DockPanel();
        var edit = ActionButton("✎", () => RunUi(EditRoute));
        edit.Width = 34; edit.Height = 34; edit.Padding = new Thickness(4); edit.Margin = new Thickness(0,8,0,0);
        edit.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        edit.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"); edit.FontSize = 18;
        edit.ToolTip = _l("Studio.Route.Edit");
        System.Windows.Automation.AutomationProperties.SetName(edit,_l("Studio.Route.Edit"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(edit,"Studio.EditRoute");
        DockPanel.SetDock(edit,Dock.Bottom); root.Children.Add(edit);
        var progress = new StackPanel(); progress.Children.Add(LiteraryUi.Text(_l("Studio.Route"),true));
        _route.Margin = new Thickness(0,5,0,8); progress.Children.Add(_route); progress.Children.Add(_routeDescription);
        root.Children.Add(new ScrollViewer { Content = progress, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return LiteraryWorkspaceParts.Card(root);
    }

    private void EditRoute()
    {
        if (IsWorking || _blocked()) return;
        try
        {
            var store = new LiteraryCalibrationStore(_directory); var brief = store.Read();
            var wasPlain = !new LiteraryCalibrationDocument(brief).Structured;
            var dialog = new LiteraryRouteEditorWindow(Window.GetWindow(this),brief,_l,_language,updated =>
            {
                store.Save(updated);
                // A plain creation brief becomes a structured field without losing its selected-source state.
                if (wasPlain && State.Selection.Remove("creation/plain",out var selected))
                    State.Selection[brief.Length == 0 ? "creation" : "creation/field/0"] = selected;
                _dirty = true;
            });
            if (dialog.ShowDialog() == true) { RefreshSources(); Save(); }
        }
        catch (Exception) { _status.Text = _l("Studio.Route.OpenError"); }
    }
}
