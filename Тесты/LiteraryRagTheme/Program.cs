using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Services;

// Visual diagnostic: isolated documents, no index rebuilding or model calls.
internal static class RagThemeProbe
{
    [STAThread] static void Main()
    {
        var output=Path.GetFullPath("Тесты/LiteraryRagTheme/runs/"+DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(output);
        var project=Program.Create(output); var store=new LiteraryChapterStore(project); store.Open(); store.Finish();
        var source=new LiteraryProjectLayout(project).EnsureFolder("Rag/Source");
        File.WriteAllText(Path.Combine(source,"text.json"),JsonSerializer.Serialize(new[]{
            new LiterarySourceSection("Книга","Глава 1","У двери мастерской горел фонарь. Герой остановился, прислушиваясь к шагам внутри."),
            new LiterarySourceSection("Заметки","Устройство мира","Мастерская закрывается после заката. Ключ хранит смотритель.")}));
        var empty=Program.Create(Path.Combine(output,"empty"));
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var xml=XDocument.Load("Исходники/AIHub/MainWindow.xaml");
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary=new XElement(ns+"ResourceDictionary",xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var element in xml.Root.Element(ns+"Window.Resources")!.Elements())
            if(element.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)element.Attribute(x+"Key"))) dictionary.Add(new XElement(element));
        foreach(var dark in new[]{true,false}) foreach(var lang in new[]{"ru","en"})
        {
            var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            foreach(var pair in new[]{("SecondaryButtonBackgroundBrush",dark?"#111827":"#F8F8F8"),("WindowBackgroundBrush",dark?"#111827":"#F3F3F3"),
                ("PanelBrush",dark?"#172033":"#FFFFFF"),("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#AAB4C4":"#5D6470"),
                ("LineBrush",dark?"#2D374B":"#DADDE3"),("StepBadgeBrush",dark?"#1E3A5F":"#EAF1FF")})
                resources[pair.Item1]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            var owner=new Window {Resources=resources}; app.MainWindow=owner;
            var translations=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
            string L(string key)=>translations.GetValueOrDefault(key,key);
            var quoted="";
            var window=new LiteraryRagEditorWindow(project,L,lang,(_,text)=>quoted=text) {ShowInTaskbar=false};
            window.Show(); Program.Pump();
            var tabs=Program.All<TabControl>(window).Single();
            for(var index=0;index<2;index++)
            {
                tabs.SelectedIndex=index; Program.Pump();
                var combo=Program.All<ComboBox>(window).Single(); var editor=Program.All<TextBox>(window).Single();
                if(editor.Text.Length==0) throw new Exception("Fixture text was not loaded");
                editor.Select(0,Math.Min(10,editor.Text.Length));
                Program.All<Button>(window).Single(b=>Equals(b.Content,L("Studio.Quote"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(quoted!=editor.SelectedText) throw new Exception("Quote selection changed");
                Capture(window,$"{lang}-{dark}-tab{index}");
                combo.IsDropDownOpen=true; Program.Pump();
                var popup=(Popup)combo.Template.FindName("PART_Popup",combo);
                Capture((FrameworkElement)popup.Child,$"{lang}-{dark}-list{index}");
                combo.IsDropDownOpen=false;
                if(index==0)
                {
                    combo.SelectedIndex=1; Program.Pump();
                    if(!editor.Text.StartsWith("Мастерская")) throw new Exception("Section selection failed");
                }
            }
            if(resources.Contains(typeof(TabControl)) || resources.Contains(typeof(ComboBox))) throw new Exception("Editor styles leaked to main window");
            window.Close();
            if(dark && lang=="ru")
            {
                window=new LiteraryRagEditorWindow(empty,L,lang,(_,_)=>{}) {ShowInTaskbar=false};window.Show(); Program.Pump();
                Program.All<TabControl>(window).Single().SelectedIndex=1;Program.Pump();Capture(window,"empty-project-dark");window.Close();
            }
        }
        File.WriteAllText(Path.Combine(output,"result.txt"),"PASS: both tabs, dropdowns, RU/EN, dark/light, empty project, selection and quoting; no model/index calls.");
        Console.WriteLine("PASS "+output);app.Shutdown();
        void Capture(FrameworkElement element,string name)
        {
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(element);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);
        }
    }
}
