using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private void RefreshModelLabel()
    {
        var variant=SongClipVariantCatalog.Current(_project.Model,_project.Settings.Motion.Mode);
        _model.Content=new TextBlock {Text=variant.Retired?_l("SongClip.RetiredModelLabel"):variant.Family+" → "+variant.Title+"…",TextWrapping=TextWrapping.Wrap};
    }
    internal bool SelectVariant(string id)
    {
        var variant=SongClipVariantCatalog.Find(id);
        if(!variant.Available)return false;
        if(_busy || OwnOperation || !Checkpoint())return false;
        _project.Model=variant.Model;
        if(variant.Model=="h3_music_context")
            _project.Settings.Motion=_project.Settings.Motion with {Mode=SongClipModes.Normalize(variant.Mode)};
        LoadFields();return Checkpoint();
    }
    private void OpenModelPicker()
    {
        if(_busy || OwnOperation || !Checkpoint())return;
        try {
            if(!Path.IsPathFullyQualified(_root)){_status.Text=_l("Generation.StorageRequired");return;}
            var picker=new SongClipModelWindow(Window.GetWindow(this),_l,_root,_project.Model,_project.Settings.Motion.Mode);
            if(picker.ShowDialog()!=true || picker.Request is not {} request)return;
            // ShowDialog has returned: the picker is closed before any downloader is constructed.
            ApplyPickerRequest(request);
        }
        catch(Exception error){_status.Text=error.Message;}
    }
    internal void ApplyPickerRequest(SongClipPickerRequest request)
    {
        if(_busy || OwnOperation)return;
        if(request.Action is SongClipPickerAction.Use or SongClipPickerAction.Download
            &&SongClipVariantCatalog.Find(request.Id).Retired){_status.Text=_l(SongClipVariantCatalog.RetiredMessageKey);return;}
        if(request.Action==SongClipPickerAction.Use) {
            if(!SelectVariant(request.Id))return;
            if(SongClipVariantCatalog.Presence(SongClipVariantCatalog.Cards(request.Id,_root)).Ready)return;
        }
        if(ApplicationBackgroundOperations.Current is {BlocksNewWork:true} background){background.ReportBlocked();_status.Text=_l("SongClip.OtherOperation");return;}
        var isGroup=request.Action is SongClipPickerAction.DownloadGroup or SongClipPickerAction.DownloadBase;
        var variant=isGroup?SongClipVariantCatalog.AvailableFamily(request.Id):SongClipVariantCatalog.Find(request.Id);
        if(variant is null){_status.Text=_l(SongClipVariantCatalog.Variants.Any(v=>v.Family==request.Id&&v.Retired)
            ?SongClipVariantCatalog.RetiredMessageKey:"SongClip.Pending");return;}
        var cards=request.Action==SongClipPickerAction.DownloadBase?SongClipVariantCatalog.BaseCards(request.Id,_root)
            :request.Action==SongClipPickerAction.DownloadGroup?SongClipVariantCatalog.GroupCards(request.Id,_root):null;
        if(cards is {Length:0})return;
        if(new SongClipDownloadWindow(Window.GetWindow(this),_l,_root,_connections,variant.Model,variant.Mode,
            autoDownload:true,requestedCards:cards).ShowDialog()==true)_status.Text=_l("SongClip.ComponentsReady");
        RefreshModelLabel();
    }
    private void OpenComponents()
    {
        if(_busy || OwnOperation)return;
        try {
            if(!Path.IsPathFullyQualified(_root)){_status.Text=_l("Generation.StorageRequired");return;}
            var variant=SongClipVariantCatalog.Current(_project.Model,_project.Settings.Motion.Mode);
            if(variant.Retired){_status.Text=_l(SongClipVariantCatalog.RetiredMessageKey);return;}
            if(!variant.Available){_status.Text=_l("SongClip.Pending");return;}
            ApplyPickerRequest(new(SongClipPickerAction.Download,variant.Id));
        }
        catch(Exception error){_status.Text=error.Message;}
    }
}
