using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

internal static class Program
{
    private static int _checks;
    internal static void Check(bool value,string name) { _checks++; if(!value) throw new Exception(name); }
    internal static IEnumerable<T> All<T>(DependencyObject value)
    { if(value is T t) yield return t; for(var i=0;i<VisualTreeHelper.GetChildrenCount(value);i++) foreach(var child in All<T>(VisualTreeHelper.GetChild(value,i))) yield return child; }
    internal static void Pump()
    { var frame=new DispatcherFrame(); var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(150)}; timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame); }
    internal static object? Field(object value,string name) => value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(value);
    internal static void Call(object value,string name) => value.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(value,null);
    internal static Grid Shell(LiteraryWorkspaceControl workspace, XDocument xml, ResourceDictionary resources)
    {
        Application.Current.Resources=resources;
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation", x="http://schemas.microsoft.com/winfx/2006/xaml";
        var footer=new XElement(xml.Descendants(ns+"Border").Single(e=>(string?)e.Attribute("Grid.Row")=="2" && e.Descendants().Any(d=>(string?)d.Attribute(x+"Name")=="StatusText")));
        footer.SetAttributeValue("Grid.Row","1");
        foreach(var click in footer.Descendants().Attributes("Click").ToArray()) click.Remove();
        var namespaces=xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration).Select(a=>new XAttribute(a)).ToArray();
        var controls=(XNamespace)"clr-namespace:AIHub.Controls;assembly=AIHub";
        var shellXml=new XElement(ns+"Grid",namespaces,
            new XElement(ns+"Grid.RowDefinitions",new XElement(ns+"RowDefinition",new XAttribute("Height","*")),new XElement(ns+"RowDefinition",new XAttribute("Height","Auto"))),
            new XElement(controls+"LiteraryNavigationControl",new XAttribute(x+"Name","LiteraryPage")),footer);
        var shell=(Grid)XamlReader.Parse(shellXml.ToString());
        var navigation=(LiteraryNavigationControl)shell.FindName("LiteraryPage");
        navigation.GetType().GetField("_workspace",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(navigation,workspace);
        Call(navigation,"Render");
        ((FrameworkElement)shell.FindName("CoreMemoryIndicatorPanel")).Visibility=Visibility.Collapsed;
        return shell;
    }
    internal static string Create(string output)
    {
        var root=Path.Combine(output,"project"); Directory.CreateDirectory(root);
        var project=new LiteraryProject { ProjectName="Studio probe",WorkTitle="У двери мастерской",Genres=["comedy","adventure"],
            CreationBrief="""{"kind":"confirmed_creation_intent_not_manuscript_events","sections":[{"Topic":1,"Question":"Что вы хотите написать? Расскажите идею своими словами.","Text":"Инженер пытается открыть старую мастерскую.","Meaning":"intent"},{"Topic":7,"Question":"Темп","Text":"Неторопливо","Meaning":"intent"}]}""" };
        File.WriteAllText(Path.Combine(root,"project.json"),JsonSerializer.Serialize(project));
        new LiteraryProjectLayout(root).Initialize(); var store=new LiteraryChapterStore(root); store.Open(); store.Save("Инженер остановился перед дверью. Из мастерской доносился тихий звон."); return root;
    }
    [STAThread] static void Main()
    {
        var output=Path.GetFullPath("Тесты/LiteraryStudio/runs/"+DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(output);
        var root=Create(output); Logic(root);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var xml=XDocument.Load("Исходники/AIHub/MainWindow.xaml"); XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary=new XElement(ns+"ResourceDictionary",xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var e in xml.Root.Element(ns+"Window.Resources")!.Elements())
            if(e.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)e.Attribute(x+"Key"))) dictionary.Add(new XElement(e));
        foreach(var lang in new[]{"ru","en"}) foreach(var dark in new[]{true,false})
        {
            var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            foreach(var pair in new[]{("SecondaryButtonBackgroundBrush",dark?"#111827":"#F8F8F8"),("WindowBackgroundBrush",dark?"#111827":"#F3F3F3"),("HeaderBackgroundBrush",dark?"#0B1220":"#FFFFFF"),("PanelBrush",dark?"#172033":"#FFFFFF"),("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#AAB4C4":"#5D6470"),("LineBrush",dark?"#2D374B":"#DADDE3")})
                resources[pair.Item1]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            var translations=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
            string L(string key)=>translations.GetValueOrDefault(key,key);
            var win=new Window { Width=1480,Height=950,Resources=resources,ShowInTaskbar=false,UseLayoutRounding=true }; app.MainWindow=win;
            var layoutPasses=0;
            win.LayoutUpdated+=(_,_)=>layoutPasses++;
            using var watchdog=new System.Threading.Timer(_=>{Console.WriteLine($"HANG {lang}/{dark}, checks={_checks}, layout passes={layoutPasses}");Environment.Exit(3);},null,30000,System.Threading.Timeout.Infinite);
            win.SetResourceReference(Window.BackgroundProperty,"WindowBackgroundBrush");
            var project=LiteraryProjectStore.ReadProject(root); using var runtime=new LiteraryChatRuntime(root);
            var workspace=new LiteraryWorkspaceControl(new LiteraryProjectEntry(project.Id,"Studio probe",root),project,L,preparedRuntime:runtime);
            workspace.GetType().GetField("_memoryStarted",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(workspace,true);
            var shell=Shell(workspace,xml,resources); win.Content=shell; win.Show(); Pump();
            var studio=All<LiteraryStudioControl>(win).Single();
            studio.State.Clear(); studio.State.Add("User","Хочу начать со знакомства с героем у мастерской.");
            studio.State.Add("Advisor","Можно показать его через небольшое действие. Он прислушивается к звуку за дверью: так мы знакомимся с его любопытством, не пересказывая биографию.");
            Call(studio,"Render"); Pump();
            var status=(TextBlock)Field(studio,"_status")!;
            var tokens=(TextBlock)Field(studio,"_tokens")!;
            status.Text=L("Paragraph.Working"); tokens.Text=L("Paragraph.Tokens")+" 3538 / 55552";
            ((TextBlock)Field(workspace,"_memoryStatus")!).Text=L("Literary.Rag.Ready"); Pump();
            var statusHost=(ContentControl)shell.FindName("LiteraryStatusHost");
            Check(statusHost.IsVisible && All<TextBlock>(statusHost).Contains(status) && All<TextBlock>(statusHost).Contains(tokens),"live status and context reach the main footer");
            Check(!All<TextBlock>(studio).Contains(status) && !All<TextBlock>(studio).Contains(tokens),"route/workspace has no status copy");
            Check(!((TextBlock)shell.FindName("StatusText")).IsVisible,"generic footer text hidden in literary scenario");
            Check(!All<TextBlock>(workspace).Any(t=>t.Text==L("Studio.Shortcuts")),"artifact shortcuts have no visible label");
            var right=((Grid)studio.Content).Children.OfType<Grid>().Single(g=>Grid.GetColumn(g)==2);
            var lower=right.Children.OfType<Grid>().Single(g=>Grid.GetRow(g)==2);
            Check(lower.Children.OfType<Border>().All(card=>Math.Abs(card.TranslatePoint(new Point(0,card.ActualHeight),studio).Y-studio.ActualHeight)<1),"all three cards fill the workspace to bottom");
            Check(!All<Button>(studio).Any(b=>Equals(b.Content,L("Studio.Prompts")) || Equals(b.Content,L("Literary.Anchor.Title"))),"global tools removed from contextual actions");
            Check(!All<TextBlock>(win).Any(t=>t.Text.StartsWith("Studio.")),"localized labels");
            Check(All<LiteraryFloatingPanel>(win).Count()==1,"floating controls");
            var floating=All<LiteraryFloatingPanel>(win).Single();
            var commands=(UIElement)Field(floating,"_commands")!;
            var frame=(Border)Field(floating,"_frame")!;
            var editorHost=(FrameworkElement)Field(floating,"_widthReference")!;
            Check(commands.Visibility==Visibility.Collapsed,"toolbar starts collapsed");
            Call(floating,"Position");
            Check(floating.IsArrangeValid,"parking an unchanged collapsed toolbar does not invalidate layout");
            Check(!All<Button>((StackPanel)Field(studio,"_messages")!).Any(b=>Equals(b.Content,L("Studio.Quote"))),"chat messages have no source quote button");
            studio.AttachQuote("Source",string.Join(" ",Enumerable.Repeat("Длинная цитата из источника с подробностями сцены.",60))); Pump();
            Call(floating,"Position");
            Check(floating.IsArrangeValid,"long quote leaves parked toolbar layout stable");
            studio.State.Quotes.Clear(); Call(studio,"Render"); Pump();
            var editorBox=(TextBox)Field(All<LiteraryDraftControl>(win).Single(),"_editor")!;
            Check(Canvas.GetTop(frame)>=editorBox.TranslatePoint(new Point(0,editorBox.ActualHeight),floating).Y
                && Canvas.GetLeft(frame)>editorHost.ActualWidth/2,$"toolbar starts below text at editor right: top={Canvas.GetTop(frame)}, text bottom={editorBox.TranslatePoint(new Point(0,editorBox.ActualHeight),floating).Y}, frame height={frame.ActualHeight}, x={Canvas.GetLeft(frame)}");
            All<Button>(floating).Single(b=>Equals(b.Content,"▾")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(Math.Abs(frame.ActualWidth-editorHost.ActualWidth)<1,"expanded toolbar matches editor width");
            Call(floating,"Position");
            Check(floating.IsArrangeValid,"parking an unchanged expanded toolbar does not invalidate layout");
            Check(((DockPanel)((Border)((ContentControl)editorHost).Content).Child).Margin.Top==0,"editor has no reserved toolbar gap");
            var commandButtons=All<Button>(commands).ToArray();
            Check(commandButtons.Select(b=>Math.Round(b.TranslatePoint(new Point(),commands).Y)).Distinct().Count()==1,"chapter commands share one row");
            var originalWidth=win.Width; win.Width+=220; Pump();
            Check(Math.Abs(frame.ActualWidth-editorHost.ActualWidth)<1,"toolbar follows window resize");
            win.Width=originalWidth; Pump();
            var editorColumn=((Grid)editorHost.Parent).ColumnDefinitions[0];
            var originalColumnWidth=editorColumn.Width; editorColumn.Width=new GridLength(680); Pump();
            Check(Math.Abs(frame.ActualWidth-editorHost.ActualWidth)<1,"toolbar follows editor divider");
            editorColumn.Width=originalColumnWidth; Pump();
            All<Button>(floating).Single(b=>Equals(b.Content,"▴")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(commands.Visibility==Visibility.Collapsed,"floating panel collapses");
            Check(frame.ActualHeight<=40 && frame.ActualWidth<editorHost.ActualWidth,"collapsed toolbar is compact");
            All<Button>(floating).Single(b=>Equals(b.Content,"▾")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var thumb=All<System.Windows.Controls.Primitives.Thumb>(floating).Single(t=>Equals(t.ToolTip,L("Studio.Drag")));
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(100000,100000) {RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent}); Pump();
            Check(Canvas.GetLeft(frame)>=0 && Canvas.GetLeft(frame)+frame.ActualWidth<=floating.ActualWidth+1 && Canvas.GetTop(frame)+frame.ActualHeight<=floating.ActualHeight+1,"floating panel stays inside window");
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-100000,-100000) {RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent}); Pump();
            Check(All<Expander>(studio).All(e=>!e.IsExpanded),"collapsed source tree");
            var input=(TextBox)Field(studio,"_input")!; Check(SpellCheck.GetIsEnabled(input),"input spelling");
            input.Clear(); Pump();
            Check(PreviewEnter(win,input).Handled && !studio.IsWorking,"Enter uses send handling without generating for an empty input");
            var inputBottom=input.TranslatePoint(new Point(0,input.ActualHeight),win).Y;
            var shortHeight=input.ActualHeight; input.Text=string.Join("\n",Enumerable.Repeat("Строка ввода",8)); Pump();
            Check(input.ActualHeight>shortHeight && input.MaxLines==6,"input grows to six lines");
            Check(Math.Abs(input.TranslatePoint(new Point(0,input.ActualHeight),win).Y-inputBottom)<2,"input grows upward");
            input.Text="Продумать встречу у двери"; Pump();
            studio.AttachQuote("Test source","Латунный ключ остался в левом кармане."); Pump();
            Check(studio.State.Quotes.Count==1,"quote attached");
            Check(All<Button>(studio).Any(b=>Equals(b.Content,L("Studio.FreeChat"))),"free chat action");
            if(lang=="ru" && dark)
            {
                foreach(var tool in new[]{"Prompts","Anchor"})
                {
                    string? dialogTitle=null;
                    var closeDialog=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(220)};
                    closeDialog.Tick+=(_,_)=>
                    {
                        closeDialog.Stop(); var dialog=app.Windows.OfType<Window>().FirstOrDefault(w=>w.Owner==win);
                        if(dialog is not null) {dialogTitle=dialog.Title; dialog.Close();}
                    };
                    closeDialog.Start(); workspace.OpenStudioLayer(tool); closeDialog.Stop();
                    Check(dialogTitle is not null && dialogTitle.StartsWith(L(tool=="Prompts"?"Studio.Prompts":"Literary.Anchor.Title")),"header tool opens its dialog: "+tool);
                }
                var draft=All<LiteraryDraftControl>(win).Single();
                File.WriteAllText(draft.Store.FilePath,"EXTERNAL_REVISION"); draft.RefreshExternalText();
                Check(draft.Text=="EXTERNAL_REVISION","clean editor reloads external revision");
                ((TextBox)Field(draft,"_editor")!).Text="LOCAL_UNSAVED_REVISION";
                File.WriteAllText(draft.Store.FilePath,"EXTERNAL_SECOND_REVISION");
                Check(!draft.Save() && File.ReadAllText(draft.Store.FilePath)=="EXTERNAL_SECOND_REVISION" && draft.Text=="LOCAL_UNSAVED_REVISION","external conflict preserves both versions");
                File.WriteAllText(draft.Store.FilePath,"EXTERNAL_REVISION"); Check(draft.Save(),"local edit can be saved after conflict resolved");
                All<Button>(studio).Single(b=>Equals(b.Content,L("Studio.FreeChat"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                var free=app.Windows.OfType<LiteraryFreeChatWindow>().Single();
                Check(PreviewEnter(free,(TextBox)Field(free,"_input")!).Handled && !free.IsWorking,"free chat handles Enter without generating for an empty input");
                ((TextBox)Field(free,"_input")!).Text="EPHEMERAL_PRIVATE_SENTINEL"; free.Close(); Pump();
                Check(!Directory.GetFiles(root,"*.json",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p).Contains("EPHEMERAL_PRIVATE_SENTINEL")),"free chat input not persisted");
                var presets=new PromptPairStore(Path.Combine(output,"presets.json")); var list=presets.Load();
                var p=new PromptPairPreset {ContractId="literary.studio.action.Discuss",Name="Test variant",AnalysisPrompt="ROLE_OVERRIDE",ComposePrompt="ACTION_OVERRIDE"};
                list.Add(p);presets.Save(list);
                var setStore=new LiteraryPromptSetStore(Path.Combine(output,"sets.json"));setStore.Load();
                var set=LiteraryPromptSets.Defaults("Test set");set.Actions["Discuss"]=new("ROLE_OVERRIDE","ACTION_OVERRIDE");setStore.Save([set]);
                var prompts=new LiteraryStudioPromptWindow(win,studio.State,L,studio.Save,presets,setStore); prompts.Show();Pump();
                All<Button>(prompts).Single(b=>Equals(b.Content,L("PromptPairs.Custom"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                All<ComboBox>(prompts).Single().SelectedIndex=0;
                Check(LiteraryPromptSets.Resolve(studio.State,"Discuss").Action=="ACTION_OVERRIDE","set applied to current project");
                Check(LiteraryPromptSets.Resolve(studio.State,"Continue").Action==LiteraryStudioPrompts.Get("Continue").Prompt,"writer default retained in full set");prompts.Close();
                input.Focus(); Hotkey(win,input,System.Windows.Input.Key.F1,typeof(LiteraryParagraphWindow));
                Check(All<LiteraryDraftControl>(win).Count()==1,"F1 returns single shared editor");
                ((Button)Field(studio,"_archive")!).Focus(); Hotkey(win,(Button)Field(studio,"_archive")!,System.Windows.Input.Key.F2,null);
                studio=All<LiteraryStudioControl>(win).Single();
                Check(All<LiteraryDraftControl>(win).Count()==1,"F2 returns shared editor");
                Check(editorHost.TranslatePoint(new Point(),studio).Y<1 && editorHost.ActualHeight>studio.ActualHeight*.7,"returning from F2 preserves editor above navigation");
            }
            ((TextBlock)Field(studio,"_status")!).Text=L("Paragraph.Working");
            ((TextBlock)Field(studio,"_tokens")!).Text=L("Paragraph.Tokens")+" 3538 / 55552"; Pump();
            var activity=(LiteraryRequestIndicator)Field(studio,"_activity")!;
            activity.ShowActivity(L("Studio.Activity.Reply")); Pump();
            var bitmap=new RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(win);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(output,$"{lang}-{dark}.png"))) png.Save(file);
            activity.Stop();
            studio.State.Transfer("Один абзац у двери."); studio.State.Result="Инженер поднял руку, но замер, услышав звон."; studio.State.Add("Writer",studio.State.Result); Call(studio,"Render"); Pump();
            Check(All<Button>(studio).Any(b=>Equals(b.Content,L("Studio.CopyAll"))),"writer copy");
            CheckWriterClipboard(studio,L);
            Check(!All<Button>(studio).Any(b=>Equals(b.Content,L("Studio.FreeChat"))),"writer has no free chat action");
            Check(studio.State.Messages.Where(m=>m.Role is "User" or "Advisor").All(m=>!m.InContext),"advisor context cleared");
            HandoffProbe.Settings(studio,win,Path.Combine(output,$"settings-{lang}-{dark}.png"),L);
            if(lang=="ru" && dark) HandoffProbe.Run(win,output,L);
            var navigation=(LiteraryNavigationControl)shell.FindName("LiteraryPage");
            navigation.Visibility=Visibility.Collapsed; Pump();
            Check(!statusHost.IsVisible && ((TextBlock)shell.FindName("StatusText")).IsVisible,"other scenarios recover the normal footer");
            navigation.Visibility=Visibility.Visible; Pump();
            Check(statusHost.IsVisible && All<TextBlock>(statusHost).Contains((TextBlock)Field(studio,"_status")!),"footer reconnects after page and artifact switches");
            Check(workspace.CanLeave(),"safe leave"); navigation.GoBack(); Pump();
            Check(statusHost.Content is null,"leaving project clears its footer"); win.Close(); Pump();
        }
        File.WriteAllText(Path.Combine(output,"result.txt"),$"{_checks} checks passed; no model inference."); Console.WriteLine($"PASS {_checks} {output}"); app.Shutdown();
    }
    private static void CheckWriterClipboard(LiteraryStudioControl studio,Func<string,string> l)
    {
        var previous=Clipboard.GetDataObject();
        try
        {
            var first=studio.State.Messages.Last(m=>m.Role=="Writer");
            studio.State.Add("Advisor","ADVISOR_NOT_FOR_CLIPBOARD").InContext=false;
            studio.State.Add("User","USER_NOT_FOR_CLIPBOARD").InContext=false;
            var second=studio.State.Add("Writer","Второй ответ.\nЕщё строка — целиком, с «кавычками».");
            foreach(var archive in new[]{false,true})
            {
                studio.GetType().GetField("_showArchive",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(studio,archive);
                Call(studio,"RenderMessages"); Pump();
                var messages=(StackPanel)Field(studio,"_messages")!;
                foreach(var expected in new[]{second,first})
                {
                    var panel=messages.Children.OfType<StackPanel>().Last(p=>All<TextBox>(p).Any(t=>t.Text==expected.Text));
                    All<TextBox>(studio).First(t=>t.IsReadOnly).SelectAll();
                    Clipboard.SetText("UNRELATED_PREVIOUS_CLIPBOARD");
                    All<Button>(panel).Single(b=>Equals(b.Content,l("Studio.CopyAll"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Clipboard.GetText()==expected.Text,"copy only the clicked writer message, archive="+archive);
                }
            }
        }
        finally { if(previous is null) Clipboard.Clear(); else Clipboard.SetDataObject(previous,true); }
    }
    private static void Hotkey(Window owner,UIElement target,System.Windows.Input.Key key,Type? expected)
    {
        var found=false;var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(220)};
        timer.Tick+=(_,_)=>
        {
            timer.Stop();var window=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w!=owner && (expected is null || expected.IsInstanceOfType(w)));
            if(window is not null) {found=true;window.Close();}
        };timer.Start();
        var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(owner),0,key) {RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
        target.RaiseEvent(args);Pump();Check(found,"scenario hotkey "+key+" outside editor");
    }
    private static System.Windows.Input.KeyEventArgs PreviewEnter(Window owner,UIElement target)
    {
        var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(owner),0,System.Windows.Input.Key.Enter)
            {RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
        target.RaiseEvent(args); return args;
    }
    private static void Logic(string root)
    {
        var store=new LiteraryStudioStore(new(root)); var state=store.Load(); state.Add("User","secret-old-dialogue"); state.Add("Advisor","proposal");
        state.Transfer("local task"); Check(state.Messages.Where(m=>m.Role is "User" or "Advisor").All(m=>!m.InContext),"transfer clears advisor");
        state.Result="writer result"; state.Add("Writer",state.Result); state.ReturnToAdvisor();
        Check(state.Messages.Where(m=>m.InContext).Select(m=>m.Role).SequenceEqual(new[]{"Task","Writer"}),"return only task and result");
        var count=state.Messages.Count; state.Clear(); Check(state.Messages.Count==count && state.Messages.All(m=>!m.InContext),"clear preserves archive");
        store.Save(state); var again=new LiteraryStudioStore(new(root)); var loaded=again.Load(); Check(loaded.Messages.Count==count,"history persists");
        state.Input="changed"; store.Save(state); var conflict=false; try{again.Save(loaded);}catch(IOException){conflict=true;} Check(conflict,"optimistic session conflict");
        Check(LiteraryStudioPrompts.Writer.Count==6 && LiteraryStudioPrompts.Advisor.Count==6,"action catalog");
        Check(LiteraryStudioPrompts.Get("Tone").RequiresInput && !LiteraryStudioPrompts.Get("Continue").RequiresInput,"input requirements");
    }
}
