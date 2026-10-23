using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services.VectorEditor;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls.VectorEditor;

internal sealed partial class VectorEditorWindow
{
    private UIElement Layout()
    {
        var root = new DockPanel { Margin = new(8) };root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty,"WindowBackgroundBrush");
        var top = new WrapPanel();
        foreach (var (key, symbol, action) in new (string, string, Func<Task>)[] {
            ("Open","＋ "+T("Open"),OpenFilesAsync), ("Save","▣ "+T("Save"),()=>{SaveProject(false);return Task.CompletedTask;}),
            ("SaveAs",T("SaveAs"),()=>{SaveProject(true);return Task.CompletedTask;}), ("Export","↗ "+T("Export"),ExportAsync),
            ("Undo","↶",()=>Shortcut("undo")),("Redo","↷",()=>Shortcut("redo")),("Canvas",T("Canvas"),CanvasAsync),
            ("Fit","⊡",()=>_browser.CallAsync("fit")),("Generation", "✦",ShowProgressAsync),("Recovery",T("Recovery"),RecoverAsync) })
            top.Children.Add(Action(key,symbol,action));
        DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
        var tabRow=new DockPanel(); var add=Action("NewSheet","＋",NewSheetAsync); DockPanel.SetDock(add,Dock.Right);tabRow.Children.Add(add);
        _tabs.MinHeight=36;tabRow.Children.Add(_tabs);bottom.Children.Add(tabRow);
        _tabs.SelectionChanged += async (_,e)=>{if(_loading || e.Source!=_tabs || _tabs.SelectedItem is not TabItem tab || tab.Tag is not string id || id==_project.ActiveSheetId)return;_project.ActiveSheetId=id;await Safe(()=>LoadSheet());};
        bottom.Children.Add(Caption(T("Prompt")));
        var composer=new DockPanel{Margin=new(4,0,4,5)};
        var send=Action("Generate","✦ "+T("Generate"),SendAsync);DockPanel.SetDock(send,Dock.Right);composer.Children.Add(send);
        var edit=Action("EditWithModel",T("EditWithModel"),()=>SendAsync(true));DockPanel.SetDock(edit,Dock.Right);composer.Children.Add(edit);
        var style=Action("UseStyle",T("UseStyle"),CaptureStyleAsync);DockPanel.SetDock(style,Dock.Right);composer.Children.Add(style);
        _prompt.ToolTip=T("Prompt");AutomationProperties.SetAutomationId(_prompt,"Vector.Editor.Prompt");AutomationProperties.SetName(_prompt,T("Prompt"));
        ConfigurePrompt();composer.Children.Add(_prompt);bottom.Children.Add(composer);
        var context=new ContextMenu();
        var insert=new MenuItem{Header=T("InsertAutomatically"),IsCheckable=true,IsChecked=_insertOnCanvas()};
        insert.Click+=(_,_)=>_saveInsert(insert.IsChecked);context.Items.Add(insert);composer.ContextMenu=context;
        var clearStyle=new MenuItem{Header=T("ClearStyle")};clearStyle.Click+=(_,_)=>{_styleSvg=null;_status.Text=T("StyleCleared");};context.Items.Add(clearStyle);
        _status.Margin=new(8,3,8,5);AutomationProperties.SetAutomationId(_status,"Vector.Editor.Status");bottom.Children.Add(_status);
        var work=new Grid();work.ColumnDefinitions.Add(new(){Width=new GridLength(235),MinWidth=160});work.ColumnDefinitions.Add(new(){Width=new GridLength(48)});
        work.ColumnDefinitions.Add(new());work.ColumnDefinitions.Add(new(){Width=new GridLength(255),MinWidth=200});root.Children.Add(work);
        var left=new Grid();left.RowDefinitions.Add(new(){Height=GridLength.Auto});left.RowDefinitions.Add(new());left.RowDefinitions.Add(new(){Height=GridLength.Auto});left.RowDefinitions.Add(new(){Height=new GridLength(.45,GridUnitType.Star)});
        left.Children.Add(Caption(T("Objects"),true));Grid.SetRow(_tree,1);left.Children.Add(_tree);
        var libraryTitle=new StackPanel();libraryTitle.Children.Add(Caption(T("Library"),true));
        var libraryActions=new WrapPanel();libraryActions.Children.Add(Action("PreviewObject","↗",PreviewLibraryAsync));libraryActions.Children.Add(Action("InsertObject","＋",InsertLibraryAsync));libraryTitle.Children.Add(libraryActions);
        Grid.SetRow(libraryTitle,2);left.Children.Add(libraryTitle);Grid.SetRow(_library,3);left.Children.Add(_library);
        _library.MouseDoubleClick+=async(_,_)=>{if(_library.SelectedItem is ListBoxItem item && item.Tag is string id)await Safe(()=>InsertSvgAsync(_project.Objects.First(x=>x.Id==id).Svg));};
        var libraryMenu=new ContextMenu();var previewItem=new MenuItem{Header=T("PreviewObject")};previewItem.Click+=async(_,_)=>await Safe(PreviewLibraryAsync);libraryMenu.Items.Add(previewItem);
        var insertItem=new MenuItem{Header=T("InsertObject")};insertItem.Click+=async(_,_)=>await Safe(InsertLibraryAsync);libraryMenu.Items.Add(insertItem);
        var drop=new MenuItem{Header=T("Delete")};drop.Click+=(_,_)=>{if(_library.SelectedItem is ListBoxItem item&&item.Tag is string id){_history.Record(_project);_project.Objects.RemoveAll(x=>x.Id==id);RefreshLibrary();Changed();}};libraryMenu.Items.Add(drop);_library.ContextMenu=libraryMenu;
        work.Children.Add(Panel(left));
        var tools=new StackPanel();
        foreach(var(key,symbol)in new[]{("select","↖"),("hand","✋"),("rect","□"),("ellipse","○"),("triangle","△"),("star","☆"),("line","╱"),("pen","✎"),("text","T")})
        {var mode=key;tools.Children.Add(Action("Tool."+key,symbol,()=>_browser.CallAsync("tool",mode)));}
        tools.Children.Add(Action("Plastic","≈",()=>PlasticAsync("plastic")));tools.Children.Add(Action("Nodes","◇",()=>PlasticAsync("nodes")));
        var toolsScroll=new ScrollViewer{Content=tools,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(toolsScroll,1);work.Children.Add(toolsScroll);
        var surface=Panel(_browser);Grid.SetColumn(surface,2);work.Children.Add(surface);
        var props=new DockPanel();var quick=new WrapPanel();DockPanel.SetDock(quick,Dock.Bottom);props.Children.Add(quick);
        foreach(var(key,symbol)in new[]{("duplicate","⧉"),("delete","⌫"),("group","▣"),("ungroup","▢"),("front","⇧"),("back","⇩"),("flipX","↔"),("flipY","↕")})
        {var name=key;quick.Children.Add(Action("Command."+key,symbol,()=>Command(name)));}
        var drawing=DrawingPalette();DockPanel.SetDock(drawing,Dock.Top);props.Children.Add(drawing);
        props.Children.Add(new ScrollViewer{Content=_properties,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var properties=Panel(props);Grid.SetColumn(properties,3);work.Children.Add(properties);
        return root;
    }
    private void RefreshTabs()
    {
        _loading=true;_tabs.Items.Clear();
        foreach(var s in _project.Sheets)
        {
            var tab=new TabItem{Header=s.Name,Tag=s.Id};var menu=new ContextMenu();
            var rename=new MenuItem{Header=T("Rename")};rename.Click+=(_,_)=>{var name=Ask(T("Rename"),s.Name);if(string.IsNullOrWhiteSpace(name))return;_history.Record(_project);var i=_project.Sheets.FindIndex(x=>x.Id==s.Id);_project.Sheets[i]=_project.Sheets[i] with{Name=name};Changed();RefreshTabs();};
            var close=new MenuItem{Header=T("CloseSheet"),IsEnabled=_project.Sheets.Count>1};close.Click+=async(_,_)=>{
                _history.Record(_project);_project.Sheets.RemoveAll(x=>x.Id==s.Id);if(_project.ActiveSheetId==s.Id)_project.ActiveSheetId=_project.Sheets[0].Id;
                Changed();RefreshTabs();await Safe(()=>LoadSheet());};menu.Items.Add(rename);menu.Items.Add(close);tab.ContextMenu=menu;
            var duplicate=new MenuItem{Header=T("DuplicateSheet")};duplicate.Click+=async(_,_)=>await Safe(async()=>{
                _history.Record(_project);var original=_project.Sheets.First(x=>x.Id==s.Id);var copy=original with{Id=Guid.NewGuid().ToString("N"),Name=original.Name+" — "+T("Copy")};
                _project.Sheets.Insert(_project.Sheets.IndexOf(original)+1,copy);_project.ActiveSheetId=copy.Id;Changed();RefreshTabs();await LoadSheet();
            });menu.Items.Add(duplicate);
            foreach(var offset in new[]{-1,1}){var shift=new MenuItem{Header=T(offset<0?"SheetLeft":"SheetRight")};shift.Click+=(_,_)=>{
                var index=_project.Sheets.FindIndex(x=>x.Id==s.Id);var next=index+offset;if(next<0||next>=_project.Sheets.Count)return;
                _history.Record(_project);var moving=_project.Sheets[index];_project.Sheets.RemoveAt(index);_project.Sheets.Insert(next,moving);Changed();RefreshTabs();
            };menu.Items.Add(shift);}
            _tabs.Items.Add(tab);if(s.Id==_project.ActiveSheetId)_tabs.SelectedItem=tab;
        }_loading=false;
    }
    private void RefreshLibrary()
    {
        _library.Items.Clear();foreach(var item in _project.Objects){var row=new ListBoxItem{Content=new TextBlock{Text=item.Name,TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.NoWrap},Tag=item.Id,ToolTip=item.Name+"\n"+T("InsertObject")};_library.Items.Add(row);}
    }
    private void RenderTree(JsonElement nodes)
    {
        _treeUpdating=true;_tree.Items.Clear();var map=new Dictionary<string,TreeViewItem>();
        foreach(var n in nodes.EnumerateArray())
        {
            var id=n.GetProperty("id").GetString()!;var parent=n.GetProperty("parent").GetString();var row=new StackPanel{Orientation=Orientation.Horizontal};
            var visible=new Button{Content=n.GetProperty("hidden").GetBoolean()?"◌":"◉",Padding=new(0),BorderThickness=new(0),MinHeight=0,MinWidth=0,Width=20,Height=24,ToolTip=T("Visibility")};
            visible.Click+=async(_,e)=>{e.Handled=true;await Safe(()=>Command("hide",new{id}));};row.Children.Add(visible);
            var locked=new Button{Content=n.GetProperty("locked").GetBoolean()?"▣":"·",Padding=new(0),BorderThickness=new(0),MinHeight=0,MinWidth=0,Width=20,Height=24,ToolTip=T("Lock")};
            locked.Click+=async(_,e)=>{e.Handled=true;await Safe(()=>Command("lock",new{id}));};row.Children.Add(locked);
            var label=n.GetProperty("name").GetString()!;if(label==n.GetProperty("tag").GetString())label=T("Shape."+label);
            row.Children.Add(new TextBlock{Text=label,Margin=new(5,2,0,2),MaxWidth=165,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis});
            var item=new TreeViewItem{Header=row,Tag=id,IsExpanded=string.IsNullOrEmpty(parent),IsSelected=_selection.Contains(id)};
            item.Selected+=async(_,e)=>{e.Handled=true;if(!_treeUpdating)await Safe(()=>_browser.CallAsync("select",new[]{id}));};
            var context=new ContextMenu();var rename=new MenuItem{Header=T("Rename")};rename.Click+=async(_,_)=>{var name=Ask(T("Rename"),n.GetProperty("name").GetString()!);if(!string.IsNullOrWhiteSpace(name)){await _browser.CallAsync("select",new[]{id});await Command("rename",new{value=name});}};context.Items.Add(rename);item.ContextMenu=context;
            if(parent is not null&&map.TryGetValue(parent,out var p))p.Items.Add(item);else _tree.Items.Add(item);map[id]=item;
        }_treeUpdating=false;
    }
    private void RenderProperties(JsonElement message)
    {
        // Preserve the user's caret while entering numeric properties.
        if(_properties.IsKeyboardFocusWithin)return;
        _properties.Children.Clear();_properties.Children.Add(Caption(T("Properties"),true));_properties.Children.Add(_zoom);
        _properties.Children.Add(Caption(T("SelectionHint")));
        if(message.TryGetProperty("bounds",out var b)&&b.ValueKind==JsonValueKind.Object)
        {
            foreach(var key in new[]{"x","y","width","height"})Property(key,b.GetProperty(key).GetDouble().ToString("0.###",CultureInfo.InvariantCulture),value=>Command("bounds",new{key,value=Numeric(value)}));
            Property("rotation","0",value=>Command("rotate",new{value=Numeric(value)}));
            foreach(var (key,field)in new[]{("fill","fill"),("stroke","stroke"),("opacity","opacity"),("strokeWidth","stroke-width")})
                Property(key,key=="opacity"?(Numeric(b.GetProperty(key).GetString()!)*100).ToString(CultureInfo.InvariantCulture):b.GetProperty(key).GetString()!,
                    value=>Command("style",new{key=field,value=key=="opacity"?(Numeric(value)/100).ToString(CultureInfo.InvariantCulture):value}));
            if(b.TryGetProperty("text",out var text)&&text.ValueKind==JsonValueKind.String){
                Property("text",text.GetString()!,value=>Command("textContent",new{value}));
                Property("fontSize",b.GetProperty("fontSize").GetString()!,value=>Command("style",new{key="font-size",value}));
                Property("fontFamily",b.GetProperty("fontFamily").GetString()!,value=>Command("style",new{key="font-family",value}));
            }
        }
        _properties.Children.Add(Caption(T("Plastic"),true));_properties.Children.Add(Caption(T("PlasticHint")));
        Property("radius",_brushRadius.ToString(CultureInfo.InvariantCulture),async v=>{_brushRadius=Math.Max(1,Numeric(v));await UpdateBrush();});
        Property("strength",_brushStrength.ToString(CultureInfo.InvariantCulture),async v=>{_brushStrength=Math.Clamp(Numeric(v),.01,1);await UpdateBrush();});
    }
    private void Property(string key,string value,Func<string,Task> apply)
    {
        var row=new Grid{Margin=new(4,3,4,3)};row.ColumnDefinitions.Add(new(){Width=new GridLength(90)});row.ColumnDefinitions.Add(new());
        row.Children.Add(Caption(T("Property."+key)));var box=new TextBox{Text=value,Padding=new(6,4,6,4),MinWidth=50};
        AutomationProperties.SetAutomationId(box,"Vector.Editor.Property."+key);Grid.SetColumn(box,1);row.Children.Add(box);_properties.Children.Add(row);
        var initial=value;async Task Commit(){if(box.Text==initial)return;await apply(box.Text.Trim());initial=box.Text;_browser.Focus();}
        box.PreviewKeyDown+=async(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;await Safe(Commit);}};
        box.LostKeyboardFocus+=async(_,_)=>await Safe(Commit);
        if(key is "fill" or "stroke"){
            row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var color=new Button{Content="●",Padding=new(5,2,5,2),Margin=new(3,0,0,0),ToolTip=T("ChooseColor")};Grid.SetColumn(color,2);row.Children.Add(color);
            color.Click+=async(_,_)=>await Safe(async()=>{
                using var picker=new System.Windows.Forms.ColorDialog{FullOpen=true,AnyColor=true};
                try{picker.Color=System.Drawing.ColorTranslator.FromHtml(box.Text);}catch(ArgumentException){picker.Color=System.Drawing.Color.Blue;}
                if(picker.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                box.Text="#"+picker.Color.R.ToString("X2")+picker.Color.G.ToString("X2")+picker.Color.B.ToString("X2");await Commit();
            });
        }
    }
    private static double Numeric(string value)=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number)?number:throw new System.IO.InvalidDataException("Vector.Editor.Error.Number");
}
