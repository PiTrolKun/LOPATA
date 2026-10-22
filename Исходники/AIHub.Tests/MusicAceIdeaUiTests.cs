using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.MusicAceIdeas;
using Button=System.Windows.Controls.Button;
using Panel=System.Windows.Controls.Panel;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class MusicAceIdeaUiTests
{
    [TestMethod,DataRow("ru",true),DataRow("en",true),DataRow("ru",false),DataRow("en",false)]
    public Task OneEntryNewProjectAndBothThemesWork(string language,bool dark)=>ScenarioNavigationTests.Sta(()=>{
        using var files=new MusicProjectTests.Files();using var view=new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root,"projects")),
            new MusicGenerationJobs(Path.Combine(files.Root,"jobs")),new MusicOutputPreferences(Path.Combine(files.Root,"output.json")));
        var localize=new LocalizationService();localize.Load(language);view.Localize(localize.T);
        view.Generation.ConfigureVariation(MusicAceCatalog.Variation,MusicAceCatalog.Defaults());view.Projects.RememberDefaults();
        view.Generation.AceIdeas=MusicAceIdeaTests.Workspace(MusicAceIdeaTests.Idea()) with {Retake=true};
        var host=new Window{Content=view,Width=1400,Height=850,ShowInTaskbar=false};
        var window=new MusicAceIdeaWindow(view.AceIdeas,localize.T){ShowInTaskbar=false};
        var theme=ScenarioNavigationTests.Create(language,1,dark).Resources;
        foreach(var key in theme.Keys){host.Resources[key]=theme[key];window.Resources[key]=theme[key];}
        foreach(var resources in new[]{host.Resources,window.Resources}) {
            resources["WindowBackgroundBrush"]=dark?new SolidColorBrush(Color.FromRgb(16,24,39)):Brushes.WhiteSmoke;
            resources["SecondaryButtonBackgroundBrush"]=resources["WindowBackgroundBrush"];
        }
        host.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AIHub;component/Controls/SettingsResources.xaml",UriKind.Relative)});
        try {
            host.Show();window.Owner=host;window.Show();host.UpdateLayout();window.UpdateLayout();
            Assert.IsTrue(view.Generation.ScorePlansButton.IsEnabled);
            var tools=(Panel)view.Generation.ScorePlansButton.Parent;
            Assert.AreEqual(1,tools.Children.OfType<Button>().Count(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("Music.Score.Open.",StringComparison.Ordinal)));
            Assert.IsNotNull(window.TryFindResource(typeof(System.Windows.Controls.ListBox)));
            if(Environment.GetEnvironmentVariable("LOPATA_ACE_IDEA_UI_PROOF") is {Length:>0} output) {
                Directory.CreateDirectory(output);Snapshot(window,Path.Combine(output,$"{language}-{dark}-ideas.png"));Snapshot(host,Path.Combine(output,$"{language}-{dark}-toolbar.png"));
            }
            view.Generation.SetOptions(view.Generation.Options with {DurationSeconds=20});view.Localize(localize.T);Assert.AreEqual(20,view.Generation.Options.DurationSeconds);
            view.Projects.New();Assert.HasCount(0,view.Generation.AceIdeas.Ideas);Assert.IsFalse(view.Generation.AceIdeas.Enabled);
            view.Generation.ConfigureVariation(MusicDiffRhythmCatalog.Variation,MusicModelVariants.Defaults(MusicDiffRhythmCatalog.Variation));Assert.IsFalse(view.Generation.ScorePlansButton.IsEnabled);
        }finally{window.Close();host.Close();}
    });
    private static void Snapshot(Window window,string path) {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
