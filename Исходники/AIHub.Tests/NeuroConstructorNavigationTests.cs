using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.NeuroConstructor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class NeuroConstructorNavigationTests
{
    [TestMethod]
    [DataRow("ru", 1120, true)]
    [DataRow("en", 520, false)]
    public Task OpensFromTestsAndStoresTheCellChosenOnThePicture(string language, int width, bool dark) =>
        ScenarioNavigationTests.Sta(() =>
    {
        var root = Directory.CreateTempSubdirectory("lopata-neuro-ui-");
        using var projectRoot = NeuroFiles.UseRoot(root.FullName);
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var nav = ScenarioNavigationTests.Create(language, 1, dark);
        var window = new Window
        {
            Content = nav, Width = width, Height = 720, Left = -15000, Top = 0,
            ShowInTaskbar = false, ShowActivated = false
        };
        window.Background = dark ? new SolidColorBrush(Color.FromRgb(17,24,39)) : Brushes.WhiteSmoke;
        nav.Resources["WindowBackgroundBrush"] = window.Background;
        nav.Resources["SecondaryButtonBackgroundBrush"] = nav.Resources["PanelBrush"];
        try
        {
            var p = NeuroService.New();
            var bitmap = BitmapSource.Create(8,8,96,96,PixelFormats.Bgra32,null,new byte[256],32);
            var path = Path.Combine(root.FullName, "marked.png");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) png.Save(file);
            NeuroService.AddExample(p,path,"Original request",0);
            var localizer = new LocalizationService();
            localizer.Load(language);
            window.Show();
            nav.SelectDirection(ScenarioNavigationCatalog.Experiments);
            var cards = ScenarioNavigationTests.LogicalDescendants(nav).OfType<Button>()
                .Where(b => b.Tag is string).ToList();
            var first = cards.FindIndex(b => Equals(b.Tag,ScenarioNavigationCatalog.Sandbox));
            Assert.IsTrue(first >= 0 && first + 1 < cards.Count);
            var entry = cards[first + 1];
            Assert.AreEqual(ScenarioNavigationCatalog.NeuroConstructor,entry.Tag);
            Assert.AreEqual(localizer.T("Neuro.Title"), AutomationProperties.GetName(entry));
            Click(entry);
            window.UpdateLayout();
            var view = nav.NeuroConstructorPage!;
            Assert.AreSame(view,nav.Content);
            Assert.AreEqual(ScenarioNavigationCatalog.NeuroConstructor,nav.RecoveryScenarioId);
            Assert.IsTrue(view.ActualWidth > 0 && view.ActualWidth <= width);
            var tabs = (TabControl)view.FindName("Pages");
            var modeChoice = (FrameworkElement)view.FindName("ModeChoice");
            var auto = (NeuroAutoView)view.FindName("AutoView");
            Assert.IsTrue(modeChoice.IsVisible);
            Assert.IsFalse(tabs.IsVisible || auto.IsVisible);
            var autoEntry = (Button)view.FindName("AutoMode");
            Assert.AreEqual(localizer.T("Neuro.AutoMode"),AutomationProperties.GetName(autoEntry));
            Assert.AreEqual(localizer.T("Neuro.ProMode"),AutomationProperties.GetName((Button)view.FindName("ProMode")));
            Capture(view,language+"-modes");
            Click(autoEntry);
            window.UpdateLayout();
            Assert.IsTrue(auto.IsVisible);
            Assert.IsFalse(tabs.IsVisible);
            var autoProject = NeuroService.OpenAuto();
            Assert.AreNotEqual(autoProject.Id,p.Id);
            Assert.AreEqual(p.Id,NeuroService.Open().Id);
            var languagePlace = (ComboBox)auto.FindName("LanguagePlace");
            var visionPlace = (ComboBox)auto.FindName("VisionPlace");
            var speechPlace = (ComboBox)auto.FindName("SpeechPlace");
            Assert.IsFalse(speechPlace.IsEnabled);
            var tuning = (ComboBox)auto.FindName("Tuning");
            Assert.AreEqual(0,tuning.SelectedIndex);
            Assert.IsFalse(((FrameworkElement)auto.FindName("CycleEditor")).IsVisible);
            ((CheckBox)auto.FindName("IncludeSpeech")).IsChecked = true;
            Assert.IsTrue(speechPlace.IsEnabled);
            tuning.SelectedIndex = 1;
            ((TextBox)auto.FindName("CycleCount")).Text = "4";
            SelectLabel(languagePlace,localizer.T("Neuro.AutoVram"));
            SelectLabel(visionPlace,localizer.T("Neuro.AutoRam"));
            SelectLabel(speechPlace,localizer.T("Neuro.AutoRam"));
            autoProject = NeuroService.OpenAuto();
            Assert.AreEqual("trio",autoProject.Composition);
            Assert.AreEqual(4,autoProject.Cycles);
            Assert.AreEqual("gpu",autoProject.LanguageDevice);
            Assert.AreEqual("cpu",autoProject.VisionDevice);
            Assert.AreEqual("cpu",autoProject.SpeechDevice);
            Assert.IsFalse(autoProject.AutoEvaluateOnly);
            Assert.IsTrue(autoProject.UseReferenceExamples);
            Assert.AreEqual(0,autoProject.Examples.Count);
            Assert.AreEqual(localizer.T("Neuro.AutoBuild"),((Button)auto.FindName("Build")).Content);
            Capture(view,language+"-auto");
            Click((Button)view.FindName("ChangeMode"));
            Assert.IsTrue(modeChoice.IsVisible);
            Click((Button)view.FindName("ProMode"));
            Assert.IsTrue(tabs.IsVisible);
            Assert.IsFalse(auto.IsVisible);
            Assert.AreEqual("pair",NeuroService.Open().Composition);
            Assert.AreEqual(localizer.T("Neuro.BuildTab"), ((TabItem)tabs.Items[0]).Header);
            var composition = (ComboBox)view.FindName("Composition");
            Assert.AreEqual(2,composition.Items.Count,((TextBlock)view.FindName("Notice")).Text);
            Capture(view,language+"-build");
            var connection = (ComboBox)view.FindName("Connection");
            var channel = (ComboBox)view.FindName("Channel");
            var hint = (TextBlock)view.FindName("SettingsHint");
            var train = (Button)view.FindName("Train");
            var compare = (Button)view.FindName("Evaluate");
            SelectLabel(connection,localizer.T("Neuro.Option.late"));
            SelectLabel(channel,localizer.T("Neuro.Option.feedback"));
            Assert.AreEqual(localizer.T("Neuro.PrepareFeedback"),hint.Text);
            Assert.IsFalse(train.IsEnabled);
            Assert.IsFalse(compare.IsEnabled);
            SelectLabel(channel,localizer.T("Neuro.Option.continuous"));
            Assert.IsTrue(train.IsEnabled);
            SelectLabel(connection,localizer.T("Neuro.Option.inside"));
            composition.SelectedIndex = 1;
            Assert.AreEqual("trio", NeuroService.OpenProject(p.Id).Composition, ((TextBlock)view.FindName("Notice")).Text);
            Assert.AreEqual("late", NeuroService.OpenProject(p.Id).Connection);
            composition.SelectedIndex = 0;
            tabs.SelectedIndex = 1;
            var examples = (ComboBox)view.FindName("SavedExamples");
            examples.SelectedIndex = 0;
            ((TextBox)view.FindName("Prompt")).Text = "Show settings";
            Click((Button)view.FindName("Mark"));
            var cells = (Panel)view.FindName("Quadrants");
            Assert.AreEqual(4, cells.Children.Count);
            Assert.AreEqual(localizer.T("Neuro.Slot2"),AutomationProperties.GetName((Button)cells.Children[2]));
            Click((Button)cells.Children[2]);
            Until(dispatcher, () => examples.Items.Count == 2 || !string.IsNullOrWhiteSpace(((TextBlock)view.FindName("Notice")).Text));
            var saved = NeuroService.OpenProject(p.Id);
            Assert.AreEqual(2,saved.Examples.Count,((TextBlock)view.FindName("Notice")).Text);
            var added = saved.Examples.Single(e => e.Text == "Show settings");
            Assert.AreEqual(2,added.Slot);
            Assert.AreEqual("train",added.Split);
            Assert.IsTrue(File.Exists(added.Image));
            tabs.SelectedIndex = 0;
            ((TextBox)view.FindName("TaskName")).Text = "Workshop signs";
            ((ComboBox)view.FindName("Dataset")).SelectedIndex = 1;
            Click((Button)view.FindName("SaveTemplate"));
            var include = (CheckBox)view.FindName("IncludeExamples");
            Assert.AreEqual(false,include.IsChecked);
            ((TextBox)view.FindName("PresetName")).Text = "Local signs";
            include.IsChecked = true;
            Click((Button)view.FindName("PickerApply"));
            Click((Button)view.FindName("OpenTemplate"));
            var picker = (ComboBox)view.FindName("PickerItems");
            picker.SelectedItem = picker.Items.Cast<object>().Single(item => item.ToString() == "Local signs");
            Click((Button)view.FindName("PickerApply"));
            var fork = NeuroService.Open();
            Assert.AreNotEqual(p.Id,fork.Id);
            Assert.AreEqual("Workshop signs",fork.TaskName);
            Assert.IsFalse(fork.UseReferenceExamples);
            Assert.AreEqual(2,fork.Examples.Count);
            saved = NeuroService.OpenProject(p.Id);
            Assert.IsTrue(nav.GoBack());
            Assert.AreEqual(ScenarioNavigationCatalog.Experiments, nav.SelectedDirectionId);
            // A result notification can belong to an older project than the current one.
            var another = NeuroService.New("trio");
            Assert.AreEqual(another.Id,NeuroService.Open().Id);
            nav.OpenScenario(ScenarioNavigationCatalog.NeuroConstructor);
            window.UpdateLayout();
            Assert.AreSame(view, nav.Content);
            Assert.IsTrue(modeChoice.IsVisible);
            Assert.IsTrue(view.ShowResult(autoProject.Id));
            Assert.IsTrue(auto.IsVisible);
            Assert.AreEqual(localizer.T("Neuro.AutoVram"),languagePlace.SelectedItem?.ToString());
            Assert.AreEqual(another.Id,NeuroService.Open().Id);
            var firstBuild = AddAutoResult(autoProject,root.FullName,"first",2);
            var secondBuild = AddAutoResult(autoProject,root.FullName,"second",3);
            var currentRun = Path.Combine(NeuroFiles.Project(autoProject.Id),"current.json");
            NeuroFiles.Write(currentRun,secondBuild);
            Assert.IsTrue(view.ShowResult(autoProject.Id,firstBuild));
            var output = (TextBlock)auto.FindName("Result");
            StringAssert.Contains(output.Text,"first.lopatanet");
            StringAssert.Contains(output.Text,string.Format(localizer.T("Neuro.Accuracy"),1,2,4));
            Assert.IsFalse(((Button)auto.FindName("Build")).IsVisible);
            Assert.AreEqual(secondBuild,NeuroFiles.Read<string>(currentRun));
            Assert.IsTrue(view.ShowResult(autoProject.Id,secondBuild));
            StringAssert.Contains(output.Text,"second.lopatanet");
            StringAssert.Contains(output.Text,string.Format(localizer.T("Neuro.Accuracy"),1,3,4));
            Click((Button)view.FindName("ChangeMode"));
            Click(autoEntry);
            Assert.IsTrue(((Button)auto.FindName("Build")).IsVisible);
            Assert.AreEqual(secondBuild,NeuroFiles.Read<string>(currentRun));
            Assert.IsTrue(view.ShowResult(p.Id));
            Assert.IsTrue(tabs.IsVisible);
            Assert.AreEqual(p.Id,NeuroService.Open().Id);
            Assert.AreEqual(2, tabs.SelectedIndex);
            Assert.IsTrue(((Button)view.FindName("Train")).IsEnabled);
            Assert.IsFalse(((Button)view.FindName("Resume")).IsEnabled);
            Assert.AreEqual(2,examples.Items.Count);
            var run = Guid.NewGuid().ToString("N");
            var dir = NeuroFiles.Run(p.Id,run);
            NeuroFiles.Write(Path.Combine(dir,"settings.json"),saved);
            NeuroFiles.Write(Path.Combine(dir,"report.json"), new {
                total=1,correct=1,baselineCorrect=0,eligible=true,baselineSeconds=.02,fusedSeconds=.01,
                controls=new { disabled=0,shuffled=0 },
                rows=new[]{ new {text="Show settings",image=path,slot=2,baseline=0,predicted=2,route="pair",calls=new {audio=0}} }
            });
            NeuroFiles.Write(Path.Combine(NeuroFiles.Project(p.Id),"finished.json"),run);
            Assert.IsTrue(view.ShowResult(p.Id));
            Assert.AreEqual(localizer.T("Neuro.QualityReached"),((TextBlock)view.FindName("ReportState")).Text);
            StringAssert.Contains(((TextBlock)view.FindName("Reviewed")).Text,localizer.T("Neuro.Slot2"));
            Capture(view,language+"-results");
            // Restore the saved settings of a completed late connection and switch its runtime policy.
            saved.Connection = "late";
            NeuroFiles.Write(Path.Combine(dir,"settings.json"),saved);
            tabs.SelectedIndex = 0;
            SelectLabel(connection,localizer.T("Neuro.Option.late"));
            SelectLabel(channel,localizer.T("Neuro.Option.feedback"));
            Assert.IsTrue(compare.IsEnabled,hint.Text);
            StringAssert.Contains(hint.Text,localizer.T("Neuro.ReadyToEvaluate"));
        }
        finally
        {
            window.Close();
            root.Delete(recursive:true);
        }
    });

    private static string AddAutoResult(NeuroProject project,string folder,string name,int score)
    {
        var id = Guid.NewGuid().ToString("N");
        var run = NeuroFiles.Run(project.Id,id);
        var path = Path.Combine(folder,name+".lopatanet");
        File.WriteAllText(path,name);
        NeuroFiles.Write(Path.Combine(run,"settings.json"),project);
        NeuroFiles.Write(Path.Combine(run,"auto-status.json"),new NeuroStatus("completed","",0,0,0,0,""));
        NeuroFiles.Write(Path.Combine(run,"report.json"),new { runId=id, eligible=true, total=4, baselineCorrect=1, correct=score });
        NeuroFiles.Write(Path.Combine(run,"auto-output.json"),path);
        NeuroFiles.Write(Path.Combine(NeuroFiles.Root,"operations",id,"request.json"),
            new { id,command="pack",project=project.Id,package=path,run=id });
        return id;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void SelectLabel(ComboBox box,string label) =>
        box.SelectedItem = box.Items.Cast<object>().Single(item => item.ToString() == label);
    private static void Capture(FrameworkElement page,string name)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_NEURO_UI_EVIDENCE") is not {Length:>0} folder) return;
        page.UpdateLayout();
        Directory.CreateDirectory(folder);
        var image = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth),(int)Math.Ceiling(page.ActualHeight),
            96,96,PixelFormats.Pbgra32);
        image.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(folder,name+".png"));
        encoder.Save(file);
    }
    private static void Until(Dispatcher dispatcher, Func<bool> completed)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var timer = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_,_) => { if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(completed(), "The WPF save operation did not finish.");
    }
}
