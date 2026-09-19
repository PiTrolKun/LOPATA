using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

internal static class PulseProbe
{
    [STAThread] static void Main()
    {
        var replay=Environment.GetEnvironmentVariable("PULSE_REPLAY_DIRECTORY");
        var output=Path.GetFullPath((replay is null ? "Тесты/LiteraryLayoutPulse/runs/" : "_backups/layout_replay_")+DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(output);
        using var log=new StreamWriter(Path.Combine(output,"layout.txt")) { AutoFlush=true };
        var directory=Program.Create(output);
        if(replay is not null)
        {
            var original=LiteraryProjectStore.ReadProject(replay); original.Id=LiteraryProjectStore.ReadProject(directory).Id;
            File.WriteAllText(Path.Combine(directory,"project.json"),JsonSerializer.Serialize(original));
            Directory.CreateDirectory(Path.Combine(directory,"Dialogs","Studio"));
            var session=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(replay,"Dialogs","Studio","session.json")))!;
            session["ProjectId"]=original.Id;
            File.WriteAllText(Path.Combine(directory,"Dialogs","Studio","session.json"),session.ToJsonString());
            var chapters=new LiteraryChapterStore(directory); chapters.Open();
            chapters.Save(File.ReadAllText(Directory.GetFiles(Path.Combine(replay,"chapters"),"*.txt").First()));
        }
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var xml=XDocument.Load("Исходники/AIHub/MainWindow.xaml"); XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation", x="http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary=new XElement(ns+"ResourceDictionary",xml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration));
        foreach(var e in xml.Root.Element(ns+"Window.Resources")!.Elements())
            if(e.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[]{"PrimaryButtonStyle","SecondaryButtonStyle"}.Contains((string?)e.Attribute(x+"Key"))) dictionary.Add(new XElement(e));
        var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        foreach(var key in resources.Keys.OfType<string>().ToArray())
            if(resources[key] is double value && (key.Contains("Font") || key.StartsWith("UiLineHeight"))) resources[key]=value*1.25;
        resources["UiButtonHeight"]=50d;
        var translations=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Исходники/AIHub/Localization/ru.json"))!;
        string L(string key)=>translations.GetValueOrDefault(key,key);
        using var runtime=new LiteraryChatRuntime(directory);
        var project=LiteraryProjectStore.ReadProject(directory);
        var workspace=new LiteraryWorkspaceControl(new LiteraryProjectEntry(project.Id,"Pulse probe",directory),project,L,preparedRuntime:runtime);
        workspace.GetType().GetField("_memoryStarted",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(workspace,true);
        var shell=new Grid(); shell.RowDefinitions.Add(new(){Height=new GridLength(80)}); shell.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)}); shell.RowDefinitions.Add(new(){Height=new GridLength(0)});
        var page=Program.Shell(workspace,xml,resources); Grid.SetRow(page,1); shell.Children.Add(page);
        var win=new Window { Width=1300,Height=740,Left=200,Top=100,Resources=resources,Content=shell,UseLayoutRounding=true,FontFamily=new FontFamily("Segoe UI Variable Display, Segoe UI"),Title="Literary layout probe",ShowInTaskbar=false };
        win.SetResourceReference(Window.FontSizeProperty,"UiBodyFontSize");
        var stage="starting"; var layouts=0; string last=""; var samples=0;
        using var watchdog=new System.Threading.Timer(_=> {Console.WriteLine($"TIMEOUT stage={stage} layouts={layouts} last={last}");Environment.Exit(3);},null,45000,System.Threading.Timeout.Infinite);
        win.Show();
        var studio=Program.All<LiteraryStudioControl>(workspace).Single();
        var activity=(LiteraryRequestIndicator)Program.Field(studio,"_activity")!;
        activity.ShowActivity(L("Studio.Activity.Reply"));
        if(replay is null) { studio.State.Clear(); studio.State.Add("User","Хочу обсудить начало сцены."); studio.State.Add("Advisor",string.Join("\n\n",Enumerable.Repeat("Герой стоит у двери мастерской. Он слышит звон и медлит с ответом. Мы остаёмся в текущей сцене: можно показать его характер через жест и короткий разговор с соседом. Какое действие он выбирает и что меняется для него в этот момент?",12))); Program.Call(studio,"Render"); }
        var right=((Grid)studio.Content).Children.OfType<Grid>().Single(g=>Grid.GetColumn(g)==2);
        // A/B control recreates the previous fractional fixed row without editing production files.
        if(Environment.GetEnvironmentVariable("PULSE_UNROUNDED_CONTROL")=="1")
            right.SizeChanged+=(_,_)=>right.RowDefinitions[2].Height=new GridLength(Math.Max(140,right.ActualHeight*.5));
        var scroll=(ScrollViewer)Program.Field(studio,"_scroll")!;
        var input=(TextBox)Program.Field(studio,"_input")!;
        ((TextBlock)Program.Field(studio,"_status")!).Text=L("Paragraph.Working");
        ((TextBlock)Program.Field(studio,"_tokens")!).Text=L("Paragraph.Tokens")+" 3538 / 55552";
        ((TextBlock)Program.Field(workspace,"_memoryStatus")!).Text=L("Literary.Rag.Ready");
        win.LayoutUpdated+=(_,_)=>
        {
            layouts++;
            var current=$"{stage} win={win.ActualWidth:F3}x{win.ActualHeight:F3} studio={studio.ActualWidth:F3}x{studio.ActualHeight:F3} right={right.ActualHeight:F3} rows={string.Join('/',right.RowDefinitions.Select(r=>r.ActualHeight.ToString("F3")))} input={input.ActualHeight:F3} scroll={scroll.ViewportHeight:F3}/{scroll.ExtentHeight:F3} offset={scroll.VerticalOffset:F3}";
            if(current!=last && samples++<120) log.WriteLine(current);
            last=current;
        };
        var timer=new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromSeconds(1) };
        var step=0; var previous=0; var failed=false; var cpu=Process.GetCurrentProcess().TotalProcessorTime;
        var inputBottom=0d; var smallInputHeight=0d;
        timer.Tick+=(_,_)=>
        {
            var count=layouts-previous; previous=layouts;
            var nowCpu=Process.GetCurrentProcess().TotalProcessorTime; var usedCpu=(nowCpu-cpu).TotalMilliseconds; cpu=nowCpu;
            log.WriteLine($"OBSERVE stage={stage} cpuMs={usedCpu:F0} passes={count} DPI={VisualTreeHelper.GetDpi(win).DpiScaleX} valid={right.IsMeasureValid}/{right.IsArrangeValid} rows={string.Join('/',right.RowDefinitions.Select(r=>r.ActualHeight.ToString("F3")))} input={input.ActualHeight:F3} {last}");
            if(step>0 && (count>30 || !right.IsMeasureValid || !right.IsArrangeValid)) failed=true;
            if(step==4 && Math.Abs(scroll.VerticalOffset-30)>.5) failed=true;
            if(step==5 && (input.ActualHeight<=smallInputHeight || Math.Abs(input.TranslatePoint(new Point(0,input.ActualHeight),studio).Y-inputBottom)>.5)) failed=true;
            switch(step++)
            {
                case 0: stage="normal idle"; break;
                case 1: stage="maximize"; win.WindowState=WindowState.Maximized; break;
                case 2: stage="maximized idle"; break;
                case 3: stage="scroll up"; scroll.ScrollToVerticalOffset(30); break;
                case 4: inputBottom=input.TranslatePoint(new Point(0,input.ActualHeight),studio).Y; smallInputHeight=input.ActualHeight; stage="grow input"; input.Text=string.Join('\n',Enumerable.Repeat("Строка ввода",6)); break;
                case 5: stage="clear input"; input.Clear(); break;
                case 6: stage="restore"; win.WindowState=WindowState.Normal; break;
                case 7: stage="maximize again"; win.WindowState=WindowState.Maximized; break;
                case 8: stage="long footer"; ((TextBlock)Program.Field(studio,"_receipts")!).Text=string.Join('\n',Enumerable.Repeat("Источник: сведения получены, проверяем размещение длинного состояния.",12)); break;
                default:
                    if(step<26) { stage="footer inset "+((step-10)*.5); shell.RowDefinitions[2].Height=new GridLength((step-10)*.5); break; }
                    timer.Stop(); activity.Stop(); workspace.CanLeave(); win.Close(); Console.WriteLine($"{(failed?"FAIL":"PASS")} layout stability {output}"); Environment.ExitCode=failed?1:0; app.Shutdown(); break;
            }
        };
        timer.Start(); app.Run();
    }
}
