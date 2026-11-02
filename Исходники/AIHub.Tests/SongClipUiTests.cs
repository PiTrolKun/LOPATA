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
public sealed class SongClipUiTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task WorkspaceRetainsThemeColorsAndProvidesExpectedControls(bool dark) => OnUi(()=>
    {
        using var f=new VideoPreparationTests.TestFiles();var l=new LocalizationService();l.Load("ru");
        var control=new SongClipControl(l.T,new SongClipStore(f.File("projects")));
        var panelColor=dark?Color.FromRgb(23,32,50):Color.FromRgb(244,246,249);
        control.Resources["PanelBrush"]=new SolidColorBrush(panelColor);
        control.Resources["TextPrimaryBrush"]=dark?Brushes.White:Brushes.Black;control.Resources["WindowBackgroundBrush"]=new SolidColorBrush(dark?Color.FromRgb(15,23,38):Colors.White);
        control.Resources["SecondaryButtonBackgroundBrush"]=new SolidColorBrush(panelColor);control.Resources["LineBrush"]=Brushes.DimGray;control.Resources["UiBodyFontSize"]=14d;
        var window=new Window{Content=control,Width=1700,Height=950,Background=(Brush)control.Resources["WindowBackgroundBrush"],ShowInTaskbar=false};
        try{
            window.Show();window.UpdateLayout();
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);window.UpdateLayout();
            var nodes=ScenarioNavigationTests.LogicalDescendants(control).ToArray();
            foreach(var id in new[]{"New","Projects","ImportSong","ImportLrc","Components","Generate","Pause","Stop","Export","Editor"})
                Assert.AreEqual(1,nodes.Count(n=>AutomationProperties.GetAutomationId(n)=="SongClip."+id));
            var model=nodes.OfType<Button>().Single(n=>AutomationProperties.GetAutomationId(n)=="SongClip.Model");Assert.Contains("Glitchframe",((TextBlock)model.Content).Text);
            var lyrics=nodes.OfType<TextBox>().Single(n=>AutomationProperties.GetAutomationId(n)=="SongClip.Lyrics");lyrics.Text="Keep these lyrics";Assert.IsTrue(control.Checkpoint());
            Assert.AreEqual("Keep these lyrics",new SongClipStore(f.File("projects")).List().Single().Lyrics);
            foreach(var list in nodes.OfType<ListBox>()){
                Assert.AreEqual(panelColor,((SolidColorBrush)list.Background).Color);list.IsEnabled=false;window.UpdateLayout();
                var rendered=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);rendered.Render(window);
                var location=list.TransformToAncestor(window).Transform(new Point(10,10));
                var pixel=new byte[4];rendered.CopyPixels(new Int32Rect((int)location.X,(int)location.Y,1,1),pixel,4,0);CollectionAssert.AreEqual(new byte[]{panelColor.B,panelColor.G,panelColor.R,255},pixel);
            }
            var evidence=Environment.GetEnvironmentVariable("LOPATA_GLITCHFRAME_TEST_EVIDENCE");
            if(evidence is not null){Directory.CreateDirectory(evidence);var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(evidence,dark?"workspace-dark.png":"workspace-light.png"));png.Save(output);}
        }finally{window.Close();}
    });
    [TestMethod]
    public Task ReferencePreviewReleasesItsFileAndInvalidImagesDoNotBreakProjectOpening() => OnUi(()=>
    {
        using var files=new VideoPreparationTests.TestFiles();var store=new SongClipStore(files.File("projects"));
        var project=new SongClipProject{Reference="attachments/logo.png"};
        var imagePath=store.Resolve(project.Id,project.Reference);Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var bitmap=new RenderTargetBitmap(8,8,96,96,PixelFormats.Pbgra32);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(imagePath))png.Save(file);
        store.Save(project);var control=new SongClipControl(key=>key,store);control.OpenProject(project.Id);
        var images=ScenarioNavigationTests.LogicalDescendants(control).OfType<System.Windows.Controls.Image>().ToArray();
        Assert.AreEqual(1,images.Count(image=>image.Source is not null));
        using(var file=File.Open(imagePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.IsTrue(file.Length>0);
        File.WriteAllText(imagePath,"not an image");
        control.OpenProject(project.Id);
        Assert.IsTrue(images.All(image=>image.Source is null),"Invalid reference bytes must leave a placeholder, without preventing project opening.");
    });
    private static Task OnUi(Action action)
    {
        var task=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{action();task.SetResult();}catch(Exception error){task.SetException(error);}finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return task.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
