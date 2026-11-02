using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class SongClipPickerUiTests
{
    [TestMethod]
    [DataRow("ru",true,1050d)] [DataRow("en",false,1050d)]
    [DataRow("ru",false,740d)] [DataRow("en",true,740d)]
    public Task GroupsSelectionAndDownloadCloseBeforeReturningToOwner(string language,bool dark,double width)=>OnUi(()=>{
        using var files=new VideoPreparationTests.TestFiles();var l=new LocalizationService();l.Load(language);
        var owner=new Window {Width=1300,Height=800,ShowInTaskbar=false,ShowActivated=false};Theme(owner,dark);owner.Show();
        var picker=new SongClipModelWindow(owner,l.T,files.File("models"),"h3_music_context","montage") {Width=width,Height=620,ShowInTaskbar=false,ShowActivated=false};
        try {
            picker.Show();Flush(picker);WaitInventory(picker);
            var nodes=ScenarioNavigationTests.LogicalDescendants(picker).ToArray();
            var radios=nodes.OfType<RadioButton>().ToArray();Assert.AreEqual(4,radios.Length);Assert.IsFalse(radios.Any(r=>AutomationProperties.GetAutomationId(r) is "SongClip.Model.Variant.infinitetalk" or "SongClip.Model.Variant.longcat_avatar_15"));Assert.AreEqual(0,radios.Count(r=>!r.IsEnabled));
            Assert.IsTrue(radios.Single(r=>AutomationProperties.GetAutomationId(r).EndsWith("montage",StringComparison.Ordinal)).IsChecked);
            Assert.AreEqual(dark?Colors.Black:Colors.White,((SolidColorBrush)picker.Background).Color);
            foreach(var radio in radios){
                Assert.AreEqual(dark?Colors.White:Colors.Black,((SolidColorBrush)radio.Foreground).Color);
                Assert.AreEqual(dark?Colors.White:Colors.Black,((SolidColorBrush)((TextBlock)radio.Content).Foreground).Color);
            }
            owner.Resources["TextPrimaryBrush"]=Brushes.Lime;Flush(picker);
            foreach(var radio in radios)Assert.AreEqual(Colors.Lime,((SolidColorBrush)((TextBlock)radio.Content).Foreground).Color);
            owner.Resources["TextPrimaryBrush"]=dark?Brushes.White:Brushes.Black;Flush(picker);
            foreach(var text in nodes.OfType<TextBlock>().Where(t=>t.IsVisible))Assert.DoesNotContain("SongClip.",text.Text);
            var scroll=nodes.OfType<ScrollViewer>().First();scroll.ScrollToVerticalOffset(170);Flush(picker);
            Evidence(picker,language+"-"+(dark?"dark":"light")+"-"+width);
            foreach(var button in nodes.OfType<Button>().Where(b=>b.IsVisible)) {
                Assert.IsTrue(button.ActualWidth>10);Assert.IsTrue(button.ActualHeight>10);
                Assert.IsTrue(button.ActualWidth+button.Margin.Left+button.Margin.Right+1>=button.DesiredSize.Width,"Action must not be clipped: "+button.Content);
            }
            Evidence(picker,language+"-"+(dark?"dark":"light")+"-"+width);
            picker.Close();
            // Real modal click returns a request, never creates or stacks a download window inside the picker.
            picker=new SongClipModelWindow(owner,l.T,files.File("models"),"glitchframe",null){ShowInTaskbar=false,ShowActivated=false};
            picker.Dispatcher.BeginInvoke(async ()=>{
                await picker.InventoryReady;
                var download=ScenarioNavigationTests.LogicalDescendants(picker).OfType<Button>()
                    .Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.Download.montage");
                download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            },DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(picker.ShowDialog());Assert.IsFalse(picker.IsVisible);Assert.IsTrue(owner.IsVisible);
            Assert.AreEqual(new SongClipPickerRequest(SongClipPickerAction.Download,"montage"),picker.Request);
            Assert.IsFalse(owner.OwnedWindows.Cast<Window>().Any(w=>w is SongClipDownloadWindow));
        }finally{picker.Close();owner.Close();}
    });
    [TestMethod]
    public Task ExternalUseKeepsIndependentModesAndMainButtonStaysLockedDuringWork()=>OnUi(()=>{
        using var files=new VideoPreparationTests.TestFiles();var store=new SongClipStore(files.File("projects"));
        var control=new SongClipControl(key=>key,store);
        Assert.IsTrue(control.SelectVariant("montage"));
        Assert.IsTrue(control.SelectVariant("geekatplay"));
        Assert.IsTrue(control.SelectVariant("motion"));Assert.IsFalse(control.SelectVariant("longcat_avatar_15"));
        var button=ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model");
        Assert.Contains("Motion Context",((TextBlock)button.Content).Text);
        typeof(SongClipControl).GetField("_busy",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(control,true);
        Assert.IsFalse(control.SelectVariant("glitchframe"));
    });
    [TestMethod]
    public Task MainSelectionButtonUsesTheExternalWindowAndCommitsOnlyOnUse()=>OnUi(()=>{
        using var files=new VideoPreparationTests.TestFiles();var root=files.File("models");
        foreach(var file in SongClipVariantCatalog.Files(SongClipVariantCatalog.Cards("motion",root))) {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);File.WriteAllBytes(file.Path,[1]);
        }
        var previous=ApplicationBackgroundOperations.Current;ApplicationBackgroundOperations.Current=null;
        var l=new LocalizationService();l.Load("ru");var store=new SongClipStore(files.File("projects"));var control=new SongClipControl(l.T,store);control.Configure(l.T,root,4);
        var owner=new Window {Content=control,Width=1700,Height=950,ShowInTaskbar=false,ShowActivated=false};Theme(owner,true);
        try {
            owner.Show();Flush(owner);
            var choose=ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model");
            bool opened=false,unchanged=false,closed=false;
            owner.Dispatcher.BeginInvoke(async ()=>{
                var picker=owner.OwnedWindows.OfType<SongClipModelWindow>().Single();opened=picker.IsVisible;
                await picker.InventoryReady;
                var nodes=ScenarioNavigationTests.LogicalDescendants(picker).ToArray();
                nodes.OfType<RadioButton>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.Variant.motion").IsChecked=true;
                unchanged=((TextBlock)choose.Content).Text.Contains("Glitchframe",StringComparison.Ordinal);
                picker.Closed+=(_,_)=>closed=true;
                nodes.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.Use").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            },DispatcherPriority.ApplicationIdle);
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(opened);Assert.IsTrue(unchanged);Assert.IsTrue(closed);
            Assert.Contains("Motion Context",((TextBlock)choose.Content).Text);
            Assert.IsFalse(owner.OwnedWindows.Cast<Window>().Any(w=>w.IsVisible));
        }finally{owner.Close();ApplicationBackgroundOperations.Current=previous;}
    });
    [TestMethod]
    public Task SlowInventoryKeepsUiResponsiveAndClosingCancelsTheScan()=>OnUi(()=>{
        using var files=new VideoPreparationTests.TestFiles();var l=new LocalizationService();l.Load("ru");
        var gate=new TaskCompletionSource<SongClipPickerInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner=new Window {ShowInTaskbar=false,ShowActivated=false};Theme(owner,true);owner.Show();
        CancellationToken observed=default;
        var picker=new SongClipModelWindow(owner,l.T,files.File("models"),"glitchframe",null,async token=>{
            observed=token;return await gate.Task.WaitAsync(token);
        }){ShowInTaskbar=false,ShowActivated=false};
        try {
            picker.Show();Flush(picker);Assert.IsFalse(picker.InventoryReady.IsCompleted);
            var nodes=ScenarioNavigationTests.LogicalDescendants(picker).ToArray();
            Assert.IsTrue(nodes.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.Cancel").IsEnabled);
            Assert.IsFalse(nodes.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.Use").IsEnabled);
            var heartbeat=false;picker.Dispatcher.BeginInvoke(()=>heartbeat=true,DispatcherPriority.Input);Flush(picker);
            Assert.IsTrue(heartbeat,"Inventory must not hold the dispatcher.");
            picker.Close();WaitInventory(picker);Assert.IsTrue(observed.IsCancellationRequested);
            Assert.IsNull(picker.Request);Assert.IsFalse(picker.IsVisible);
        }finally{picker.Close();owner.Close();}
    });
    [TestMethod]
    public Task BaseH3DeleteOffersAnExplicitDependencyWarningAndCancelKeepsEveryFile()=>OnUi(()=>{
        using var files=new VideoPreparationTests.TestFiles();var root=files.File("models");
        var originals=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.BaseCards("MiniMax H3",root));
        foreach(var file in originals){Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);File.WriteAllBytes(file.Path,[1]);}
        var previous=ApplicationBackgroundOperations.Current;ApplicationBackgroundOperations.Current=null;
        var l=new LocalizationService();l.Load("ru");var owner=new Window {ShowInTaskbar=false,ShowActivated=false};Theme(owner,true);owner.Show();
        var picker=new SongClipModelWindow(owner,l.T,root,"h3_music_context",null){ShowInTaskbar=false,ShowActivated=false};
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};var frame=new DispatcherFrame();
        string warning="";bool confirmed=false;var limit=DateTime.UtcNow.AddSeconds(15);
        timer.Tick+=(_,_)=>{
            var review=picker.OwnedWindows.Cast<Window>().FirstOrDefault(w=>AutomationProperties.GetAutomationId(w)=="SongClip.Model.DeleteConfirm");
            if(review is not null){
                var nodes=ScenarioNavigationTests.LogicalDescendants(review).ToArray();warning=string.Join("\n",nodes.OfType<TextBlock>().Select(t=>t.Text));confirmed=true;
                nodes.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.DeleteCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                frame.Continue=false;
            }else if(DateTime.UtcNow>limit)frame.Continue=false;
        };
        try {
            picker.Show();Flush(picker);WaitInventory(picker);
            var remove=ScenarioNavigationTests.LogicalDescendants(picker).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="SongClip.Model.DeleteBase");
            Assert.IsTrue(remove.IsEnabled);timer.Start();
            picker.Dispatcher.BeginInvoke(()=>remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),DispatcherPriority.Input);
            Dispatcher.PushFrame(frame);
            Assert.IsTrue(confirmed,string.Join(" | ",ScenarioNavigationTests.LogicalDescendants(picker).OfType<TextBlock>().Select(t=>t.Text)));Assert.Contains(l.T("SongClip.PickerDeleteH3Warning"),warning);Assert.Contains("Motion Context",warning);Assert.Contains("Geekatplay",warning);
            Assert.IsTrue(originals.All(f=>File.Exists(f.Path)),"Cancel must preserve the complete H3 set.");
        }finally{timer.Stop();picker.Close();owner.Close();ApplicationBackgroundOperations.Current=previous;}
    });
    private static void Theme(Window host,bool dark){
        foreach(var key in new[]{"UiBodyFontSize","UiButtonFontSize","UiSmallFontSize","UiTinyFontSize"})host.Resources[key]=14d;
        host.Resources["WindowBackgroundBrush"]=dark?Brushes.Black:Brushes.White;host.Resources["PanelBrush"]=dark?Brushes.DarkSlateGray:Brushes.WhiteSmoke;
        host.Resources["TextPrimaryBrush"]=dark?Brushes.White:Brushes.Black;host.Resources["TextSecondaryBrush"]=dark?Brushes.LightGray:Brushes.DimGray;
        host.Resources["SecondaryButtonBackgroundBrush"]=host.Resources["PanelBrush"];host.Resources["InputBrush"]=host.Resources["PanelBrush"];host.Resources["LineBrush"]=Brushes.Gray;host.Resources["AccentBrush"]=Brushes.RoyalBlue;
    }
    private static void Evidence(Window window,string name){var root=Environment.GetEnvironmentVariable("LOPATA_PICKER_EVIDENCE");if(root is null)return;Directory.CreateDirectory(root);var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(root,name+".png"));png.Save(output);}
    private static void WaitInventory(SongClipModelWindow picker)
    {
        if(!picker.InventoryReady.IsCompleted){var frame=new DispatcherFrame();picker.InventoryReady.ContinueWith(_=>picker.Dispatcher.BeginInvoke(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
        picker.InventoryReady.GetAwaiter().GetResult();
    }
    private static void Flush(Window host){host.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);host.UpdateLayout();}
    private static Task OnUi(Action action){var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>{try{action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(45));}
}
