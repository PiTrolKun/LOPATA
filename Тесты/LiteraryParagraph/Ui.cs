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
using AIHub.Services;

internal static class Ui
{
    private static IEnumerable<T> All<T>(DependencyObject o)
    { if(o is T t) yield return t; for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++) foreach(var n in All<T>(VisualTreeHelper.GetChild(o,i))) yield return n; }
    private static void Pump()
    { var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(350) }; timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame); }
    internal static void Run(string? source=null)
    {
        var root=Program.Create("_ui",source); Console.WriteLine(root);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var xml=XDocument.Load("Исходники/AIHub/MainWindow.xaml"); XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary=new XElement(ns+"ResourceDictionary",xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var e in xml.Root.Element(ns+"Window.Resources")!.Elements())
            if(e.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)e.Attribute(x+"Key"))) dictionary.Add(new XElement(e));
        var owner=new Window { Resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString()),ShowInTaskbar=false }; app.MainWindow=owner;
        int checks=0;
        void Assert(bool result,string message) { checks++; if(!result) throw new Exception(message); }
        foreach(var lang in new[]{"ru","en"}) foreach(var dark in new[]{false,true})
        {
            foreach(var pair in new[]{("SecondaryButtonBackgroundBrush",dark?"#111827":"#F8F8F8"),("WindowBackgroundBrush",dark?"#111827":"#F3F3F3"),("PanelBrush",dark?"#172033":"#FFFFFF"),("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#AAB4C4":"#5D6470"),("LineBrush",dark?"#2D374B":"#DADDE3")})
                owner.Resources[pair.Item1]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            var d=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
            string L(string key)=>d.GetValueOrDefault(key,key);
            var draft=new LiteraryDraftControl(root,L); draft.EnableSpelling(lang);
            var editorPanel=new DockPanel(); var parameters=new LiteraryProjectParametersControl(root,L);
            DockPanel.SetDock(parameters,Dock.Bottom); editorPanel.Children.Add(parameters); editorPanel.Children.Add(draft);
            var editor=new ContentControl { Content=editorPanel };
            using var runtime=new LiteraryChatRuntime(root);
            var win=new LiteraryParagraphWindow(root,draft,editor,runtime,()=>false,L,lang) { ShowInTaskbar=false };
            win.Show(); Pump(); win.UpdateLayout();
            Assert(All<Expander>(win).All(e=>!e.IsExpanded),"collapsed by default");
            Assert(!All<TextBlock>(win).Any(t=>t.Text.StartsWith("Paragraph.")),"untranslated keys");
            Assert(parameters.Header is TextBlock header && header.Text.Contains(L("Literary.Parameters.Title")),"real parameters summary");
            Assert(!All<System.Windows.Controls.ComboBox>(editorPanel).Any(),"no disabled style or mode placeholders");
            parameters.IsExpanded=true; Pump();
            Assert(All<TextBlock>(parameters).Any(t=>t.Text.StartsWith(L("Literary.Calibration.Field29")+":")),"tempo from creator visible");
            var projectPath=Path.Combine(root,"project.json"); var previousProject=File.ReadAllText(projectPath);
            try
            {
                var p=LiteraryProjectStore.ReadProject(root); var document=new LiteraryCalibrationDocument(p.CreationBrief); var labels=new LiteraryCalibrationLabels(L);
                document.Fields.First(f=>labels.StepNumber(f)==5).Update("Калиброванная форма UI");
                p.CreationBrief=document.Serialize(); File.WriteAllText(projectPath,ParagraphJson.Encode(p));
                parameters.Refresh(); Pump();
                Assert(((TextBlock)parameters.Header).Text.Contains("Калиброванная форма UI"),"calibration value replaces old creator value on refresh");
            }
            finally { File.WriteAllText(projectPath,previousProject); parameters.Refresh(); }
            parameters.IsExpanded=false;
            var tree=All<LiteraryParagraphTree>(win).Single();
            var branch=All<Expander>(tree).First(); branch.IsExpanded=true; Pump();
            var descendant=All<Expander>(branch).Skip(1).First(); descendant.IsExpanded=true; Pump();
            var check=All<CheckBox>(descendant).First(); check.IsChecked=true;
            Assert(All<CheckBox>(branch).First().IsChecked==false,"parent selection independent");
            branch.IsExpanded=false; Pump(); Assert(win.State.Selection.Values.Any(s=>s.Selected),"collapse preserves selection");
            var chosen=win.State.Selection.Count(s=>s.Value.Selected);
            win.State.Recommendations.Add(new("creation/field/0","Проверить исходную идею")); tree.Refresh(); Pump();
            Assert(All<TextBlock>(branch).Any(t=>t.Text.Contains('!')),"recommendation marker on collapsed ancestor");
            Assert(win.State.Selection.Count(s=>s.Value.Selected)==chosen,"recommendation does not auto-select");
            branch.IsExpanded=true; descendant.IsExpanded=true; Pump();
            foreach(var text in All<TextBox>(tree)) Assert(SpellCheck.GetIsEnabled(text),"comment spelling");
            var field=typeof(LiteraryParagraphWindow).GetField("_input",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var input=(TextBox)field.GetValue(win)!; input.Text="Неотправленная просьба для восстановления."; Pump();
            Assert(SpellCheck.GetIsEnabled(input),"input spelling");
            var state=new LiteraryParagraphStore(new(root)).Load(); Assert(state.Request==input.Text,"autosave input");
            var render=typeof(LiteraryParagraphWindow).GetMethod("Render",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!;
            foreach(var stage in new[]{ParagraphStage.Prepared,ParagraphStage.Result})
            { win.State.Stage=stage; win.State.Prepared="Пользователь меняет задачу."; win.State.Result="Пока только предложение Писателя."; render.Invoke(win,null); Pump(); Assert(input.Text.Length>0,"stage input");
                Assert(All<TextBlock>(win).Any(t=>t.Text==L("Paragraph.Input."+stage)),"role and input purpose visible"); }
            Assert(All<TextBlock>(win).Any(t=>t.Text==L("Paragraph.SharedHistory")),"screen history explicitly shared");
            Assert(All<Button>(win).Single(b=>Equals(b.Content,L("Paragraph.Discuss"))).Visibility==Visibility.Collapsed,"discussion hidden in writer stage");
            var route=(System.Windows.Controls.ComboBox)typeof(LiteraryParagraphWindow).GetField("_route",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(win)!;
            var refresh=typeof(LiteraryParagraphWindow).GetMethod("RefreshSources",BindingFlags.NonPublic|BindingFlags.Instance)!;
            win.State.RouteId="route/1"; refresh.Invoke(win,null); Pump();
            Assert(((ComboBoxItem)route.SelectedItem).Tag?.ToString()=="route/1","saved current route restored");
            var currentDescription=new LiteraryParagraphCatalog(LiteraryProjectStore.ReadProject(root),draft.Capture(win.State.ProjectId,root),L).Routes["route/1"].Description;
            Assert(All<TextBlock>(win).Any(t=>t.Text==L("Paragraph.RouteBoundary")+" "+currentDescription),"current route description shown");
            win.State.RouteId="route/999"; refresh.Invoke(win,null); Pump();
            Assert(((ComboBoxItem)route.SelectedItem).Content?.ToString()==L("Paragraph.RouteMissing"),"deleted route remains visibly missing");
            route.SelectedIndex=0; Pump(); Assert(win.State.RouteId=="","user can clear a deleted current route");
            var clear=All<Button>(win).Single(b=>Equals(b.Content,L("Paragraph.Clear"))); clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert(win.State.Stage==ParagraphStage.Request && win.State.History.Count==0,"clear reset");
            Assert(All<Button>(win).Single(b=>Equals(b.Content,L("Paragraph.Discuss"))).Visibility==Visibility.Visible,"discussion available after clear");
            Assert(win.State.Selection.Values.Any(s=>s.Selected),"clear selection preserved");
            input.Text="Просьба после ошибки";
            win.State.RecordExchange(LiteraryChatProfile.Advisor,"Прежняя просьба","Прежний ответ");
            var historyBefore=ParagraphJson.Encode(win.State.History);
            win.State.Selection["missing-scope"]=new(){Selected=true};
            var send=typeof(LiteraryParagraphWindow).GetMethod("SendAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            for(int attempt=0;attempt<3;attempt++)
            {
                var operation=(Task)send.Invoke(win,new object[]{false})!;
                var deadline=DateTime.UtcNow.AddSeconds(20);
                while(!operation.IsCompleted && DateTime.UtcNow<deadline) Pump();
                Assert(operation.IsCompleted,"failed send completes"); operation.GetAwaiter().GetResult();
                Assert(ParagraphJson.Encode(win.State.History)==historyBefore,"failed send does not duplicate history");
                Assert(input.Text=="Просьба после ошибки" && win.State.Stage==ParagraphStage.Request,"failed send preserves input and stage");
                Assert(!runtime.IsBusy && !win.State.Interrupted,"failed send releases operation");
            }
            Assert(ParagraphJson.Encode(new LiteraryParagraphStore(new(root)).Load().History)==historyBefore,"failed send persists unchanged history");
            win.State.Selection.Remove("missing-scope"); clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var bitmap=new RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render((Visual)win.Content);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(root,$"window-{lang}-{dark}.png"))) encoder.Save(file);
            win.Close(); Assert(editor.Parent is null,"shared editor released");
            var reopen=new LiteraryParagraphWindow(root,draft,editor,runtime,()=>false,L,lang) { ShowInTaskbar=false }; reopen.Show(); Pump();
            Assert(reopen.State.Stage==ParagraphStage.Request && reopen.State.Prepared.Length==0,"no cleared text resurrection"); reopen.Close();
            Console.WriteLine($"PASS {lang} dark={dark}");
        }
        WorkspaceKeys(owner,root,Assert);
        Program.Save(root,"ui-results.json",new{checks,passed=true}); owner.Close(); app.Shutdown(); Console.WriteLine("CHECKS "+checks);
    }
    private static void WorkspaceKeys(Window owner,string root,Action<bool,string> assert)
    {
        var l=new LocalizationService(); l.Load("ru"); var project=LiteraryProjectStore.ReadProject(root);
        new LiteraryCalibrationStore(root).MarkOpened();
        using var runtime=new LiteraryChatRuntime(root);
        var workspace=new LiteraryWorkspaceControl(new(project.Id,project.ProjectName,root),project,l.T,(_,_)=>Task.CompletedTask,runtime);
        owner.Content=workspace; owner.Width=1300; owner.Height=900; owner.Show(); Pump();
        var oldParent=workspace.EditorHost.Parent;
        Exception? failure=null;
        owner.Dispatcher.BeginInvoke(new Action(()=>
        {
            try
            {
                var child=Application.Current.Windows.OfType<LiteraryParagraphWindow>().Single();
                assert(ReferenceEquals(child.SharedEditor,workspace.EditorHost),"F1 uses original editor");
                child.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(child),0,System.Windows.Input.Key.F1)
                    { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                assert(Application.Current.Windows.OfType<LiteraryParagraphWindow>().Count()==1,"F1 keeps single instance");
                child.Close();
            }
            catch(Exception ex) { failure=ex; foreach(var w in Application.Current.Windows.OfType<LiteraryParagraphWindow>().ToArray()) w.Close(); }
        }),DispatcherPriority.Loaded);
        workspace.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(workspace),0,System.Windows.Input.Key.F1)
            { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        if(failure is not null) throw failure;
        assert(ReferenceEquals(oldParent,workspace.EditorHost.Parent),"editor reattached to workspace");
        assert(workspace.EditorHost.IsEnabled,"editor enabled after close");
        owner.Content=null; Pump();
    }
}
