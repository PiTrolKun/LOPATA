using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class GeekatplayUiTests
{
    [TestMethod]
    [DataRow("ru",true)] [DataRow("en",false)]
    public Task ModelSwitchKeepsIndependentSettingsAndThemedReadableControls(string language,bool dark) => OnUi(() => {
        using var files=new VideoPreparationTests.TestFiles();
        var previous=ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current=null;
        var localizer=new LocalizationService();localizer.Load(language);
        var store=new SongClipStore(files.File("projects"));
        var project=new SongClipProject {Model="h3_music_context",Name="Test",Song="attachments/song.wav",
            Lyrics="A cat watches the moon.",Settings=new(){Motion=new(){Width=864,Height=480,Steps=30}}};
        store.Save(project);
        var control=new SongClipControl(localizer.T,store);control.OpenProject(project.Id);
        var host=new Window {Content=control,Width=1700,Height=960,ShowActivated=false,ShowInTaskbar=false};
        foreach(var key in new[]{"UiBodyFontSize","UiButtonFontSize","UiSmallFontSize","UiTinyFontSize"})host.Resources[key]=14d;
        host.Resources["PanelBrush"]=dark?Brushes.DarkSlateGray:Brushes.WhiteSmoke;
        host.Resources["WindowBackgroundBrush"]=dark?Brushes.Black:Brushes.White;
        host.Resources["TextPrimaryBrush"]=dark?Brushes.White:Brushes.Black;
        host.Resources["TextSecondaryBrush"]=dark?Brushes.LightGray:Brushes.DimGray;
        foreach(var key in new[]{"SecondaryButtonBackgroundBrush","InputBrush"})host.Resources[key]=host.Resources["PanelBrush"];
        host.Resources["LineBrush"]=Brushes.Gray;host.Resources["AccentBrush"]=Brushes.RoyalBlue;
        try {
            host.Show();Flush(host);
            var models=Find<Button>(control,"Model");
            Assert.IsTrue(control.SelectVariant("geekatplay"));Flush(host);
            var ids=new[]{"GeekSize","GeekSteps","GeekCardSteps","GeekSeconds","GeekStrength"};
            foreach(var id in ids){var field=Find<ComboBox>(control,id);Assert.IsTrue(field.IsVisible,id);Assert.IsTrue(field.ActualWidth>=100,id);Assert.DoesNotContain("SongClip.",AutomationProperties.GetName(field));}
            Find<ComboBox>(control,"GeekSize").SelectedIndex=2;
            Find<ComboBox>(control,"GeekSteps").SelectedItem=12;
            Find<ComboBox>(control,"GeekCardSteps").SelectedItem=16;
            Find<ComboBox>(control,"GeekSeconds").SelectedItem=8;
            Assert.IsTrue(control.Checkpoint());
            var saved=store.Load(project.Id);
            Assert.AreEqual(new SongClipGeekSettings {Width=480,Height=864,Steps=12,CardSteps=16,ClipSeconds=8},saved.Settings.Geek);
            Assert.AreEqual(project.Settings.Motion,saved.Settings.Motion);
            Assert.IsTrue(control.SelectVariant("motion"));Flush(host);
            Assert.IsFalse(Find<ComboBox>(control,"GeekSize").IsVisible);
            Assert.AreEqual(30,Find<ComboBox>(control,"MotionSteps").SelectedItem);
            Assert.IsTrue(control.SelectVariant("geekatplay"));Flush(host);
            Assert.AreEqual(12,Find<ComboBox>(control,"GeekSteps").SelectedItem);
            Assert.AreEqual(8,Find<ComboBox>(control,"GeekSeconds").SelectedItem);
            typeof(SongClipControl).GetMethod("SetInputs",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[false]);
            foreach(var id in ids)Assert.IsFalse(Find<ComboBox>(control,id).IsEnabled,id);
            var format=typeof(SongClipControl).GetMethod("FormatEngineProgress",BindingFlags.NonPublic|BindingFlags.Instance)!;
            foreach(var (stage,key) in new[]{("geek-analysis","GeekAnalysis"),("geek-cards","GeekCards"),("geek-assembly","GeekAssembly")})
                Assert.AreEqual(localizer.T("SongClip."+key),format.Invoke(control,[JsonSerializer.Serialize(new{kind="stage",stage})]));
        }
        finally {host.Close();Flush(host);ApplicationBackgroundOperations.Current=previous;}
    });
    private static T Find<T>(DependencyObject root,string id) where T:DependencyObject => ScenarioNavigationTests.LogicalDescendants(root).OfType<T>().Single(item=>AutomationProperties.GetAutomationId(item)=="SongClip."+id);
    private static void Flush(Window host){host.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);host.UpdateLayout();}
    private static Task OnUi(Action action){var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>{try{action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(35));}
}
