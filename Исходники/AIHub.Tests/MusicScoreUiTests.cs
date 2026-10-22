using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class MusicScoreUiTests
{
    [TestMethod,DataRow("ru",true),DataRow("en",true),DataRow("ru",false),DataRow("en",false)]
    public Task ScoreWindowAndHorizontalToolbarFitBothThemes(string language,bool dark)=>Sta(async()=> {
        using var files=new MusicProjectTests.Files();var localization=new LocalizationService();localization.Load(language);
        using var view=new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root,"projects")),new MusicGenerationJobs(Path.Combine(files.Root,"jobs")),new MusicOutputPreferences(Path.Combine(files.Root,"output.json")));
        view.Generation.ConfigureVariation(MusicStudioRuntime.Variation,new MusicExpertSettings{Variation=MusicStudioRuntime.Variation});
        view.Editor.Lyrics="[Verse]\nHello\n[Chorus]\nWorld";view.Localize(localization.T);view.Projects.RememberDefaults();
        await view.Scores.AddAsync(MusicScoreTests.Abc,"import");
        var theme=ScenarioNavigationTests.Create(language,1,dark).Resources;
        var host=new Window{Content=view,Width=1400,Height=850,ShowInTaskbar=false};
        var window=new MusicScoreWindow(view.Scores,localization.T){ShowInTaskbar=false};
        foreach(var key in theme.Keys){host.Resources[key]=theme[key];window.Resources[key]=theme[key];}
        host.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AIHub;component/Controls/SettingsResources.xaml",UriKind.Relative)});
        foreach(var resources in new[]{host.Resources,window.Resources}) {
            resources["WindowBackgroundBrush"]=dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16,24,39)):System.Windows.Media.Brushes.WhiteSmoke;
            resources["SecondaryButtonBackgroundBrush"]=resources["WindowBackgroundBrush"];
        }
        try {
            host.Show();window.Owner=host;window.Show();await Task.Delay(150);window.UpdateLayout();host.UpdateLayout();
            Assert.IsTrue(view.Generation.ScorePlansButton.IsEnabled);
            var tools=(System.Windows.Controls.Panel)view.Generation.ScorePlansButton.Parent;
            Assert.AreEqual(1,tools.Children.OfType<System.Windows.Controls.Button>().Count(button=>
                System.Windows.Automation.AutomationProperties.GetAutomationId(button).StartsWith("Music.Score.Open.",StringComparison.Ordinal)));
            Assert.IsTrue(window.ActualWidth>=760);Assert.IsTrue(window.ActualHeight>=560);
            Assert.AreEqual(760,window.MinWidth);Assert.AreEqual(560,window.MinHeight);
            Assert.IsNotNull(window.TryFindResource(typeof(System.Windows.Controls.TabItem)));
            Assert.IsNotNull(window.TryFindResource(typeof(System.Windows.Controls.ListBox)));
            if(Environment.GetEnvironmentVariable("LOPATA_SCORE_UI_PROOF") is {Length:>0} output) {
                Directory.CreateDirectory(output);
                Snapshot(window,Path.Combine(output,$"{language}-{dark}-plans.png"));
                window.SelectTab(1);window.UpdateLayout();Snapshot(window,Path.Combine(output,$"{language}-{dark}-editor.png"));
                window.SelectTab(2);window.UpdateLayout();Snapshot(window,Path.Combine(output,$"{language}-{dark}-sections.png"));
                Snapshot(host,Path.Combine(output,$"{language}-{dark}-toolbar.png"));
            }
            window.Close();view.Scores.Open(2);
            var opened=host.OwnedWindows.OfType<MusicScoreWindow>().Single();
            view.Scores.Open();Assert.AreEqual(2,opened.SelectedTab);
            opened.Close();view.Scores.Open();
            Assert.AreEqual(2,host.OwnedWindows.OfType<MusicScoreWindow>().Single().SelectedTab);
        }finally{window.Close();foreach(var owned in host.OwnedWindows.OfType<MusicScoreWindow>().ToArray())owned.Close();host.Close();}
    });
    private static void Snapshot(Window window,string path) {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static Task Sta(Func<Task> test) {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>{try{await test();completion.SetResult();}catch(Exception error){completion.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();});thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}
