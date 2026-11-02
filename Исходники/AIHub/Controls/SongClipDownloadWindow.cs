using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using Button=System.Windows.Controls.Button;
using ProgressBar=System.Windows.Controls.ProgressBar;
using Orientation=System.Windows.Controls.Orientation;

namespace AIHub.Controls;

internal sealed class SongClipDownloadWindow:Window
{
    private CancellationTokenSource? _cancel;
    private readonly Func<string,string> _l;
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel _files=new();
    private readonly ProgressBar _progress=new(){Minimum=0,Maximum=100,Height=9,Margin=new(0,8,0,8)};
    private readonly Button _download=new(),_stop=new();
    private readonly ManagedModelArtifactCard[] _cards;
    private readonly Dictionary<string, TextBlock> _rows = new();
    private readonly ManagedModelLibraryStore _store=new();
    private readonly int _connections;
    private bool _busy;
    private readonly string _model, _root;
    private readonly bool _montage, _acquisitionOnly;
    internal SongClipDownloadWindow(Window owner,Func<string,string> l,string root,int connections,string model="glitchframe",string? mode=null,bool autoDownload=false,ManagedModelArtifactCard[]? requestedCards=null)
    {
        if(SongClipVariantCatalog.IsRetiredModel(model))throw new InvalidOperationException(l(SongClipVariantCatalog.RetiredMessageKey));
        Owner=owner;_l=l;_connections=connections;MusicWishUi.PrepareWindow(this,l("SongClip.Components"),"SongClip.Download.Window",820);Height=580;MinWidth=600;MinHeight=400;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        Resources.MergedDictionaries.Insert(0,owner.Resources);
        _model=model;_root=root;_montage=model=="h3_music_context"&&SongClipModes.IsMontage(mode);
        _acquisitionOnly=requestedCards is not null;
        _cards=requestedCards??(model==InfiniteTalkRunner.ModelId?InfiniteTalkComponents.Cards(root):model==GeekatplayRunner.ModelId?GeekatplayComponents.Cards(root):model=="h3_music_context"?(_montage?H3MontageComponents.Cards(root):H3MusicComponents.Cards(root)).ToArray():GlitchframeComponents.Cards(root));foreach(var card in _cards)_store.Upsert(card);
        foreach(var card in _cards) {
            _files.Children.Add(new TextBlock{Text=card.DisplayName,FontWeight=FontWeights.SemiBold,Margin=new(0,9,0,4)});
            foreach(var file in card.Files) {
                var row=new TextBlock{Text="○ "+file.RelativePath,Margin=new(0,3,0,3),TextWrapping=TextWrapping.Wrap};
                _rows[card.ModelArtifactId+"/"+file.RelativePath]=row;_files.Children.Add(row);
            }
        }
        var panel=new DockPanel{Margin=new(20)};Content=panel;
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);panel.Children.Add(bottom);
        bottom.Children.Add(_status);bottom.Children.Add(_progress);var buttons=new StackPanel{Orientation=Orientation.Horizontal};bottom.Children.Add(buttons);
        _download.Content=l("SongClip.Download");_download.Padding=new(12,8,12,8);_download.Click+=async(_,_)=>await Prepare(true);buttons.Children.Add(_download);
        _stop.Content=l("SongClip.Stop");_stop.Padding=new(12,8,12,8);_stop.Click+=(_,_)=>_cancel?.Cancel();buttons.Children.Add(_stop);
        panel.Children.Add(new ScrollViewer{Content=_files,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        AutomationProperties.SetAutomationId(this,"SongClip.Download.Window");
        Loaded+=async(_,_)=>await Prepare(autoDownload);
        Closing+=(_,_)=>_cancel?.Cancel();
    }
    private async Task Prepare(bool download)
    {
        if(_busy)return;_busy=true;_download.IsEnabled=false;_stop.IsEnabled=true;_cancel=new CancellationTokenSource();
        var total=_cards.Sum(c=>c.TotalBytes);long completed=0;
        using var acquisition=new ManagedModelAcquisitionService(_store){MaximumParallelConnections=_connections};
        try {
            using var audit=ComponentAudit.CaptureOperation();
            if(download)await ComponentLicenseGate.EnsureAsync(_cards.Select(c=>c.ModelArtifactId).ToArray(),_cancel.Token);
            var ready=true;
            foreach(var card in _cards) {
                var offset=completed;
                _status.Text=card.DisplayName;
                var progress=new Progress<ManagedModelDownloadProgress>(p=>{
                    _progress.Value=100d*(offset+p.DownloadedBytes)/Math.Max(1,total);
                    if(_rows.TryGetValue(card.ModelArtifactId+"/"+p.FileName,out var row))row.Text=(p.Stage=="verifying"?"… ":"↓ ")+p.FileName;
                    _status.Text=$"{card.DisplayName} · {(offset+p.DownloadedBytes)/1048576d:F0}/{total/1048576d:F0} MB · {p.BytesPerSecond/1048576d:F1} MB/s";
                });
                var checkedCard=await acquisition.VerifyAsync(card.ModelArtifactId,progress,_cancel.Token);
                if(checkedCard.Status!=ManagedModelStatuses.Installed && download)checkedCard=await acquisition.DownloadAsync(card.ModelArtifactId,progress,_cancel.Token);
                ready &= checkedCard.Status==ManagedModelStatuses.Installed;completed+=card.TotalBytes;
                if(checkedCard.Status==ManagedModelStatuses.Installed)foreach(var f in card.Files)_rows[card.ModelArtifactId+"/"+f.RelativePath].Text="✓ "+f.RelativePath;
            }
            if(ready){_status.Text=_l("SongClip.Preparing");if(_acquisitionOnly){}else if(_model==InfiniteTalkRunner.ModelId)await InfiniteTalkComponents.PrepareAsync(_root,_connections,_cancel.Token,m=>_status.Text=m);else if(_model==GeekatplayRunner.ModelId)await GeekatplayComponents.PrepareAsync(_root,_connections,_cancel.Token,m=>_status.Text=m);else if(_montage)await H3MontageComponents.PrepareAsync(_root,_connections,_cancel.Token,m=>_status.Text=m);else if(_model=="h3_music_context")await H3MusicComponents.PrepareAsync(_root,_connections,_cancel.Token,m=>_status.Text=m);else await GlitchframeComponents.PrepareTreesAsync(_cards,_cancel.Token,repair:true);_cancel.Token.ThrowIfCancellationRequested();_progress.Value=100;DialogResult=true;}
            else _status.Text=_l("SongClip.DownloadRequired");
        }
        catch(OperationCanceledException){_status.Text=_l("SongClip.Canceled");}
        catch(Exception error){_status.Text=error.Message;}
        finally{_busy=false;_download.IsEnabled=true;_stop.IsEnabled=false;_cancel.Dispose();_cancel=null;}
    }
}
