using System.Windows;
using System.Windows.Controls;
using AIHub.Services.SongClip;
using ProgressBar=System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

internal sealed partial class SongClipModelWindow
{
    private readonly CancellationTokenSource _lifetime=new();
    private SongClipPickerInventory? _inventory;
    private Func<CancellationToken,Task<SongClipPickerInventory>> _inventoryLoader=null!;
    private readonly ProgressBar _inventoryProgress=new(){IsIndeterminate=true,Height=4,Margin=new(4,2,4,6)};
    internal Task InventoryReady {get;private set;}=Task.CompletedTask;
    private void InitializeInventory(Func<CancellationToken,Task<SongClipPickerInventory>>? loader)
    {
        _inventoryLoader=loader??(token=>Task.Run(()=>SongClipPickerInventory.Scan(_root,token),token));
        _inventoryProgress.SetResourceReference(ProgressBar.ForegroundProperty,"AccentBrush");
        _inventoryProgress.SetResourceReference(ProgressBar.BackgroundProperty,"PanelBrush");
        var panel=(DockPanel)Content;DockPanel.SetDock(_inventoryProgress,Dock.Top);panel.Children.Insert(panel.Children.Count-1,_inventoryProgress);
        Loaded+=(_,_)=>InventoryReady=RefreshInventoryAsync();
        Closed+=(_,_)=>_lifetime.Cancel();
    }
    private async Task RefreshInventoryAsync()
    {
        _inventoryProgress.Visibility=Visibility.Visible;
        try {
            var snapshot=await _inventoryLoader(_lifetime.Token);
            if(_lifetime.IsCancellationRequested)return;
            _inventory=snapshot;Render();
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){}
        catch(Exception error){_status.Text=error.Message;}
        finally {_inventoryProgress.Visibility=Visibility.Collapsed;}
    }
}
