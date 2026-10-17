using System.IO;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using MessageBox=System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class FileMetadataWindow
{
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if(_busy)return;
        var additions=new List<Entry>();
        foreach(var path in paths)
        {
            try
            {
                var full=Path.GetFullPath(path);
                if(!File.Exists(full)||_entries.Any(e=>e.Path.Equals(full,StringComparison.OrdinalIgnoreCase)))continue;
                var entry=new Entry(full){Status=_l("Metadata.Reading")};_entries.Add(entry);additions.Add(entry);
            }
            catch(Exception error)when(error is IOException or ArgumentException or UnauthorizedAccessException){_status.Text=_l("Metadata.InvalidPath");}
        }
        if(additions.Count==0)return;
        _busy=true;_work=new();RefreshActions();_status.Text=_l("Metadata.Reading");
        try
        {
            await Task.Run(()=>Parallel.ForEachAsync(additions,new ParallelOptions{MaxDegreeOfParallelism=2,CancellationToken=_work.Token},async(entry,token)=>
            {
                var snapshot=await _reader.ReadAsync(entry.Path,token);
                await Dispatcher.InvokeAsync(()=>{if(_closed)return;entry.Snapshot=snapshot;entry.Status=snapshot.Error is null?_l("Metadata.Ready"):Explain(snapshot.Error);});
            }));
            _status.Text=_l("Metadata.Ready");
        }
        catch(OperationCanceledException){foreach(var entry in additions.Where(e=>e.Snapshot is null))entry.Status=_l("Metadata.Canceled");_status.Text=_l("Metadata.Canceled");}
        finally
        {
            _busy=false;_work.Dispose();_work=null;
            if(!_closed){if(_list.SelectedItems.Count==0)_list.SelectedItem=additions[0];RefreshActions();if(!Dirty)RefreshDetails();}
        }
    }
    private async Task SelectionChangedAsync()
    {
        if(_changingSelection||_closed)return;
        var next=_list.SelectedItems.Cast<Entry>().ToArray();
        if(Dirty)
        {
            var answer=_confirm(_l("Metadata.SaveBeforeChange"),MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
            if(answer==MessageBoxResult.Cancel||answer==MessageBoxResult.Yes&&!await SaveCurrentAsync())
            {
                _changingSelection=true;_list.SelectedItems.Clear();foreach(var entry in _shown)_list.SelectedItems.Add(entry);_changingSelection=false;return;
            }
        }
        _shown=next;_cover=null;RefreshDetails();RefreshActions();
    }
    private void RemoveSelected()
    {
        if(Dirty&&_confirm(_l("Metadata.Discard"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        _cover=null;_rows.Clear();_changingSelection=true;
        foreach(var entry in _shown)_entries.Remove(entry);
        _shown=[];_list.SelectedItems.Clear();_changingSelection=false;RefreshDetails();RefreshActions();
    }
    private async Task<bool> SaveCurrentAsync()
    {
        if(_busy||!Dirty)return !Dirty;
        var values=_rows.Where(r=>r.Touched&&r.Apply.IsChecked==true).ToDictionary(r=>r.Id,r=>r.Text.Text);
        var details=string.Join("\n",_shown.Select(e=>Path.GetFileName(e.Path)))+"\n\n"+
            string.Join(", ",values.Keys.Select(k=>_l("Metadata.Field."+k)))+(_cover is null?"":"\n"+_l("Metadata.ReplaceCover")+" · "+_cover.SourceName);
        if(_confirm(_l("Metadata.SaveWarning")+"\n\n"+details,MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK)return false;
        _busy=_saving=true;_work=new();RefreshActions();var success=true;var completed=new HashSet<Entry>();
        try
        {
            foreach(var entry in _shown)
            {
                _work.Token.ThrowIfCancellationRequested();entry.Status=_l("Metadata.Saving");
                if(entry.Snapshot is null){entry.Status=_l("Metadata.ReadFailed");success=false;completed.Add(entry);continue;}
                var result=await Task.Run(()=>_editor.SaveAsync(entry.Snapshot,new(values,_cover),_work.Token));
                entry.Status=result.Error is null?_l(result.Saved?"Metadata.Saved":"Metadata.Unchanged"):Explain(result.Error);
                completed.Add(entry);
                if(result.Error is not null){success=false;continue;}
                entry.Snapshot=await Task.Run(()=>_reader.ReadAsync(entry.Path,CancellationToken.None));
            }
            if(success){_cover=null;RefreshDetails();}_status.Text=_l(success?"Metadata.Saved":"Metadata.Partial");return success;
        }
        catch(OperationCanceledException){foreach(var entry in _shown.Where(e=>!completed.Contains(e)))entry.Status=_l("Metadata.Canceled");_status.Text=_l("Metadata.Canceled");return false;}
        finally{_busy=_saving=false;_work.Dispose();_work=null;RefreshActions();}
    }
    private async Task ChooseCoverAsync()
    {
        if(!_replace.IsEnabled)return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter="JPEG / PNG|*.jpg;*.jpeg;*.png",CheckFileExists=true,Title=_l("Metadata.ReplaceCover")};
        if(dialog.ShowDialog(this)!=true)return;
        if(new FileInfo(dialog.FileName).Length>16*1024*1024)throw new InvalidDataException("Metadata.InvalidImage");
        var bytes=await File.ReadAllBytesAsync(dialog.FileName);FileMetadataPreview.ValidateImage(bytes);
        using var codec=SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
        var mime=codec!.EncodedFormat==SkiaSharp.SKEncodedImageFormat.Jpeg?"image/jpeg":"image/png";
        var source=new FileMetadataCoverWindow(this,bytes,mime,_l);
        if(source.ShowDialog()!=true||source.Result is null)return;
        _cover=source.Result;_status.Text=_l("Metadata.CoverPending")+" · "+_cover.SourceName;RefreshActions();
    }
}
