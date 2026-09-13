using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Services;

class Program
{
    static IEnumerable<T> All<T>(DependencyObject root) where T:DependencyObject
    {if(root is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var c in All<T>(VisualTreeHelper.GetChild(root,i)))yield return c;}
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread] static void Main()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var source=XDocument.Load(@"H:\AI_HUB\Исходники\AIHub\MainWindow.xaml");
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dict=new XElement(ns+"ResourceDictionary",source.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var e in source.Root.Element(ns+"Window.Resources")!.Elements())
            if(e.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || (e.Name.LocalName=="Style" && new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)e.Attribute(XName.Get("Key","http://schemas.microsoft.com/winfx/2006/xaml")))))dict.Add(new XElement(e));
        app.Resources=(ResourceDictionary)XamlReader.Parse(dict.ToString());
        var parent=Path.Combine(@"H:\AI_HUB\_tmp\interview_recent_ui",DateTime.Now.ToString("yyyyMMddHHmmss"));Directory.CreateDirectory(parent);
        foreach(var lang in new[]{"ru","en"})foreach(var dark in new[]{false,true})
        {
            app.Resources["TextPrimaryBrush"]=new SolidColorBrush(dark?Colors.White:Colors.Black);
            app.Resources["PanelBrush"]=new SolidColorBrush(dark?Colors.Black:Colors.White);
            var local=Path.Combine(parent,lang+dark);Directory.CreateDirectory(local);
            var recent=new LiteraryInterviewRecentStore(Path.Combine(local,"recent.json"));
            for(int i=1;i<=4;i++){using var s=new LiteraryInterviewSession(lang,local,recent);s.State.ProjectName="Draft "+i;s.Reserve();s.State.Step=12;s.State.Inputs[12]="Saved answer";s.Save();}
            var words=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($@"H:\AI_HUB\Исходники\AIHub\Localization\{lang}.json"))!;
            string L(string key)=>words[key];
            var nav=new LiteraryNavigationControl();
            typeof(LiteraryNavigationControl).GetField("_recent",Flags)!.SetValue(nav,recent);
            typeof(LiteraryNavigationControl).GetField("_store",Flags)!.SetValue(nav,new LiteraryProjectStore(Path.Combine(local,"projects.json")));
            nav.Configure(L,true,lang,local);
            var window=new Window{Width=1100,Height=800,Left=-18000,Top=-18000,ShowInTaskbar=false,Content=nav};window.Show();
            void Click(string key){window.UpdateLayout();All<Button>(nav).Single(b=>Equals(b.Content,L(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();}
            Click("Literary.New");
            if(!All<TextBlock>(nav).Any(t=>t.Text==L("Literary.Interview.NewStart")))throw new Exception("Missing choice");
            Click("Literary.Select");
            var buttons=All<Button>(nav).Where(b=>Equals(b.Content,L("Literary.Continue"))).ToArray();
            if(buttons.Length!=3)throw new Exception("Wrong recent count");
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
            if(!All<TextBox>(nav).Any(b=>b.Text=="Saved answer"))throw new Exception("Draft did not resume");
            if(All<TextBlock>(nav).Any(t=>t.Text.Contains("F1")))throw new Exception("Test navigation still shown");
            Click("Literary.Interview.Pause");window.UpdateLayout();
            if(All<Button>(nav).Count(b=>Equals(b.Content,L("Literary.Continue")))!=3)throw new Exception("Pause list broken");
            nav.GoBack();window.UpdateLayout();Click("Literary.Start");
            if(!All<TextBlock>(nav).Any(t=>t.Text==L("Literary.Interview.Expert")))throw new Exception("New mode choice missing");
            window.Close();Console.WriteLine("PASS recent navigation, resume, pause, new modes: "+lang+" dark="+dark);
        }
        app.Shutdown();
    }
}
