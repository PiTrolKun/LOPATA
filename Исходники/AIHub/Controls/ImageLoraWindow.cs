using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.ImageLora;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

internal sealed partial class ImageLoraWindow : Window
{
    private readonly string _root, _model;
    private readonly Func<string,string> _l;
    private readonly bool _ru;
    private readonly ImageLoraSelection _original;
    private readonly Dictionary<string, ImageLoraChoice> _choices;
    private readonly Dictionary<string, ImageLoraDefinition> _definitions = [];
    private readonly Dictionary<string, TextBlock> _descriptions = [];
    private readonly Dictionary<string, Border> _cards = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _status, _count;
    private readonly TextBox _query = new() { MinWidth = 260, MaxWidth = 550, Margin = new(0,4,8,4) };
    private readonly CheckBox _master, _onlySelected;
    private readonly Button _search, _apply, _stop, _downloadSet;
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 8, Margin = new(0,8,0,8) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private readonly SemaphoreSlim _descriptionGate = new(1,1);
    private CancellationTokenSource _summaryCancel = new();
    private bool _busy, _closing;
    internal ImageLoraSelection Selection => _original with { Enabled = _master.IsChecked == true,
        ByModel = _original.ByModel.Where(p => p.Key != _model).Append(new KeyValuePair<string, ImageLoraChoice[]>(_model, _choices.Values.ToArray())).ToDictionary(p => p.Key, p => p.Value.ToArray()) };
    private string T(string key) => _l("Generation.Lora." + key);
    internal ImageLoraWindow(Window? owner, string root, string model, ImageLoraSelection selection, Func<string,string> l, bool ru)
    {
        _root=root; _model=model; _l=l; _ru=ru; _original=selection.Snapshot();
        selection.Validate(); _choices=(selection.ByModel.GetValueOrDefault(model) ?? []).ToDictionary(c=>c.Id);
        MusicWishUi.PrepareWindow(this, T("Title"), "Generation.Lora.Window", 1020); MinWidth=660; Height=800;
        if (owner is not null) Owner=owner;
        var dock = new DockPanel { Margin = new(20) }; var top = new StackPanel(); DockPanel.SetDock(top,Dock.Top); dock.Children.Add(top);
        top.Children.Add(MusicWishUi.Text(T("Title") + " · " + ImageGenerationCatalog.Get(model).Name,true));
        _master=MusicWishUi.Check(T("Master"),selection.Enabled,_=>UpdateCount()); AutomationProperties.SetAutomationId(_master,"Generation.Lora.Master"); top.Children.Add(_master);
        top.Children.Add(MusicWishUi.Text(T("Hint")));
        var themes=new WrapPanel();
        foreach (var key in new[]{"Realism","Painting","Characters","Pixel","Uncensored","Body"})
            themes.Children.Add(MusicWishUi.Button(T(key),"Generation.Lora.Topic."+key,()=>_query.Text=T(key)));
        top.Children.Add(themes);
        top.Children.Add(MusicWishUi.Text(T("Query")));
        var row=new WrapPanel(); AutomationProperties.SetName(_query,T("Query")); AutomationProperties.SetAutomationId(_query,"Generation.Lora.Query"); row.Children.Add(_query);
        _search=MusicWishUi.Button(T("Search"),"Generation.Lora.Search",()=>_ = RunSearchAsync(_query.Text)); row.Children.Add(_search);
        _stop=MusicWishUi.Button(T("Stop"),"Generation.Lora.Stop",()=>_operation?.Cancel()); row.Children.Add(_stop); top.Children.Add(row);
        var filters=new WrapPanel(); _onlySelected=MusicWishUi.Check(T("OnlySelected"),false,_=>Filter()); filters.Children.Add(_onlySelected);
        _downloadSet=MusicWishUi.Button(T("DownloadSet"),"Generation.Lora.DownloadSet",()=>ChooseSet(_definitions.Values.Where(d=>d.Supported).ToArray())); filters.Children.Add(_downloadSet); top.Children.Add(filters);
        _count=MusicWishUi.Text(""); top.Children.Add(_count);
        var bottom=new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); dock.Children.Add(bottom);
        _status=MusicWishUi.Text(""); bottom.Children.Add(_status); bottom.Children.Add(_progress);
        var footer=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right };
        _apply=MusicWishUi.Button(T("Apply"),"Generation.Lora.Apply",()=>_ = ApplyAsync()); footer.Children.Add(_apply);
        footer.Children.Add(MusicWishUi.Button(T("Cancel"),"Generation.Lora.Cancel",Close)); bottom.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content=_list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }); Content=dock;
        foreach(var d in ImageLoraCatalog.For(model)) AddCard(d);
        UpdateCount(); SetBusy(false);
        Closing+=(_,e)=>{ if(_busy){e.Cancel=true;_closing=true;_operation?.Cancel();} };
        Closed+=(_,_)=>{ _lifetime.Cancel(); _summaryCancel.Cancel(); };
    }
    private void AddCard(ImageLoraDefinition d)
    {
        if(_definitions.ContainsKey(d.Id)) return; _definitions[d.Id]=d;
        var panel=new StackPanel(); var row=new DockPanel();
        var actions=new StackPanel { Width=160 }; DockPanel.SetDock(actions,Dock.Right); row.Children.Add(actions);
        var title=new StackPanel { Margin=new(0,0,15,0) }; row.Children.Add(title);
        var selected=_choices.GetValueOrDefault(d.Id);
        var check=MusicWishUi.Check(d.Name,selected?.Enabled==true,value=>{
            var old=_choices.GetValueOrDefault(d.Id) ?? new(d.Id,1); _choices[d.Id]=old with { Enabled=value }; UpdateCount(); Filter(); });
        check.IsEnabled=d.Supported; AutomationProperties.SetAutomationId(check,"Generation.Lora.Select."+d.Id); title.Children.Add(check);
        title.Children.Add(MusicWishUi.Text(d.Kind=="collection" ? T("Collection") : T("Compatible")));
        var description=MusicWishUi.Text(ImageLoraCatalog.Bundled.Any(b=>b.Id==d.Id)
            ? (_ru?d.DescriptionRu:d.DescriptionEn) : ImageLoraDescriptionService.Display(d,_ru));
        _descriptions[d.Id]=description; title.Children.Add(description);
        title.Children.Add(MusicWishUi.Text(d.Repository));
        if(d.Supported) {
            title.Children.Add(MusicWishUi.Text($"{d.Bytes/1048576d:0.#} MB · {d.License}"));
            if(d.Trigger.Length>0) title.Children.Add(MusicWishUi.Text(T("Trigger")+" "+d.Trigger));
            var ready=MusicWishUi.Text(ImageLoraAssets.Ready(d,_root)?T("Downloaded"):T("NotDownloaded")); actions.Children.Add(ready);
            actions.Children.Add(MusicWishUi.Button(T("Download"),"Generation.Lora.Download."+d.Id,()=>_ = DownloadAsync([d])));
            actions.Children.Add(MusicWishUi.Button(T("Delete"),"Generation.Lora.Delete."+d.Id,()=>_ = DeleteAsync(d)));
            var strength=new DockPanel(); var value=MusicWishUi.Text((selected?.Strength??1).ToString("0.00")); value.Width=55; DockPanel.SetDock(value,Dock.Right); strength.Children.Add(value);
            strength.Children.Add(MusicWishUi.Text(T("Strength"))); var slider=new Slider { Minimum=0,Maximum=2,Value=selected?.Strength??1,SmallChange=.05,LargeChange=.1,TickFrequency=.05,IsSnapToTickEnabled=true,MinWidth=180,Margin=new(15,7,15,7) };
            AutomationProperties.SetName(slider,T("Strength")+" · "+d.Name); AutomationProperties.SetAutomationId(slider,"Generation.Lora.Strength."+d.Id);
            slider.ValueChanged+=(_,_)=>{ value.Text=slider.Value.ToString("0.00"); _choices[d.Id]=new(d.Id,slider.Value,_choices.GetValueOrDefault(d.Id)?.Enabled==true); }; strength.Children.Add(slider); panel.Children.Add(row); panel.Children.Add(strength);
        } else {
            actions.Children.Add(MusicWishUi.Button(T("ChooseFiles"),"Generation.Lora.Collection."+d.Id,()=>_ = CollectionAsync(d))); panel.Children.Add(row);
        }
        var border=new Border { Child=panel,CornerRadius=new(10),BorderThickness=new(1),Padding=new(14),Margin=new(0,5,8,5) };
        border.SetResourceReference(Border.BackgroundProperty,"PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
        _cards[d.Id]=border; _list.Children.Add(border);
    }
    private void Filter()
    {
        foreach(var (id,card) in _cards) card.Visibility=_onlySelected.IsChecked==true && _choices.GetValueOrDefault(id)?.Enabled!=true ? Visibility.Collapsed:Visibility.Visible;
    }
    private void UpdateCount() { if(_count is not null) _count.Text=T("Selected")+" "+_choices.Values.Count(c=>c.Enabled)+" / "+_definitions.Values.Count(d=>d.Supported); }
    private void SetBusy(bool busy)
    {
        _busy=busy; _list.IsEnabled=_master.IsEnabled=_apply.IsEnabled=_search.IsEnabled=_downloadSet.IsEnabled=!busy;
        _stop.IsEnabled=busy; _progress.IsIndeterminate=busy;
        _progress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
    }
    private void RefreshReady()
    {
        var definitions=_definitions.Values.ToArray(); _definitions.Clear(); _cards.Clear(); _descriptions.Clear(); _list.Children.Clear();
        foreach(var d in definitions) AddCard(d); UpdateCount(); Filter();
    }
}
