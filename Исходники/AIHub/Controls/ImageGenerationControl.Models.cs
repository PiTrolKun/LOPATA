using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    public ContextMenu ModelMenu()
    {
        var menu = new ContextMenu();
        var installed = ImageGenerationCatalog.Manifest.Models.Where(m => _installation.IsReady(_modelsRoot, m.Id)).ToArray();
        foreach (var model in installed)
        {
            var item = new MenuItem { Header = model.Name, IsCheckable = true, IsChecked = model.Id == _modelId, IsEnabled = CanChangeModel };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "Generation.InstalledModel." + model.Id);
            item.Click += async (_, _) => await TryAction(() => SwitchModelAsync(model.Id)); menu.Items.Add(item);
        }
        if (installed.Length == 0) menu.Items.Add(new MenuItem { Header = L("NoInstalledModels"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var more = new MenuItem { Header = L("OtherModels"), IsEnabled = CanChangeModel };
        more.Click += (_, _) => { _page = 0; Render(); }; menu.Items.Add(more); return menu;
    }
    public void ShowModelMenu(FrameworkElement target)
    { var menu = ModelMenu(); menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; }
    private async Task SwitchModelAsync(string id)
    {
        if (!CanChangeModel || id == _modelId || !_installation.IsReady(_modelsRoot, id)) return;
        var previous = _modelId; _modelId = id;
        // Installed profiles still require integrity and license checks before use.
        if (!await PrepareAsync(false)) _modelId = previous;
        else (_width, _height) = ImageGenerationDimensions.Fit(_width, _height, Math.Max(_width, _height), ImageGenerationCatalog.Get(id).MaximumSide);
        Render();
    }
}
