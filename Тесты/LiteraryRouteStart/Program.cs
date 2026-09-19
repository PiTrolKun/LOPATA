using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private static string _output = "";
    private static Application _app = null!;
    private const string Brief = """{"sections":[{"Topic":1,"Text":"Original idea"}],"route":[{"Number":1,"Title":"Начало пути","Description":"Знакомство с героем"},{"Number":4,"Title":"Встреча","Description":"Разговор у мастерской"}]}""";
    [STAThread] private static void Main()
    {
        _output=Path.GetFullPath("Тесты/LiteraryRouteStart/runs/"+DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(_output);
        _app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var xml=XDocument.Load("Исходники/AIHub/MainWindow.xaml");
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary=new XElement(ns+"ResourceDictionary",xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var element in xml.Root.Element(ns+"Window.Resources")!.Elements())
            if(element.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)element.Attribute(x+"Key"))) dictionary.Add(new XElement(element));
        foreach(var dark in new[]{true,false}) foreach(var lang in new[]{"ru","en"})
        {
            var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            foreach(var pair in new[]{("WindowBackgroundBrush",dark?"#101827":"#F5F6F9"),("SecondaryButtonBackgroundBrush",dark?"#111827":"#F8F8F8"),
                ("PanelBrush",dark?"#172033":"#FFFFFF"),("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#AAB4C4":"#5D6470"),("LineBrush",dark?"#2D374B":"#DADDE3")})
                resources[pair.Item1]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            _app.Resources=resources;
            var translations=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
            string L(string key)=>translations.TryGetValue(key,out var value)?value:throw new Exception("Missing: "+key);
            var owner=new Window { Width=1200,Height=860,Resources=resources,ShowInTaskbar=false,UseLayoutRounding=true };
            owner.SetResourceReference(Window.BackgroundProperty,"WindowBackgroundBrush"); owner.Show();
            var prefix=lang+"-"+dark;
            Start(owner,L,prefix);
            Route(owner,L,lang,prefix);
            Studio(owner,L,lang,prefix);
            owner.Close();
        }
        var summary=$"PASS: {_checks} checks; RU/EN dark/light; route editing, append, validation, save failure, reopen, current stage and sources; start screen actions and narrow layout. Synthetic projects only; no model calls.";
        File.WriteAllText(Path.Combine(_output,"result.txt"),summary); Console.WriteLine(summary+"\n"+_output); _app.Shutdown();
    }

    private static void Start(Window owner,Func<string,string> l,string prefix)
    {
        var invoked=new int[5];
        var control=new LiteraryProjectStartControl(new("synthetic","У двери мастерской","Synthetic project"),l,
            ()=>invoked[0]++,()=>invoked[1]++,()=>invoked[2]++,()=>invoked[3]++,()=>invoked[4]++);
        owner.Content=new ScrollViewer { Content=control,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(24) }; Pump();
        foreach(var id in new[]{"New","Continue","Select","Active","Export"}) Click(owner,"ProjectStart."+id);
        Check(invoked.All(i=>i==1),"five original actions remain connected");
        Check(All<UniformGrid>(control).Single().Columns==2,"wide screen uses two columns"); Capture(owner,prefix+"-start-wide");
        owner.Width=680; owner.Height=760; Pump();
        Check(All<UniformGrid>(control).Single().Columns==1,"narrow screen uses one column");
        Check(All<Button>(control).All(b=>b.ActualWidth>30 && b.ActualWidth<control.ActualWidth),"buttons fit narrow cards");
        Capture(owner,prefix+"-start-narrow");
        owner.Content=new LiteraryProjectStartControl(null,l,()=>{},null,()=>{},()=>{},()=>{}); Pump();
        Check(!Get<Button>(owner,"ProjectStart.Continue").IsEnabled && Get<Button>(owner,"ProjectStart.New").IsEnabled,"no active project has safe actions");
        // Exercise the real navigation heading, margins and footer, with room reserved for the app shell.
        var navigation=new LiteraryNavigationControl();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(LiteraryNavigationControl).GetField("_l",flags)!.SetValue(navigation,l);
        typeof(LiteraryNavigationControl).GetProperty("ShowingProjects")!.SetValue(navigation,true);
        var projects=(LiteraryProjectSelection)typeof(LiteraryNavigationControl).GetField("_projects",flags)!.GetValue(navigation)!;
        projects.SetProjects([new("synthetic","У двери мастерской","Synthetic project")]); projects.SetActive("synthetic");
        typeof(LiteraryNavigationControl).GetMethod("Render",flags|System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(navigation,null);
        owner.Content=new Border { Child=navigation,Padding=new Thickness(0,90,0,40) };
        var keys=new[]{"UiBodyFontSize","UiSmallFontSize","UiTinyFontSize","UiCardTitleFontSize","UiPageTitleFontSize","UiButtonFontSize","UiButtonHeight"};
        var originals=keys.ToDictionary(k=>k,k=>owner.Resources[k]);
        foreach(var size in new[]{(1920d,1030d,1.25),(1360d,850d,1d)})
        {
            owner.Width=size.Item1; owner.Height=size.Item2;
            foreach(var key in keys) owner.Resources[key]=(double)originals[key]*size.Item3;
            Pump();
            var scroll=Visual<ScrollViewer>(navigation).Single();
            Check(scroll.ScrollableHeight<1,"all start cards visible without scrolling at "+size);
            var export=Get<Button>(owner,"ProjectStart.Export");
            Check(export.TranslatePoint(new Point(0,export.ActualHeight),scroll).Y<=scroll.ActualHeight,"export button fully visible at "+size);
            Capture(owner,prefix+"-start-navigation-"+size.Item1);
        }
        foreach(var key in keys) owner.Resources[key]=originals[key];
        owner.Width=1200; owner.Height=860;
    }

    private static void Route(Window owner,Func<string,string> l,string language,string prefix)
    {
        string? saved=null; var fail=true;
        var window=new LiteraryRouteEditorWindow(owner,Brief,l,language,text=> { if(fail) throw new IOException("test"); saved=text; });
        Inspect(()=>
        {
            Get<TextBox>(window,"Route.Title").Text="Исправленное начало";
            Get<TextBox>(window,"Route.Description").Text="Новая цель сцены";
            for(var i=0;i<11;i++) { Click(window,"Route.Add"); Get<TextBox>(window,"Route.Title").Text="Следующий этап "+i; Get<TextBox>(window,"Route.Description").Text="Описание "+i; }
            Check(Get<ListBox>(window,"Route.Steps").Items.Count==13,"can exceed onboarding limit");
            Get<TextBox>(window,"Route.Title").Text=""; Click(window,"Route.Save");
            Check(saved is null && window.IsVisible,"empty title rejected");
            Check(All<TextBlock>(window).Any(t=>t.Text==l("Studio.Route.TitleRequired")),"inline validation displayed");
            Get<TextBox>(window,"Route.Title").Text="Финальный этап"; Click(window,"Route.Save");
            Check(saved is null && window.IsVisible && All<TextBlock>(window).Any(t=>t.Text==l("Studio.Route.SaveError")),"failed save retains window and edits");
            var selected=(ListBoxItem)Get<ListBox>(window,"Route.Steps").SelectedItem;
            var selectedBrush=(SolidColorBrush)Visual<Border>(selected).First().Background;
            Check(selectedBrush.Color==((SolidColorBrush)window.FindResource("AccentBrush")).Color,"selected stage keeps readable accent when text field has focus");
            Capture(window,prefix+"-route"); fail=false; Click(window,"Route.Save");
        });
        Check(window.ShowDialog()==true && saved is not null,"save closes after successful persistence");
        var result=new LiteraryRouteDocument(saved!); Check(result.Steps[0].Number==1 && result.Steps[1].Number==4 && result.Steps.Last().Number==15,"stable original numbers and appended IDs");
        Check(result.Steps[0].Description=="Новая цель сцены","edits survived changing selection");
        var reopen=new LiteraryRouteEditorWindow(owner,saved!,l,language,_=>throw new Exception("Cancel must not save"));
        Inspect(()=>{Check(Get<ListBox>(reopen,"Route.Steps").Items.Count==13,"reopen full route"); Click(reopen,"Route.Cancel");}); reopen.ShowDialog();
    }

    private static void Studio(Window owner,Func<string,string> l,string language,string prefix)
    {
        var root=Path.Combine(_output,prefix+"-project"); Directory.CreateDirectory(root);
        var project=new LiteraryProject { LanguageCode=language,CreationBrief=Brief,ProjectName="Synthetic" };
        File.WriteAllText(Path.Combine(root,"project.json"),JsonSerializer.Serialize(project)); new LiteraryProjectLayout(root).Initialize();
        var chapters=new LiteraryChapterStore(root); chapters.Open(); chapters.Save("Рукопись не меняется.");
        new LiteraryCalibrationStore(root).MarkOpened();
        using var runtime=new LiteraryChatRuntime(root);
        var workspace=new LiteraryWorkspaceControl(new(project.Id,"Synthetic",root),project,l,preparedRuntime:runtime);
        workspace.GetType().GetField("_memoryStarted",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(workspace,true);
        owner.Content=workspace; Pump();
        var studio=Visual<LiteraryStudioControl>(owner).Single(); studio.State.RouteId="route/1"; studio.RefreshSources();
        var edit=Visual<Button>(owner).Single(b=>AutomationProperties.GetAutomationId(b)=="Studio.EditRoute");
        var bounds=edit.TranslatePoint(new Point(),studio);
        Check(bounds.X>studio.ActualWidth*.8 && bounds.Y>studio.ActualHeight*.85,"pencil parked bottom right");
        Inspect(()=>
        {
            var dialog=_app.Windows.OfType<LiteraryRouteEditorWindow>().Single();
            Get<TextBox>(dialog,"Route.Description").Text="UPDATED_ROUTE_BOUNDARY";
            Click(dialog,"Route.Add"); Get<TextBox>(dialog,"Route.Title").Text="ADDED_ROUTE_STAGE";
            Click(dialog,"Route.Save");
        });
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(studio.State.RouteId=="route/1","route editing does not move focus to new stage");
        Check(Visual<TextBlock>(studio).Any(t=>t.Text.Contains("UPDATED_ROUTE_BOUNDARY")),"current boundary refreshed immediately");
        Check(Visual<ComboBox>(studio).SelectMany(c=>c.Items.OfType<ComboBoxItem>()).Any(i=>i.Content?.ToString()?.Contains("ADDED_ROUTE_STAGE")==true),"new stage available immediately");
        var refreshed=LiteraryProjectStore.ReadProject(root);
        var snapshot=LiteraryEditorSnapshot.Capture(project.Id,root,chapters.Index,chapters.Load(),false);
        var catalog=new LiteraryParagraphCatalog(refreshed,snapshot,l);
        Check(catalog.Routes["route/1"].Description=="UPDATED_ROUTE_BOUNDARY" && catalog.Routes.ContainsKey("route/5"),"model catalog and future route share saved data");
        Check(chapters.Load()=="Рукопись не меняется.","manuscript preserved");
        Capture(owner,prefix+"-studio"); Check(workspace.CanLeave(),"workspace can leave after editing"); owner.Content=null; Pump();
    }

    private static void Check(bool condition,string text) { _checks++; if(!condition) throw new Exception(text); }
    private static IEnumerable<T> All<T>(DependencyObject root) { if(root is T item) yield return item; foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach(var found in All<T>(child)) yield return found; }
    private static IEnumerable<T> Visual<T>(DependencyObject root) { if(root is T item) yield return item; for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Visual<T>(VisualTreeHelper.GetChild(root,i))) yield return child; }
    private static T Get<T>(Window root,string id) where T:FrameworkElement => All<T>(root).Single(c=>AutomationProperties.GetAutomationId(c)==id);
    private static void Click(Window root,string id) { Get<Button>(root,id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Inspect(Action action) => _app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=> {try { action(); } catch(Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }}));
    private static void Pump() { var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) }; timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;}; timer.Start(); Dispatcher.PushFrame(frame); }
    private static void Capture(FrameworkElement element,string name) { var image=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(element);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(_output,name+".png"));png.Save(file); }
}
