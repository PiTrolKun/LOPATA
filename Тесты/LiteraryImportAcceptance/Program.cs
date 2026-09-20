using System.IO;
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
using AIHub.Services.LiteraryImport;

internal static class Program
{
    private static int _checks;
    [STAThread] private static void Main()
    {
        var output=Path.GetFullPath("Тесты/LiteraryImportAcceptance/runs/"+DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(output);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var xml=XDocument.Load("Исходники/AIHub/MainWindow.xaml");
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation", x="http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary=new XElement(ns+"ResourceDictionary",xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var element in xml.Root.Element(ns+"Window.Resources")!.Elements())
            if(element.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)element.Attribute(x+"Key"))) dictionary.Add(new XElement(element));
        foreach(var dark in new[]{true,false}) foreach(var language in new[]{"ru","en"})
        {
            var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            foreach(var (key,value) in new[]{("WindowBackgroundBrush",dark?"#111827":"#FFFFFF"),("PanelBrush",dark?"#172033":"#FFFFFF"),
                ("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#AAB4C4":"#5D6470"),
                ("LineBrush",dark?"#2D374B":"#DADDE3"),("SecondaryButtonBackgroundBrush",dark?"#111827":"#F8F8F8")})
                resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            var owner=new Window { Resources=resources, ShowInTaskbar=false, Width=400,Height=300 }; owner.Show();
            var texts=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{language}.json"))!;
            string L(string key)=>texts.GetValueOrDefault(key,key);
            var label=language+(dark?"-dark":"-light");
            Exception? failure=null;
            void Drive(Action action) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
            { try { action(); } catch(Exception ex) { failure=ex; foreach(var w in app.Windows.OfType<Window>().Where(w=>w!=owner).ToArray()) w.Close(); } }));
            string Key(string key)=>L("Literary.Import."+key);
            var units=new[]{new ImportUnit("a","c","m","","RESPONSE",0,0,"Synthetic book excerpt.",false),new ImportUnit("b","c","n","","RESPONSE",0,0,"Synthetic continuation.",false)};
            var decisions=new[]{new ImportDecision("a","KEEP","Легенда о Синем и Дальнем Море","One",""),new ImportDecision("b","KEEP","Легенда о Синим и Дальнем Море","One","")};
            var input=new ImportInput([new("c","Synthetic",2)],units.ToList(),[],[]);
            var names=new LiteraryImportWorkNamesWindow(owner,input,decisions,L);
            Drive(()=> { Click(names,Key("FindSimilar")); Check(All<ListBox>(names).Single().SelectedItems.Count==2,"alias suggestions"); Shot(names,output,label+"-groups"); Click(names,Key("MergeSelected")); Click(names,Key("SaveGroups")); });
            Check(names.ShowDialog()==true,"group dialog saved"); if(failure is not null) throw failure;
            Check(names.Decisions.Select(d=>d.Project).Distinct().Count()==1,"all units merged");
            var dir=Path.Combine(output,label); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"source.json"),"[]");
            using var session=ImportSession.Create(dir,Path.Combine(dir,"source.json"),default); session.State.SelectedProject="Synthetic";
            var entry=ImportProjectBuilder.Build(session,input,decisions.Select(d=>d with {Kind="DOUBT",Reason="Synthetic review"}).ToArray(),new(Path.Combine(dir,"projects.json")),dir,"Book","test",language);
            var review=new LiteraryImportReviewWindow(owner,entry.ProjectPath,L);
            Drive(()=>
            {
                Shot(review,output,label+"-review");
                Drive(()=>
                {
                    var edit=app.Windows.OfType<Window>().Single(w=>w.Title==Key("EditPart"));
                    var field=All<TextBox>(edit).Single(); field.Text="Remove. Keep."; field.Select(0,8); Click(edit,Key("RemoveSelected"));
                    Check(field.Text=="Keep.","delete selected text"); Shot(edit,output,label+"-edit"); Click(edit,Key("SaveReviewed"));
                });
                Click(review,Key("EditPart")); Check(review.Changed,"review persisted"); review.Close();
            });
            review.ShowDialog(); if(failure is not null) throw failure;
            var chapters=new LiteraryChapterStore(entry.ProjectPath); chapters.Open(); Check(chapters.Snapshot().Single().Parts.Single()=="Keep.","exact edited text");
            const string quote="Synthetic exact quote.";
            var fact=new LiteraryJellyFact {Subject="Hero",Relation="said",Evidence="wrong quote"}; bool committed=false;
            Drive(()=>
            {
                var dialog=app.Windows.OfType<Window>().Single(w=>w.Title==L("Literary.Jelly.Title"));
                var evidence=All<TextBox>(dialog).Single(t=>t.Text=="wrong quote"); Check(evidence.Foreground is SolidColorBrush b && b.Color.R>b.Color.G,"immediate invalid quote highlight");
                Drive(()=>
                {
                    var source=app.Windows.OfType<Window>().Single(w=>w.Title=="[001]"); var field=All<TextBox>(source).Single(); field.SelectAll();
                    Click(source,L("Literary.Jelly.UseQuote")); source.Close();
                });
                Click(dialog,L("Literary.Jelly.Source")); Check(evidence.Text==quote,"source selection copied exactly"); Shot(dialog,output,label+"-memory"); Click(dialog,L("Literary.Jelly.Confirm"));
            });
            Check(LiteraryJellyReviewDialog.Show(owner,L,[new(fact,"001",quote)],f=> { committed=f[0].Evidence==quote; return Task.CompletedTask; },language:language,summary:"Synthetic: 1 / 2"),"memory dialog approved");
            if(failure is not null) throw failure; Check(committed,"memory callback received exact quote"); owner.Close();
        }
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { checks=_checks, result="PASS", synthetic=true }));
        Console.WriteLine($"PASS {_checks}: {output}"); app.Shutdown();
    }
    private static void Check(bool ok,string message) { _checks++; if(!ok) throw new Exception(message); }
    private static void Click(DependencyObject root,string title)=>All<Button>(root).Single(b=>Equals(b.Content,title)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> All<T>(DependencyObject root) where T:DependencyObject
    { if(root is T t) yield return t; for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var c in All<T>(VisualTreeHelper.GetChild(root,i))) yield return c; }
    private static void Shot(Window window,string folder,string name)
    { window.UpdateLayout(); var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(window); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file=File.Create(Path.Combine(folder,name+".png")); png.Save(file); }
}
