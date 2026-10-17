using DataGrid=System.Windows.Controls.DataGrid;
using DataFormats=System.Windows.DataFormats;
using DragDropEffects=System.Windows.DragDropEffects;
using GridSplitter=System.Windows.Controls.GridSplitter;
using Control=System.Windows.Controls.Control;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ListBox=System.Windows.Controls.ListBox;
using TextBox=System.Windows.Controls.TextBox;
using Orientation=System.Windows.Controls.Orientation;
using MessageBox=System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class FileMetadataWindow:Window
{
    private readonly Func<string,string> _l;
    private readonly Func<string,MessageBoxButton,MessageBoxImage,MessageBoxResult> _confirm;
    private readonly FileMetadataReader _reader=new();
    private readonly FileMetadataEditor _editor=new();
    private readonly ObservableCollection<Entry> _entries=[];
    private readonly ListBox _list=new(){SelectionMode=System.Windows.Controls.SelectionMode.Extended,DisplayMemberPath=nameof(Entry.Display)};
    private readonly StackPanel _details=new();
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(4,6,4,6)};
    private readonly TextBox _search=new(){MinWidth=180,MaxWidth=450,Margin=new(8,4,0,4)};
    private readonly System.Windows.Controls.ProgressBar _progress=new(){Height=4,Visibility=Visibility.Collapsed};
    private readonly List<Row> _rows=[];
    private readonly List<(DataGrid Grid,IReadOnlyList<FileMetadataField> Fields)> _grids=[];
    private readonly Button _save,_remove,_replace,_cancel;
    private Entry[] _shown=[];
    private CancellationTokenSource? _work,_preview;
    private bool _busy,_saving,_closed,_changingSelection;
    private FileMetadataCover? _cover;
    private bool Dirty=>_cover is not null||_rows.Any(r=>r.Touched&&r.Apply.IsChecked==true);
    public FileMetadataWindow(Window? owner,Func<string,string> l):this(owner,l,null){}
    internal FileMetadataWindow(Window? owner,Func<string,string> l,
        Func<string,MessageBoxButton,MessageBoxImage,MessageBoxResult>? confirm)
    {
        _l=l;_confirm=confirm??((message,buttons,icon)=>MessageBox.Show(this,message,Title,buttons,icon));if(owner is not null)Owner=owner;
        MusicWishUi.PrepareWindow(this,l("Metadata.Title"),"Metadata.Window",1180);Height=850;MinWidth=720;MinHeight=500;
        if(owner is not null)Resources.MergedDictionaries.Insert(0,owner.Resources);
        var root=new DockPanel{Margin=new(16)};
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(MusicWishUi.Text(l("Metadata.Title"),true));top.Children.Add(MusicWishUi.Text(l("Metadata.Hint")));
        var tools=new WrapPanel{Orientation=Orientation.Horizontal};
        tools.Children.Add(Action("Add",async()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Multiselect=true,CheckFileExists=true,Title=l("Metadata.Add")};if(dialog.ShowDialog(this)==true)await AddFilesAsync(dialog.FileNames);}));
        _remove=Action("Remove",()=>{RemoveSelected();return Task.CompletedTask;});tools.Children.Add(_remove);
        _save=Action("Save",async()=>{_ = await SaveCurrentAsync();});tools.Children.Add(_save);
        _replace=Action("ReplaceCover",ChooseCoverAsync);tools.Children.Add(_replace);
        _cancel=Action("Cancel",()=>{_work?.Cancel();_preview?.Cancel();return Task.CompletedTask;});tools.Children.Add(_cancel);
        _search.ToolTip=l("Metadata.Search");AutomationProperties.SetName(_search,l("Metadata.Search"));
        AutomationProperties.SetAutomationId(_search,"Metadata.Search");_search.TextChanged+=(_,_)=>Filter();tools.Children.Add(_search);
        top.Children.Add(tools);top.Children.Add(_progress);
        DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);
        var columns=new Grid();columns.ColumnDefinitions.Add(new(){Width=new(280),MinWidth=170});columns.ColumnDefinitions.Add(new(){Width=new(8)});columns.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star),MinWidth=360});
        _list.ItemsSource=_entries;AutomationProperties.SetAutomationId(_list,"Metadata.Files");
        _list.SetResourceReference(Control.BackgroundProperty,"InputBrush");_list.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list,ScrollBarVisibility.Disabled);
        var itemStyle=new Style(typeof(ListBoxItem));itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,System.Windows.HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(ToolTipProperty,new System.Windows.Data.Binding(nameof(Entry.Display))));_list.ItemContainerStyle=itemStyle;
        var labelFactory=new FrameworkElementFactory(typeof(TextBlock));labelFactory.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(Entry.Display)));
        labelFactory.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);_list.DisplayMemberPath="";_list.ItemTemplate=new DataTemplate{VisualTree=labelFactory};
        _list.SelectionChanged+=async(_,_)=>await SelectionChangedAsync();columns.Children.Add(_list);
        var splitter=new GridSplitter{Width=5,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Stretch};Grid.SetColumn(splitter,1);columns.Children.Add(splitter);
        var scroll=new ScrollViewer{Content=_details,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new(8,0,0,0)};Grid.SetColumn(scroll,2);columns.Children.Add(scroll);root.Children.Add(columns);
        Content=root;AllowDrop=true;
        DragOver+=(_,e)=>{e.Effects=!_busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        Drop+=async(_,e)=>{if(!_busy&&e.Data.GetData(DataFormats.FileDrop) is string[] paths)await AddFilesAsync(paths);};
        Closing+=OnClosing;Closed+=(_,_)=>{_closed=true;_work?.Cancel();_preview?.Cancel();};
        RefreshDetails();RefreshActions();
    }
    private Button Action(string id,Func<Task> action)
    {
        var button=MusicWishUi.Button(_l("Metadata."+id),"Metadata."+id,()=>{});
        button.Click+=async(_,_)=>{try{await action();}catch(OperationCanceledException){if(!_closed)_status.Text=_l("Metadata.Canceled");}
            catch(Exception error){if(!_closed)_status.Text=Explain(error.Message);}};
        return button;
    }
    private string Explain(string value)=>value.StartsWith("Metadata.",StringComparison.Ordinal)?_l(value):_l("Metadata.ReadFailed");
    private void RefreshActions()
    {
        _details.IsEnabled=!_saving;_save.IsEnabled=!_busy&&Dirty;_remove.IsEnabled=!_busy&&_shown.Length>0;
        _replace.IsEnabled=!_busy&&_shown.Length>0&&_shown.All(e=>e.Snapshot?.CanReplaceCover==true);
        _replace.ToolTip=_l("Metadata.CoverReadOnly");
        _cancel.IsEnabled=_busy;_list.IsEnabled=!_saving;_progress.Visibility=_busy?Visibility.Visible:Visibility.Collapsed;_progress.IsIndeterminate=_busy;
    }
    private sealed class Entry(string path):INotifyPropertyChanged
    {
        public string Path{get;}=path;
        public FileMetadataSnapshot? Snapshot{get;set;}
        private string _status="";
        public string Display=>System.IO.Path.GetFileName(Path)+(_status.Length==0?"":"\n"+_status);
        public string Status{set{_status=value;Changed();}}
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed()=>PropertyChanged?.Invoke(this,new(nameof(Display)));
    }
    private sealed class Row(string id,CheckBox apply,TextBox text,FrameworkElement element)
    {
        public string Id{get;}=id;public CheckBox Apply{get;}=apply;public TextBox Text{get;}=text;
        public FrameworkElement Element{get;}=element;public bool Touched{get;set;}
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_saving){_work?.Cancel();e.Cancel=true;_status.Text=_l("Metadata.CancelPending");return;}
        if(Dirty&&_confirm(_l("Metadata.Discard"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)e.Cancel=true;
        if(!e.Cancel)_work?.Cancel();
    }
}
