using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.ImageLora;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

internal sealed partial class ImageLoraWindow
{
    private async Task RunSearchAsync(string query)
    {
        if(_busy) return; _summaryCancel.Cancel(); _summaryCancel=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); SetBusy(true); _status.Text=T("Searching");
        var token=_operation.Token; var found=0;
        try {
            using var search=new ImageLoraSearch();
            await search.SearchAsync(_model,query,new Progress<ImageLoraDefinition>(d=>{
                if(token.IsCancellationRequested) return;
                try {
                    if(!ImageLoraCatalog.Bundled.Any(b=>b.Id==d.Id)) ImageLoraStore.Register(d);
                    AddCard(d); found++; UpdateCount(); Filter();
                    if(!ImageLoraCatalog.Bundled.Any(b=>b.Id==d.Id)) _ = DescribeAsync(d);
                } catch(Exception e) { _status.Text=_l(e.Message); }
            }),new Progress<string>(s=>{ if(!token.IsCancellationRequested) _status.Text=s; }),token);
            _status.Text=found==0?T("NoResults"):T("SearchFinished");
        } catch(OperationCanceledException) { _status.Text=T("Cancelled"); }
        catch(Exception e) { _status.Text=T("Failed")+" "+_l(e.Message); }
        finally { _operation.Dispose(); _operation=null; SetBusy(false); if(_closing) Close(); }
    }
    private async Task DescribeAsync(ImageLoraDefinition d)
    {
        var token=_summaryCancel.Token; var entered=false;
        try {
            await _descriptionGate.WaitAsync(token); entered=true;
            var result=await ImageLoraDescriptionService.CreateAsync(d,_root,_ru,_=>{},token);
            if(!token.IsCancellationRequested && _descriptions.TryGetValue(d.Id,out var text)) text.Text=result.Text;
        } catch(OperationCanceledException) { }
        catch(Exception e) { if(!token.IsCancellationRequested) _status.Text=T("DescriptionFallback")+" "+_l(e.Message); }
        finally { if(entered) _descriptionGate.Release(); }
    }
    private async Task CollectionAsync(ImageLoraDefinition collection)
    {
        if(_busy) return;
        var existing=_definitions.Values.Where(d=>d.Supported && d.Repository==collection.Repository).ToArray();
        // A thematic search can contain only part of a discovered collection.
        // Only the fully bundled file list can be offered without re-inspection.
        if(existing.Length>0 && ImageLoraCatalog.Bundled.Any(d=>d.Supported && d.Repository==collection.Repository)) { ChooseSet(existing); return; }
        _operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); SetBusy(true); var token=_operation.Token;
        var found=new List<ImageLoraDefinition>(); _status.Text=T("Searching");
        try {
            using var search=new ImageLoraSearch();
            await search.InspectAsync(collection.Repository,_model,d=>{
                ImageLoraStore.Register(d); found.Add(d);
            },new Progress<string>(s=>_status.Text=s),token);
            foreach(var d in found) { AddCard(d); if(!ImageLoraCatalog.Bundled.Any(b=>b.Id==d.Id)) _ = DescribeAsync(d); }
            _status.Text=found.Count==0?T("NoResults"):T("SearchFinished");
        } catch(OperationCanceledException) { _status.Text=T("Cancelled"); }
        catch(Exception e) { _status.Text=T("Failed")+" "+_l(e.Message); }
        finally { _operation.Dispose();_operation=null; SetBusy(false);UpdateCount();if(_closing)Close(); }
        if(!_closing && found.Count>0) ChooseSet(found.ToArray());
    }
    private void ChooseSet(ImageLoraDefinition[] definitions)
    {
        if(_busy || definitions.Length==0) return;
        var selected=new Dictionary<string,CheckBox>(); var panel=new StackPanel { Margin=new(18) };
        var window=new Window { Owner=this }; MusicWishUi.PrepareWindow(window,T("ChooseFiles"),"Generation.Lora.Set",760); window.Height=580;
        var dock=new DockPanel { Margin=new(15) }; var footer=new WrapPanel(); DockPanel.SetDock(footer,Dock.Bottom); dock.Children.Add(footer);
        footer.Children.Add(MusicWishUi.Button(T("Download"),"Generation.Lora.Set.Download",()=>window.DialogResult=true));
        footer.Children.Add(MusicWishUi.Button(T("Cancel"),"Generation.Lora.Set.Cancel",window.Close));
        panel.Children.Add(MusicWishUi.Text(T("SetHint")));
        var total=MusicWishUi.Text("");
        void Total()=>total.Text=T("DownloadSize")+" "+(definitions.Where(d=>selected.TryGetValue(d.Id,out var c)&&c.IsChecked==true&&!ImageLoraAssets.Ready(d,_root)).Sum(d=>d.Bytes)/1048576d).ToString("0.#")+" MB";
        panel.Children.Add(MusicWishUi.Check(T("SelectAll"),false,value=>{foreach(var c in selected.Values)c.IsChecked=value;Total();}));
        foreach(var d in definitions.DistinctBy(d=>d.Id)) {
            var box=MusicWishUi.Check(d.Name+$" · {d.Bytes/1048576d:0.#} MB",false,_=>Total()); selected[d.Id]=box; panel.Children.Add(box);
        }
        panel.Children.Add(total); Total(); dock.Children.Add(new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto }); window.Content=dock;
        if(window.ShowDialog()==true) _ = DownloadAsync(definitions.Where(d=>selected[d.Id].IsChecked==true).ToArray());
    }
    private async Task DownloadAsync(ImageLoraDefinition[] definitions)
    {
        if(_busy || definitions.Length==0) return; _operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); SetBusy(true);
        try {
            _summaryCancel.Cancel();
            await _descriptionGate.WaitAsync(_operation.Token); _descriptionGate.Release();
            using var auxiliary=ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            await ImageLoraAssets.PrepareAsync(_root,definitions,true,new Progress<AIHub.Models.ManagedModelDownloadProgress>(p=>{
                _status.Text=p.FileName+" · "+p.Stage+$" · {p.DownloadedBytes/1048576d:0.#}/{p.TotalBytes/1048576d:0.#} MB";
                _progress.IsIndeterminate=p.TotalBytes<=0; _progress.Value=p.TotalBytes>0?p.DownloadedBytes*100d/p.TotalBytes:0;
            }),_operation.Token); _status.Text=T("Downloaded");
        } catch(OperationCanceledException) { _status.Text=T("Cancelled"); }
        catch(Exception e) { _status.Text=T("Failed")+" "+_l(e.Message); }
        finally { _operation.Dispose();_operation=null;SetBusy(false);RefreshReady();if(_closing)Close(); }
    }
    private async Task DeleteAsync(ImageLoraDefinition d)
    {
        if(_busy || MessageBox.Show(this,T("DeleteConfirm")+"\n"+d.Name,T("Title"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        SetBusy(true);
        try { await ImageLoraAssets.RemoveAsync(d,_root,_lifetime.Token);_status.Text=T("Removed"); }
        catch(Exception e) { _status.Text=T("Failed")+" "+_l(e.Message); }
        finally { SetBusy(false);RefreshReady();if(_closing)Close(); }
    }
    private async Task ApplyAsync()
    {
        if(_busy) return;
        try {
            SetBusy(true); _summaryCancel.Cancel();
            try { await _descriptionGate.WaitAsync(_lifetime.Token); _descriptionGate.Release(); }
            finally { SetBusy(false); }
            if(_closing || _lifetime.IsCancellationRequested) { if(_closing) Close(); return; }
            var uses=ImageLoraCatalog.Capture(Selection,_model);
            var missing=uses.Select(u=>ImageLoraCatalog.Find(u.Id)).Where(d=>!ImageLoraAssets.Ready(d,_root)).ToArray();
            if(missing.Length>0) {
                if(MessageBox.Show(this,T("PrepareSelected"),T("Title"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                await DownloadAsync(missing); if(missing.Any(d=>!ImageLoraAssets.Ready(d,_root)) || _closing)return;
            }
            DialogResult=true;
        }
        catch(OperationCanceledException) { if(_closing) Close(); }
        catch(Exception e) { _status.Text=T("Failed")+" "+_l(e.Message); }
    }
}
