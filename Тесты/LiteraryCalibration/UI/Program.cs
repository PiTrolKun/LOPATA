using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Xml.Linq;
using System.Text.Json;
using System.IO;
using AIHub.Controls;
using AIHub.Services;
class Probe {
 static IEnumerable<T> All<T>(DependencyObject o) { if(o is T t)yield return t; for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++)foreach(var a in All<T>(VisualTreeHelper.GetChild(o,i)))yield return a; }
 [STAThread] static void Main() {
  Directory.CreateDirectory("Тесты/LiteraryCalibration/runs/ui");
  var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
  var doc=XDocument.Load(Path.GetFullPath("Исходники/AIHub/MainWindow.xaml"));XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
  var rd=new XElement(ns+"ResourceDictionary",doc.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
  foreach(var e in doc.Root.Element(ns+"Window.Resources")!.Elements()) if(e.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)e.Attribute(x+"Key")))rd.Add(new XElement(e));
  var owner=new Window {Resources=(ResourceDictionary)XamlReader.Parse(rd.ToString())};app.MainWindow=owner;
  foreach(var lang in new[]{"ru","en"}) foreach(var dark in new[]{false,true}) {
   foreach(var pair in new[]{("SecondaryButtonBackgroundBrush",dark?"#111827":"#F8F8F8"),("WindowBackgroundBrush",dark?"#111827":"#F3F3F3"),("PanelBrush",dark?"#172033":"#FFFFFF"),("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#AAB4C4":"#5D6470"),("LineBrush",dark?"#2D374B":"#DADDE3")}) owner.Resources[pair.Item1]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
   var dict=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
   string L(string key)=>dict[key];string saved="";
   var initial=JsonSerializer.Serialize(new {kind="intent", sections=new[]{new {Topic=1,Question="Для какого читателя пишем?",Text="История неудачливого инженера, который попадает в комичные ситуации из-за своего любопытства."},new {Topic=1,Question="QUESTION MUST BE HIDDEN",Text="Сатира, комедия и приключения. Каждая глава — отдельная история."},new {Topic=2,Question="QUESTION MUST BE HIDDEN",Text="Современный небольшой город. Реальные технологии и неожиданные последствия экспериментов."}},route=new[]{new {Number=1,Title="Начало",Description="Знакомство с героем"}}});
   var win=new LiteraryCalibrationWindow(L,initial,lang,t=>saved=t,(r,ct)=>Task.FromResult(new CalibrationResult([new(new("f0",0,7),"Тестовая отметка",[],[])],false))){Left=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false};win.Show();win.UpdateLayout();
   if(!All<TextBlock>(win).Any(t=>t.Text==L("Literary.Calibration.Field7")) || !All<TextBlock>(win).Any(t=>t.Text=="QUESTION MUST BE HIDDEN"))throw new Exception("Missing category or adaptive question");
   var inputs=All<TextBox>(win).Where(b=>!Equals(b.Tag,"CalibrationAnalysisRequest")).ToArray(); if(inputs.Length!=5)throw new Exception("Field count");
   var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render((Visual)win.Content);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create($@"Тесты\LiteraryCalibration\runs\ui\groups-{lang}-{dark}.png"))encoder.Save(file);
      var panels=All<LiteraryCalibrationAnalysisPanel>(win).ToArray(); if(panels.Length!=4)throw new Exception("Analysis panels");
   for(int pi=0;pi<panels.Length;pi++){var buttons=All<Button>(panels[pi]).ToArray(); if(buttons.Length!=(pi==3?14:6)||buttons.Where(b=>b.Visibility==Visibility.Visible).Any(b=>!b.IsEnabled || b.ToolTip is null))throw new Exception("Analysis buttons");}
   All<Button>(panels[0]).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));win.UpdateLayout();
   var marked=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32);marked.Render((Visual)win.Content);var markEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();markEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(marked));using(var mf=File.Create($@"Тесты\LiteraryCalibration\runs\ui\marked-{lang}-{dark}.png"))markEncoder.Save(mf);
   var request=All<TextBox>(panels[0]).Single();request.Text="Проверить смысл";
   All<ScrollViewer>(win).First().ScrollToEnd();win.UpdateLayout();
   var bottom=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32);bottom.Render((Visual)win.Content);var ep=new System.Windows.Media.Imaging.PngBitmapEncoder();ep.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bottom));using(var f=File.Create($@"Тесты\LiteraryCalibration\runs\ui\analysis-{lang}-{dark}.png"))ep.Save(f);
   var input=inputs[0]; input.Text="Исправленный результат\n"+new string('я',14000);
   All<Button>(win).Single(b=>Equals(b.Content,L("Literary.Anchor.Save"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if(JsonDocument.Parse(saved).RootElement.GetProperty("sections")[0].GetProperty("Text").GetString()!=input.Text || !SpellCheck.GetIsEnabled(input))throw new Exception("Editor save/spelling"); win.Close();
   Console.WriteLine("PASS "+lang+" edit/save/close 14000+ chars");
  }
  var ru=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Исходники/AIHub/Localization/ru.json"))!; Mechanics.Run(k=>ru[k]);
  owner.Close();app.Shutdown();
 }
}
