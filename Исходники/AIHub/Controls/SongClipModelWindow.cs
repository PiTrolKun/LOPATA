using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Controls;

internal enum SongClipPickerAction { Use, Download, DownloadGroup, DownloadBase }
internal sealed record SongClipPickerRequest(SongClipPickerAction Action, string Id);

internal sealed partial class SongClipModelWindow : Window
{
    private readonly Func<string,string> _l;
    private readonly string _root;
    private readonly StackPanel _groups = new();
    private readonly Button _use = new();
    private readonly TextBlock _status = new() { TextWrapping=TextWrapping.Wrap, Margin=new(6) };
    private string _selected;
    private bool _removalBusy;
    internal SongClipPickerRequest? Request { get; private set; }
    internal SongClipModelWindow(Window owner, Func<string,string> localize, string root, string model, string? mode,
        Func<CancellationToken,Task<SongClipPickerInventory>>? inventoryLoader=null)
    {
        Owner=owner; _l=localize; _root=root;
        var current=SongClipVariantCatalog.Current(model,mode);
        _selected=current.Retired?SongClipVariantCatalog.VisibleVariants.First(v=>v.Available).Id:current.Id;
        MusicWishUi.PrepareWindow(this,T("PickerTitle"),"SongClip.Model.Window",1050);
        Height=720; MinWidth=720; MinHeight=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Insert(0,owner.Resources);
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        var panel=new DockPanel {Margin=new(16)};Content=panel;
        var bottom=new DockPanel();DockPanel.SetDock(bottom,Dock.Bottom);panel.Children.Add(bottom);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        DockPanel.SetDock(actions,Dock.Right);bottom.Children.Add(actions);
        var cancel=Action(T("PickerCancel"),()=>{DialogResult=false;},"Cancel");cancel.IsCancel=true;actions.Children.Add(cancel);
        _use.IsDefault=true;_use.Content=T("PickerUse");_use.Padding=new(12,8,12,8);_use.Margin=new(4);
        AutomationProperties.SetAutomationId(_use,"SongClip.Model.Use");
        _use.Click+=(_,_)=>Finish(SongClipPickerAction.Use,_selected);actions.Children.Add(_use);bottom.Children.Add(_status);
        var hint=new TextBlock {Text=T("PickerHint"),TextWrapping=TextWrapping.Wrap,Margin=new(5,0,5,10)};
        DockPanel.SetDock(hint,Dock.Top);panel.Children.Add(hint);
        panel.Children.Add(new ScrollViewer {Content=_groups,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        InitializeInventory(inventoryLoader);Render();
    }
    private string T(string key)=>_l("SongClip."+key);
    private static string Size(long bytes)=>bytes>=1_000_000_000?$"{bytes/1e9:F2} GB":$"{bytes/1e6:F2} MB";
    private Button Action(string text, Action run, string id)
    {
        var b=new Button {Content=text,Padding=new(10,7,10,7),Margin=new(4),VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetAutomationId(b,"SongClip.Model."+id);b.Click+=(_,_)=>run();return b;
    }
    private void Render()
    {
        _groups.Children.Clear();
        _groups.Children.Add(Row("",T("PickerVolume"),T("PickerState"),new TextBlock {Text=T("PickerActions"),Margin=new(5)}));
        foreach(var family in SongClipVariantCatalog.VisibleVariants.Select(v=>v.Family).Distinct()
            .OrderBy(f=>SongClipVariantCatalog.Variants.Any(v=>v.Family==f&&v.Available)?0:1)
            .ThenBy(f=>_inventory?.FamilyBytes.GetValueOrDefault(f)??0)) {
            var variants=SongClipVariantCatalog.VisibleVariants.Where(v=>v.Family==family).ToArray();
            var stack=new StackPanel();
            stack.Children.Add(new TextBlock {Text=family,FontWeight=FontWeights.Bold,FontSize=18,Margin=new(5,7,5,6)});
            if(family=="MiniMax H3") {
                var common=_inventory?.H3Base;
                var buttons=new WrapPanel();
                var download=Action(common?.Presence.Ready==true?T("PickerCheck"):T("Download"),()=>Finish(SongClipPickerAction.DownloadBase,family),"DownloadBase");
                download.IsEnabled=_inventory is not null;buttons.Children.Add(download);
                var remove=Action(T("PickerDeleteH3"),()=>_ = Delete([],true),"DeleteBase");
                remove.IsEnabled=common?.HasArtifacts==true;buttons.Children.Add(remove);
                var row=Row(T("PickerBase"),common is null?"…":Size(common.Presence.Total),State(common?.Presence),buttons);
                stack.Children.Add(row);
            }
            foreach(var variant in variants) {
                var inventory=_inventory?.Variants.GetValueOrDefault(variant.Id);
                var presence=inventory?.Presence;
                var title=new TextBlock {Text=variant.Title};title.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");
                var radio=new RadioButton {Content=title,GroupName="SongClipVariant",IsChecked=_selected==variant.Id,
                    IsEnabled=variant.Available,Margin=new(5),VerticalAlignment=VerticalAlignment.Center};
                radio.SetResourceReference(RadioButton.ForegroundProperty,"TextPrimaryBrush");
                radio.Opacity=variant.Available?1:.5;
                AutomationProperties.SetName(radio,variant.Title);
                AutomationProperties.SetAutomationId(radio,"SongClip.Model.Variant."+variant.Id);
                radio.Checked+=(_,_)=>{_selected=variant.Id;RefreshSelection();};
                var name=new StackPanel();name.Children.Add(radio);
                var description=new TextBlock {Text=T(variant.Hint),TextWrapping=TextWrapping.Wrap,Margin=new(23,0,6,7)};
                description.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");name.Children.Add(description);
                var amount=variant.Available?(family=="MiniMax H3"?"+ "+(inventory is null?"…":Size(inventory.ExtraBytes)):(presence is null?"…":Size(presence.Total))):T("PickerUnknownSize");
                var buttons=new WrapPanel();
                if(variant.Available) {
                    var download=Action(presence?.Ready==true?T("PickerCheck"):T("Download"),()=>Finish(SongClipPickerAction.Download,variant.Id),"Download."+variant.Id);
                    download.IsEnabled=_inventory is not null;buttons.Children.Add(download);
                    var remove=Action(T("PickerDelete"),()=>_ = Delete([variant.Id]),"Delete."+variant.Id);
                    remove.IsEnabled=inventory?.HasArtifacts==true;remove.ToolTip=T("PickerSharedBase");buttons.Children.Add(remove);
                }
                stack.Children.Add(Row(name,amount,variant.Available?State(presence):T("Pending"),buttons));
            }
            if(variants.Any(v=>v.Available)&&variants.Length>1) {
                var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};
                var download=Action(T("PickerDownloadAll"),()=>Finish(SongClipPickerAction.DownloadGroup,family),"DownloadAll."+family);
                download.IsEnabled=_inventory is not null;buttons.Children.Add(download);
                var remove=Action(T("PickerDeleteGroup"),()=>_ = Delete(variants.Where(v=>v.Available).Select(v=>v.Id).ToArray()),"DeleteGroup."+family);
                remove.IsEnabled=variants.Any(v=>_inventory?.Variants.GetValueOrDefault(v.Id)?.HasArtifacts==true);
                buttons.Children.Add(remove);stack.Children.Add(buttons);
            }
            var border=new Border {Child=stack,CornerRadius=new(8),Margin=new(2,4,2,10),Padding=new(8)};
            border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");_groups.Children.Add(border);
        }
        RefreshSelection();
    }
    private string State(SongClipPresence? state)=>state is null?T("PickerInventoryLoading"):state.Ready?T("PickerPresent"):string.Format(T("PickerMissing"),Size(state.Missing));
    private static Grid Row(object name,string amount,string state,UIElement actions)
    {
        var grid=new Grid {Margin=new(0,4,0,4)};
        foreach(var width in new[]{3d,1d,1.6,2d})grid.ColumnDefinitions.Add(new(){Width=new(width,GridUnitType.Star)});
        UIElement title=name as UIElement??new TextBlock{Text=name.ToString(),TextWrapping=TextWrapping.Wrap,Margin=new(5)};
        grid.Children.Add(title);
        var size=new TextBlock {Text=amount,TextWrapping=TextWrapping.Wrap,Margin=new(5),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(size,1);grid.Children.Add(size);
        var label=new TextBlock {Text=state,TextWrapping=TextWrapping.Wrap,Margin=new(5),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(label,2);grid.Children.Add(label);
        Grid.SetColumn(actions,3);grid.Children.Add(actions);return grid;
    }
    private void RefreshSelection()
    {
        var variant=SongClipVariantCatalog.Find(_selected);_use.IsEnabled=variant.Available&&_inventory is not null;
        _status.Text=_inventory is null?T("PickerInventoryLoading"):T("PickerSelected")+" "+variant.Family+" → "+variant.Title;
    }
    private void Finish(SongClipPickerAction action,string id)
    {
        if(_inventory is null)return;
        Request=new(action,id);
        // No downloader is opened here: the owner receives this result after this window closes.
        DialogResult=true;
    }
    private Task<(SongClipRemovalPlan Plan,string[] Paths,long Bytes)> RemovalPreview(string[] ids,bool baseH3)=>Task.Run(()=>{
        var plan=baseH3?SongClipComponentRemoval.PlanBase(_root):SongClipComponentRemoval.Plan(_root,ids);
        var paths=plan.Paths;return (plan,paths,paths.Sum(path=>new FileInfo(path).Length));
    },_lifetime.Token);
    private async Task Delete(string[] ids,bool baseH3=false)
    {
        if(_removalBusy)return;
        _removalBusy=true;
        try {await DeleteCore(ids,baseH3);}
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) {}
        catch(Exception error){_status.Text=error.Message;}
        finally {_removalBusy=false;}
    }
    private async Task DeleteCore(string[] ids,bool baseH3)
    {
        if(ApplicationBackgroundOperations.Current?.HasPending==true){_status.Text=T("OtherOperation");return;}
        _status.Text=T("PickerRemovalLoading");
        var preview=await RemovalPreview(ids,baseH3);
        var plan=preview.Plan;
        if(_lifetime.IsCancellationRequested)return;
        if(plan.Remove.Length==0){_status.Text=T("PickerNothingRemovable");return;}
        var reviewedPaths=preview.Paths;var reviewedBytes=preview.Bytes;
        var text=(baseH3?T("PickerDeleteH3Warning")+"\n"+string.Join(", ",plan.Affected)+"\n\n":"")+string.Format(T("PickerDeleteConfirm"),Size(reviewedBytes))+"\n\n"+
            string.Join("\n",reviewedPaths)+(plan.Keep.Length>0?"\n\n"+string.Format(T("PickerKeepShared"),plan.Keep.Length)+"\n"+string.Join(", ",plan.ProtectedBy):"");
        var review=new Window {Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        MusicWishUi.PrepareWindow(review,T("PickerDelete"),"SongClip.Model.DeleteConfirm",760);review.Height=510;
        review.Resources.MergedDictionaries.Insert(0,Resources);review.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");review.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        var panel=new DockPanel {Margin=new(16)};review.Content=panel;
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
        buttons.Children.Add(Action(T("PickerCancel"),()=>review.DialogResult=false,"DeleteCancel"));
        buttons.Children.Add(Action(T("PickerDelete"),()=>review.DialogResult=true,"DeleteApply"));
        panel.Children.Add(new ScrollViewer {Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap},VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        if(review.ShowDialog()!=true)return;
        IsEnabled=false;
        string? failure=null;
        try {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            var refreshed=await RemovalPreview(ids,baseH3);var latest=refreshed.Plan;
            if(!latest.Remove.Select(f=>f.Path).SequenceEqual(plan.Remove.Select(f=>f.Path),StringComparer.OrdinalIgnoreCase)
                ||!refreshed.Paths.SequenceEqual(reviewedPaths,StringComparer.OrdinalIgnoreCase)||refreshed.Bytes!=reviewedBytes)
                throw new InvalidOperationException(T("PickerInventoryChanged"));
            var guard=new DelegateModelUsageGuard(_=>_lifetime.IsCancellationRequested||ApplicationBackgroundOperations.Current?.HasPending==true);
            await Task.Run(()=>SongClipComponentRemoval.Execute(plan,new ManagedModelLibraryStore(),guard),_lifetime.Token);
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) {}
        catch(Exception error){failure=error.Message;}
        finally {
            IsEnabled=true;
            if(!_lifetime.IsCancellationRequested) {
                await RefreshInventoryAsync();
                if(failure is not null)_status.Text=failure;
            }
        }
    }
}
