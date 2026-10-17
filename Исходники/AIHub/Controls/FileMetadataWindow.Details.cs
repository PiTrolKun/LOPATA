using DataGrid=System.Windows.Controls.DataGrid;
using Binding=System.Windows.Data.Binding;
using Control=System.Windows.Controls.Control;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using TextBox=System.Windows.Controls.TextBox;
using Image=System.Windows.Controls.Image;

namespace AIHub.Controls;

public sealed partial class FileMetadataWindow
{
    private void RefreshDetails()
    {
        _preview?.Cancel();_preview?.Dispose();_preview=new();
        _rows.Clear();_grids.Clear();_details.Children.Clear();
        if(_shown.Length==0){_details.Children.Add(MusicWishUi.Text(_l("Metadata.Empty")));return;}
        var snapshots=_shown.Where(e=>e.Snapshot is not null).Select(e=>e.Snapshot!).ToArray();
        _details.Children.Add(MusicWishUi.Text(string.Join(" · ",_shown.Take(3).Select(e=>Path.GetFileName(e.Path)))+(_shown.Length>3?" …":""),true));
        foreach(var snapshot in snapshots.Where(s=>s.Error is not null))_details.Children.Add(MusicWishUi.Text(Path.GetFileName(snapshot.Path)+": "+Explain(snapshot.Error!)));
        foreach(var snapshot in snapshots.Where(s=>s.Notice=="Metadata.Signed"))_details.Children.Add(MusicWishUi.Text(Path.GetFileName(snapshot.Path)+": "+_l(snapshot.Notice!)));
        var preview=new StackPanel();_details.Children.Add(preview);
        _=RenderPreviewsAsync(preview,snapshots,_preview.Token);
        var basic=snapshots.SelectMany(s=>s.Fields.Where(f=>f.Group=="Basic")).GroupBy(f=>f.Id).ToArray();
        if(basic.Length>0)_details.Children.Add(MusicWishUi.Text(_l("Metadata.Group.Basic"),true));
        foreach(var fields in basic)
        {
            var available=fields.ToArray();var first=available[0];var values=available.Select(f=>f.Value).Distinct().ToArray();
            var mixed=values.Length!=1||available.Length!=snapshots.Length;var allowed=available.Count(f=>f.Editable);
            var row=new Grid{Margin=new(0,3,0,7)};row.ColumnDefinitions.Add(new(){Width=new(28)});row.ColumnDefinitions.Add(new(){Width=new(155)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
            var apply=new CheckBox{IsEnabled=false,VerticalAlignment=VerticalAlignment.Center,ToolTip=_l("Metadata.ApplyField")};row.Children.Add(apply);
            AutomationProperties.SetName(apply,_l("Metadata.ApplyField")+" · "+_l(first.Name));
            var label=MusicWishUi.Text(_l(first.Name)+(allowed>0?$" ({allowed}/{_shown.Length})":""));Grid.SetColumn(label,1);row.Children.Add(label);
            var box=new TextBox{Text=mixed?_l("Metadata.Different"):values[0],IsReadOnly=allowed==0,AcceptsReturn=true,
                TextWrapping=TextWrapping.Wrap,MinHeight=30,MaxHeight=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxLength=64000};
            FileMetadataScroll.Attach(box);
            box.SetResourceReference(Control.BackgroundProperty,"InputBrush");box.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
            Grid.SetColumn(box,2);row.Children.Add(box);AutomationProperties.SetAutomationId(box,"Metadata.Field."+fields.Key[5..]);
            AutomationProperties.SetName(box,_l(first.Name));
            if(allowed==0)box.ToolTip=_l(first.Reason??"Metadata.ReadOnly");
            var editor=new Row(fields.Key[5..],apply,box,row);_rows.Add(editor);
            box.TextChanged+=(_,_)=>{if(box.IsReadOnly)return;editor.Touched=true;apply.IsEnabled=true;apply.IsChecked=true;RefreshActions();};
            apply.Checked+=(_,_)=>RefreshActions();apply.Unchecked+=(_,_)=>RefreshActions();_details.Children.Add(row);
        }
        if(basic.Length>0&&basic.All(g=>g.All(f=>f.Value.Length==0)))_details.Children.Add(MusicWishUi.Text(_l("Metadata.EmptyFields")));
        foreach(var group in new[]{"Technical","Origin","Other","File"})
        {
            var fields=snapshots.SelectMany(s=>s.Fields.Where(f=>f.Group==group).Select(f=>f with{Name=(_shown.Length>1?Path.GetFileName(s.Path)+" · ":"")+f.Name})).ToArray();
            if(fields.Length==0)continue;
            var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,
                MaxHeight=320,MinHeight=90,SelectionUnit=DataGridSelectionUnit.FullRow,HeadersVisibility=DataGridHeadersVisibility.Column};
            FileMetadataScroll.Configure(grid);
            grid.SetResourceReference(Control.BackgroundProperty,"InputBrush");grid.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
            grid.SetResourceReference(DataGrid.RowBackgroundProperty,"PanelBrush");grid.SetResourceReference(Control.BorderBrushProperty,"LineBrush");
            var header=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("PanelBrush")));
            header.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("TextPrimaryBrush")));
            grid.ColumnHeaderStyle=header;
            grid.Columns.Add(new DataGridTextColumn{Header=_l("Metadata.Name"),Binding=new Binding(nameof(ViewRow.Name)),Width=new(2,DataGridLengthUnitType.Star)});
            grid.Columns.Add(new DataGridTextColumn{Header=_l("Metadata.Value"),Binding=new Binding(nameof(ViewRow.Value)),Width=new(3,DataGridLengthUnitType.Star)});
            grid.SizeChanged+=(_,e)=>{if(e.NewSize.Width<180)return;var width=e.NewSize.Width-24;
                grid.Columns[0].Width=new DataGridLength(width*.4);grid.Columns[1].Width=new DataGridLength(width*.6);};
            grid.MouseDoubleClick+=(_,_)=>{if(grid.SelectedItem is ViewRow value)ShowText(value.Field);};
            var expander=new Expander{Header=_l("Metadata.Group."+group)+" · "+fields.Length,Content=grid,IsExpanded=false,Margin=new(0,8,0,4)};
            _details.Children.Add(expander);_grids.Add((grid,fields));
        }
        _details.Children.Add(MusicWishUi.Text(_l("Metadata.ReadOnlyHint")));Filter();
    }
    private void Filter()
    {
        var query=_search.Text.Trim();
        foreach(var (grid,fields) in _grids)grid.ItemsSource=fields.Where(f=>query.Length==0||f.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||f.Value.Contains(query,StringComparison.OrdinalIgnoreCase))
            .Select(f=>new ViewRow(f.Name.StartsWith("Metadata.")?_l(f.Name):f.Name,f.Value.Length>2000?f.Value[..2000]+" …":f.Value,f)).ToArray();
        foreach(var row in _rows)row.Element.Visibility=query.Length==0||_l("Metadata.Field."+row.Id).Contains(query,StringComparison.OrdinalIgnoreCase)||row.Text.Text.Contains(query,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;
    }
    private void ShowText(FileMetadataField field)
    {
        var window=new Window{Owner=this};MusicWishUi.PrepareWindow(window,field.Name.StartsWith("Metadata.")?_l(field.Name):field.Name,"Metadata.FieldView",800);
        var box=new TextBox{Text=field.Value,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new(12)};
        box.SetResourceReference(Control.BackgroundProperty,"InputBrush");box.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");window.Content=box;window.Show();
    }
    private sealed record ViewRow(string Name,string Value,FileMetadataField Field);
    private async Task RenderPreviewsAsync(StackPanel panel,FileMetadataSnapshot[] snapshots,CancellationToken token)
    {
        try
        {
            var items=snapshots.SelectMany(s=>new[]{(File:s,Picture:(FileMetadataPicture?)null)}.Concat(s.Pictures.Select(p=>(File:s,Picture:(FileMetadataPicture?)p)))).ToArray();
            var wrap=new WrapPanel();panel.Children.Add(wrap);var position=0;
            async Task More()
            {
                var end=Math.Min(position+24,items.Length);
                while(position<end)
                {
                    token.ThrowIfCancellationRequested();var item=items[position++];
                    if(item.Picture is null&&item.File.Kind is not ("image" or "video"))continue;
                    var card=new StackPanel{Width=160,Margin=new(0,0,10,10)};wrap.Children.Add(card);
                    card.Children.Add(MusicWishUi.Text(Path.GetFileName(item.File.Path)+" · "+(item.Picture?.Name??_l(item.File.Kind=="video"?"Metadata.ThirdFrame":"Metadata.Preview"))));
                    byte[]? preview;
                    try{preview=await Task.Run(()=>item.Picture is null?FileMetadataPreview.ReadAsync(item.File,token):Task.FromResult<byte[]?>(FileMetadataPreview.Thumbnail(item.Picture.Bytes)),token);}
                    catch(Exception error)when(error is IOException or InvalidDataException or TimeoutException or InvalidOperationException){preview=null;}
                    token.ThrowIfCancellationRequested();
                    if(preview is null){card.Children.Add(MusicWishUi.Text(_l("Metadata.PreviewUnavailable")));continue;}
                    var image=new Image{Source=Bitmap(preview,240),Height=125,Stretch=Stretch.Uniform};
                    var open=new Button{Content=image,ToolTip=_l("Metadata.Enlarge")};
                    open.Click+=(_,_)=>{var viewer=new Window{Owner=this};MusicWishUi.PrepareWindow(viewer,_l("Metadata.Preview"),"Metadata.ImageView",900);viewer.Content=new Image{Source=Bitmap(item.Picture?.Bytes??preview,1800),Stretch=Stretch.Uniform};viewer.Show();};card.Children.Add(open);
                    if(item.Picture is { } picture)
                    {
                        var save=Action("ExportImage",async()=>
                        {
                            var ext=picture.Mime switch{"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>".bin"};
                            var dialog=new Microsoft.Win32.SaveFileDialog{FileName=Path.GetFileNameWithoutExtension(item.File.Path)+"-image"+ext,DefaultExt=ext,OverwritePrompt=true};
                            if(dialog.ShowDialog(this)==true)await FileMetadataImages.ExportAsync(picture,dialog.FileName,_entries.Select(e=>e.Path),CancellationToken.None);
                        });card.Children.Add(save);
                    }
                }
            }
            await More();
            if(position<items.Length)
            {
                var more=Action("MoreImages",async()=>{await More();});panel.Children.Add(more);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(!token.IsCancellationRequested&&!_closed)panel.Children.Add(MusicWishUi.Text(Explain(error.Message)));}
    }
    internal static BitmapSource Bitmap(byte[] bytes,int width)
    {
        using var stream=new MemoryStream(bytes);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=width;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
    }
}
