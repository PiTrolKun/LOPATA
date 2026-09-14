using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class LiteraryParagraphWindow : Window
{
    private readonly Func<string,string> _l;
    private readonly string _directory, _language;
    private readonly LiteraryDraftControl _draft;
    private readonly LiteraryChatRuntime _runtime;
    private readonly Func<bool> _blocked;
    private readonly LiteraryParagraphStore _store;
    private readonly LiteraryRequestDiagnostics _diagnostics;
    private readonly DispatcherTimer _timer=new() { Interval=TimeSpan.FromMilliseconds(250) };
    private readonly TextBox _input=LiteraryWorkspaceParts.TextArea(false), _history=LiteraryWorkspaceParts.TextArea();
    private readonly TextBlock _stage=LiteraryUi.Text("",true), _status=LiteraryUi.Text(""), _tokens=LiteraryUi.Text(""), _receipts=LiteraryUi.Text("");
    private readonly TextBlock _inputLabel=LiteraryUi.Text("",true), _routeDescription=LiteraryUi.Text("");
    private Dictionary<string,ParagraphRoute> _routes=[];
    private readonly Button _send=new(), _next=new(), _clear=new(), _stop=new(), _retry=new(), _discuss=new();
    private readonly ComboBox _route=new();
    private readonly ContentControl _sourceHost=new(), _editorContainer=new();
    private readonly List<Button> _actions=[];
    private LiteraryParagraphTree _tree=null!;
    private CancellationTokenSource? _operation;
    private bool _rendering, _dirty, _allowClose;
    private long _editVersion;
    public LiteraryParagraphState State { get; }
    public ContentControl SharedEditor { get; }

    public LiteraryParagraphWindow(string directory, LiteraryDraftControl draft, ContentControl editor,
        LiteraryChatRuntime runtime, Func<bool> blocked, Func<string,string> l, string language)
    {
        _directory=directory; _draft=draft; SharedEditor=editor; _runtime=runtime; _blocked=blocked; _l=l; _language=language;
        var layout=new LiteraryProjectLayout(directory); _store=new(layout); State=_store.Load();
        _diagnostics=new("ParagraphUi",_=>{},layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
        Title=l("Paragraph.Title"); Width=1350; Height=900; MinWidth=840; MinHeight=620; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        if(System.Windows.Application.Current?.MainWindow is {} main) Resources=main.Resources;
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        BuildLayout(); RefreshSources(); Render();
        _input.TextChanged+=(_,_)=>
        {
            if(_rendering) return;
            switch(State.Stage) { case ParagraphStage.Request: State.Request=_input.Text; break; case ParagraphStage.Prepared: State.Prepared=_input.Text; break; default: State.Result=_input.Text; break; }
            Changed();
        };
        _route.SelectionChanged+=(_,_)=>
        { if(!_rendering) { State.RouteId=(_route.SelectedItem as ComboBoxItem)?.Tag as string??""; RouteDescription(); Changed(); } };
        _send.Click+=async(_,_)=>await SendAsync(); _stop.Click+=(_,_)=>_operation?.Cancel();
        _discuss.Click+=async(_,_)=>await SendAsync(discuss:true);
        _next.Click+=(_,_)=>{ State.Next(); Changed(); Render(); Save(); };
        _clear.Click+=(_,_)=>{ State.Clear(); _receipts.Text=""; _tokens.Text=""; Changed(); Render(); _tree.Refresh(); Save(); _diagnostics.Write("clear",new { State.SessionId }); };
        _retry.Click+=(_,_)=>{ State.Stage=ParagraphStage.Prepared; Changed(); Render(); Save(); };
        _timer.Tick+=(_,_)=>{ Availability(); if(_dirty) Save(); }; _timer.Start();
        Closing+=OnClosing;
        Closed+=(_,_)=>{ _timer.Stop(); _diagnostics.Dispose(); _editorContainer.Content=null; };
        PreviewKeyDown+=(_,e)=>{ if(e.Key==System.Windows.Input.Key.F1) { Activate(); e.Handled=true; } };
    }
    private void BuildLayout()
    {
        var root=new Grid { Margin=new Thickness(18) }; Content=root;
        root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(.4,GridUnitType.Star),MinHeight=180 });
        root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(8) });
        root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(.6,GridUnitType.Star),MinHeight=260 });
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        _editorContainer.Content=SharedEditor; root.Children.Add(_editorContainer);
        var split=new GridSplitter { Height=6,HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(split,1); root.Children.Add(split);
        var bottom=new Grid(); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1.1,GridUnitType.Star),MinWidth=340 });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(12) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star),MinWidth=330 }); Grid.SetRow(bottom,2); root.Children.Add(bottom);
        var left=new Grid(); left.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1.1,GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); left.Children.Add(_stage);
        var historyPanel=new DockPanel(); var historyLabel=LiteraryUi.Text(_l("Paragraph.SharedHistory"));
        DockPanel.SetDock(historyLabel,Dock.Top); historyPanel.Children.Add(historyLabel); historyPanel.Children.Add(_history);
        Grid.SetRow(historyPanel,1); left.Children.Add(historyPanel); _history.Margin=new Thickness(0,5,0,8);
        var inputPanel=new DockPanel(); DockPanel.SetDock(_inputLabel,Dock.Top); inputPanel.Children.Add(_inputLabel); inputPanel.Children.Add(_input);
        Grid.SetRow(inputPanel,2); left.Children.Add(inputPanel); _input.MinHeight=80; LiterarySpellChecking.Enable(_input,_language);
        var actions=new WrapPanel { Margin=new Thickness(0,0,0,8) };
        void Button(Button button,string label, bool primary=false)
        { button.Content=_l(label); button.Margin=new Thickness(0,0,8,6); button.SetResourceReference(StyleProperty,primary?"PrimaryButtonStyle":"SecondaryButtonStyle"); actions.Children.Add(button); _actions.Add(button); }
        Button(_discuss,"Paragraph.Discuss"); Button(_send,"Paragraph.Prepare",true); Button(_next,"Paragraph.Next"); Button(_retry,"Paragraph.BackToTask"); Button(_clear,"Paragraph.Clear"); Button(_stop,"Paragraph.Stop");
        bottom.Children.Add(LiteraryWorkspaceParts.Card(left));
        var right=new StackPanel();
        right.Children.Add(LiteraryUi.Text(_l("Paragraph.Sources"),true)); right.Children.Add(LiteraryUi.Text(_l("Paragraph.SourcesHint")));
        var refresh=new Button { Content=_l("Paragraph.Refresh"),Margin=new Thickness(0,5,0,5) }; refresh.SetResourceReference(StyleProperty,"SecondaryButtonStyle");
        refresh.Click+=(_,_)=>{ try { RefreshSources(); } catch(Exception ex) { _status.Text=_l("Paragraph.Failure")+" "+ex.Message; } }; _actions.Add(refresh); right.Children.Add(refresh);
        right.Children.Add(LiteraryUi.Text(_l("Paragraph.Route"))); right.Children.Add(_route); _route.Margin=new Thickness(0,5,0,10);
        right.Children.Add(_routeDescription);
        right.Children.Add(_sourceHost); right.Children.Add(_receipts);
        var scroll=new ScrollViewer { Content=right,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        var controls=new DockPanel(); DockPanel.SetDock(actions,Dock.Top); controls.Children.Add(actions); controls.Children.Add(scroll);
        var card=LiteraryWorkspaceParts.Card(controls); Grid.SetColumn(card,2); bottom.Children.Add(card);
        var footer=new StackPanel(); footer.Children.Add(_tokens); footer.Children.Add(_status); Grid.SetRow(footer,3); root.Children.Add(footer);
    }
    private void RefreshSources()
    {
        var project=LiteraryProjectStore.ReadProject(_directory);
        var catalog=new LiteraryParagraphCatalog(project,_draft.Capture(project.Id,_directory),_l);
        _routes=catalog.Routes;
        _tree=new(catalog,State,_l,_language); _tree.Changed+=Changed; _sourceHost.Content=_tree;
        _rendering=true;
        _route.Items.Clear(); _route.Items.Add(new ComboBoxItem { Content=_l("Paragraph.NoRoute"),Tag="" });
        foreach(var node in catalog.Roots.Single(r=>r.Id=="route").Children) _route.Items.Add(new ComboBoxItem { Content=node.Label,Tag=node.Id });
        if(State.RouteId.Length>0 && !_routes.ContainsKey(State.RouteId))
            _route.Items.Add(new ComboBoxItem { Content=_l("Paragraph.RouteMissing"),Tag=State.RouteId });
        _route.SelectedItem=_route.Items.OfType<ComboBoxItem>().FirstOrDefault(i=>(string)i.Tag==State.RouteId)??_route.Items[0];
        RouteDescription();
        _rendering=false;
    }
    private void RouteDescription()
    {
        _routeDescription.Text=string.IsNullOrEmpty(State.RouteId)?"":_routes.TryGetValue(State.RouteId,out var route)
            ?_l("Paragraph.RouteBoundary")+" "+route.Description:_l("Paragraph.RouteMissing");
        _routeDescription.Margin=new Thickness(0,0,0,8);
    }
    private void Changed() { _dirty=true; _editVersion++; Availability(); }
    private bool Save()
    {
        try { _store.Save(State); _dirty=false; return true; }
        catch(Exception ex) { _status.Text=_l("Paragraph.SaveError")+" "+ex.Message; return false; }
    }
    private void Render()
    {
        _rendering=true; _stage.Text=_l("Paragraph.Stage."+State.Stage);
        _inputLabel.Text=_l("Paragraph.Input."+State.Stage);
        _input.Text=State.Stage switch { ParagraphStage.Request=>State.Request,ParagraphStage.Prepared=>State.Prepared,_=>State.Result };
        _history.Text=string.Join("\n\n",State.History.Select(t=>_l("Paragraph."+t.Role)+":\n"+t.Text)); _history.ScrollToEnd();
        _send.Content=_l(State.Stage==ParagraphStage.Prepared?"Paragraph.Write":"Paragraph.Prepare");
        _send.Visibility=State.Stage==ParagraphStage.Result?Visibility.Collapsed:Visibility.Visible;
        _discuss.Visibility=State.Stage==ParagraphStage.Request?Visibility.Visible:Visibility.Collapsed;
        _next.Visibility=_retry.Visibility=State.Stage==ParagraphStage.Result?Visibility.Visible:Visibility.Collapsed;
        _status.Text=State.Interrupted?_l("Paragraph.Interrupted"):State.Failure.Length>0?_l(State.Failure):_l("Paragraph.Manual");
        _rendering=false; Availability();
    }
    private void Availability()
    {
        var busy=_operation is not null || _runtime.IsBusy || _blocked();
        foreach(var button in _actions) button.IsEnabled=!busy;
        _send.IsEnabled=!busy && !string.IsNullOrWhiteSpace(_input.Text);
        _discuss.IsEnabled=!busy && !string.IsNullOrWhiteSpace(_input.Text);
        _stop.IsEnabled=_operation is not null; _sourceHost.IsEnabled=_route.IsEnabled=!busy;
        SharedEditor.IsEnabled=!busy;
    }
}
