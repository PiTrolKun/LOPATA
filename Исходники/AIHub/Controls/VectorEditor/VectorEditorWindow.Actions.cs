using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;
using Clipboard = System.Windows.Clipboard;

namespace AIHub.Controls.VectorEditor;

internal sealed partial class VectorEditorWindow
{
    private double _brushRadius = 90, _brushStrength = .65;
    private string _brushMode = "plastic";
    private async Task Shortcut(string command)
    {
        switch(command)
        {
            case "save":SaveProject(false);break;
            case "saveAs":SaveProject(true);break;
            case "undo":_project=_history.Undo(_project);Changed();RefreshTabs();RefreshLibrary();await LoadSheet();break;
            case "redo":_project=_history.Redo(_project);Changed();RefreshTabs();RefreshLibrary();await LoadSheet();break;
            case "copy":case "cut":
                var fragment=await _browser.CallAsync("command",new{name="fragment"});
                if(fragment.ValueKind!=System.Text.Json.JsonValueKind.String)return;
                var svg=fragment.GetString()!;_ = VectorSvg.Parse(svg);Clipboard.SetText(svg);
                if(command=="cut")await Command("delete");break;
            case "paste":
                if(Clipboard.ContainsText())await InsertSvgAsync(Clipboard.GetText());
                else if(Clipboard.ContainsFileDropList())foreach(var path in Clipboard.GetFileDropList().Cast<string>())if(Path.GetExtension(path).Equals(".svg",StringComparison.OrdinalIgnoreCase))await InsertSvgAsync(ReadSvg(path));break;
            default:await Command(command);break;
        }
    }
    internal static string ReadSvg(string path)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(Path.GetFullPath(path));
        if(new FileInfo(path).Length>32L*1024*1024)throw new InvalidDataException("Vector.Error.Complexity");
        var svg=File.ReadAllText(path,new System.Text.UTF8Encoding(false,true));_ = VectorSvg.Parse(svg);return svg;
    }
    internal async Task AddSheetAsync(string svg,string name)
    {
        var normalized=VectorEditorSvg.AsSheet(svg);_history.Record(_project);
        var sheet=new VectorSheet(Guid.NewGuid().ToString("N"),name,normalized);_project.Sheets.Add(sheet);_project.ActiveSheetId=sheet.Id;
        Changed();RefreshTabs();await LoadSheet(true);
    }
    private async Task InsertSvgAsync(string svg,double x=0,double y=0)
    {
        var updated=VectorEditorSvg.Insert(Active.Svg,svg,x,y);_history.Record(_project);
        var index=_project.Sheets.FindIndex(s=>s.Id==Active.Id);_project.Sheets[index]=Active with{Svg=updated};
        Changed();await LoadSheet();
    }
    private async Task OpenFilesAsync()
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter=T("OpenFilter"),Multiselect=true};
        if(dialog.ShowDialog(this)!=true)return;
        foreach(var path in dialog.FileNames)
        {
            if(Path.GetExtension(path).Equals(".lopataSVG",StringComparison.OrdinalIgnoreCase))
            {
                var project=await Task.Run(()=>VectorProjectArchive.Load(path));
                OpenProjectWindow?.Invoke(project,path);
            }
            else await AddSheetAsync(ReadSvg(path),Path.GetFileNameWithoutExtension(path));
        }
    }
    internal event Action<VectorProject,string?>? OpenProjectWindow;
    private Task InsertLibraryAsync()=>_library.SelectedItem is System.Windows.Controls.ListBoxItem item&&item.Tag is string id
        ?InsertSvgAsync(_project.Objects.First(x=>x.Id==id).Svg):Task.CompletedTask;
    private Task PreviewLibraryAsync()
    {
        if(_library.SelectedItem is not System.Windows.Controls.ListBoxItem item||item.Tag is not string id)return Task.CompletedTask;
        var source=_project.Objects.First(x=>x.Id==id);var box=VectorSvg.Parse(source.Svg).Root!.Attribute("viewBox")!.Value.Split([' ',',','\r','\n','\t'],StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,CultureInfo.InvariantCulture)).ToArray();
        var window=new Window{Title=source.Name,Owner=this,Width=Math.Min(950,SystemParameters.WorkArea.Width-40),Height=Math.Min(760,SystemParameters.WorkArea.Height-40),
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new VectorImageViewport(new System.Windows.Controls.Grid(),_l("Vector.ZoomHint"),()=>source.Svg,box[2]/box[3],_l("Vector.ZoomError"))};
        window.Resources.MergedDictionaries.Add(Resources);window.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");window.Show();return Task.CompletedTask;
    }
    private bool SaveProject(bool saveAs)
    {
        var path=_path;
        if(saveAs||string.IsNullOrEmpty(path))
        {
            var dialog=new Microsoft.Win32.SaveFileDialog{Filter="LOPATA SVG (*.lopataSVG)|*.lopataSVG",DefaultExt=".lopataSVG",FileName=_project.Name,AddExtension=true};
            if(dialog.ShowDialog(this)!=true)return false;path=dialog.FileName;
        }
        try
        {
            var saved=_project with{Name=Path.GetFileNameWithoutExtension(path!)};
            VectorProjectArchive.Save(saved,path!);_project=saved;_path=path;_dirty=false;_autosave.Stop();VectorProjectRecovery.Remove(_project.Id);
            UpdateTitle();_status.Text=T("Saved");return true;
        }
        catch(Exception e){Error(e);return false;}
    }
    private async Task NewSheetAsync()
    {
        var name=Ask(T("NewSheet"),T("Sheet")+" "+(_project.Sheets.Count+1));if(string.IsNullOrWhiteSpace(name))return;
        _history.Record(_project);var sheet=new VectorSheet(Guid.NewGuid().ToString("N"),name,VectorEditorSvg.Blank(1000,1000));
        _project.Sheets.Add(sheet);_project.ActiveSheetId=sheet.Id;Changed();RefreshTabs();await LoadSheet(true);
    }
    private async Task CanvasAsync()
    {
        var box=VectorSvg.Parse(Active.Svg).Root!.Attribute("viewBox")!.Value;
        var text=Ask(T("CanvasDimensions"),box.Split(' ').Skip(2).Aggregate((a,b)=>a+" × "+b));if(text is null)return;
        var parts=text.Split(['×','x','X',';',','],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length!=2)throw new InvalidDataException("Vector.Editor.Error.Number");
        await Command("canvas",new{width=Numeric(parts[0]),height=Numeric(parts[1])});
    }
    private async Task PlasticAsync(string mode)
    {
        if(_selection.Length==0)throw new InvalidDataException("Vector.Editor.Error.PlasticSelection");
        var svg=VectorPathEditing.Prepare(Active.Svg,_selection);_history.Record(_project);
        var selected=_selection.ToArray();var index=_project.Sheets.FindIndex(s=>s.Id==Active.Id);_project.Sheets[index]=Active with{Svg=svg};Changed();
        await LoadSheet();await _browser.CallAsync("select",selected);_brushMode=mode;await UpdateBrush();
        _status.Text=T("PlasticActive");
    }
    private async Task UpdateBrush()=>await _browser.CallAsync("plastic",new{mode=_brushMode,radius=_brushRadius,strength=_brushStrength});
    private async Task RecoverAsync()
    {
        var files=VectorProjectRecovery.Available();
        if(files.Length==0){_status.Text=T("NoRecovery");return;}
        var list=new System.Windows.Controls.ListBox{MinWidth=400,MaxHeight=400,Margin=new(12)};
        foreach(var file in files){
            try{var info=VectorProjectArchive.Describe(file);list.Items.Add(new System.Windows.Controls.ListBoxItem{Content=info.Name+" · "+info.ModifiedUtc.ToLocalTime().ToString("g"),Tag=file});}
            catch(Exception error)when(error is IOException or System.Text.Json.JsonException){OwnedProcessRegistry.Log("vector_recovery_unavailable","VectorEditor",detail:error.GetType().Name);}
        }
        if(list.Items.Count==0){_status.Text=T("NoRecovery");return;}list.SelectedIndex=0;
        var open=new System.Windows.Controls.Button{Content=T("Open"),Margin=new(12),Padding=new(14,7,14,7),IsDefault=true};
        var layout=new System.Windows.Controls.StackPanel();layout.Children.Add(list);layout.Children.Add(open);
        var dialog=new Window{Owner=this,Title=T("Recovery"),Content=layout,SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        dialog.Resources.MergedDictionaries.Add(Resources);dialog.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");dialog.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");open.Click+=(_,_)=>dialog.DialogResult=true;
        if(dialog.ShowDialog()==true&&list.SelectedItem is System.Windows.Controls.ListBoxItem item&&item.Tag is string path){
            var project=await Task.Run(()=>VectorProjectArchive.Load(path));OpenProjectWindow?.Invoke(project,null);
        }
    }
    private async Task ExportAsync()
    {
        var dialog=new Microsoft.Win32.SaveFileDialog{Filter="SVG (*.svg)|*.svg|PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|WebP (*.webp)|*.webp",FileName=Active.Name+".svg",AddExtension=true};
        if(dialog.ShowDialog(this)!=true)return;
        var svg=VectorEditorSvg.Export(Active.Svg,_project.CreatedUtc);var path=Path.GetFullPath(dialog.FileName);
        if(path.Equals(_path,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Vector.Error.ExportDestination");
        if(Path.GetExtension(path).Equals(".svg",StringComparison.OrdinalIgnoreCase))
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{await File.WriteAllTextAsync(temp,svg,new System.Text.UTF8Encoding(false));File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        }
        else
        {
            var box=VectorSvg.Parse(svg).Root!.Attribute("viewBox")!.Value.Split([' ',',','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(s=>double.Parse(s,CultureInfo.InvariantCulture)).ToArray();
            var settings=new VectorRasterExportWindow(this,_l,(int)Math.Clamp(box[2],1,int.MaxValue),(int)Math.Clamp(box[3],1,int.MaxValue));
            if(settings.ShowDialog()!=true||settings.Value is null)return;
            var format=Path.GetExtension(path).ToLowerInvariant() switch{".jpg" or ".jpeg"=>"jpeg",".png"=>"png",".webp"=>"webp",_=>throw new InvalidDataException("Vector.Error.ExportDestination")};
            if(settings.Value.Format!=format)path=Path.ChangeExtension(path,settings.Value.Format=="jpeg"?"jpg":settings.Value.Format);
            await VectorRasterExport.SaveSourceAsync(svg,VectorEditorSvg.Facts([svg],_project.CreatedUtc),path,settings.Value,CancellationToken.None);
        }
        _status.Text=T("Exported");
    }
}
