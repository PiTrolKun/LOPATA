using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.ImageLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLoraUiTests
{
    [TestMethod, TestCategory("OptIn")]
    public Task LocalizedWindowsRenderAtNarrowWidthInBothPalettes() => ScenarioNavigationTests.Sta(()=>{
        var output=Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_UI_EVIDENCE");
        if(string.IsNullOrWhiteSpace(output))Assert.Inconclusive("Visible WPF check is opt-in.");
        Directory.CreateDirectory(output!);
        foreach(var language in new[]{"ru","en"})foreach(var dark in new[]{true,false}) {
            var l=new LocalizationService();l.Load(language);var window=new ImageLoraWindow(null,Path.GetTempPath(),"z-image",new(),l.T,language=="ru") { Width=760,Height=800 };
            try {
                foreach(var pair in new Dictionary<string,string> {
                    ["WindowBackgroundBrush"]=dark?"#111827":"#F3F3F3",["PanelBrush"]=dark?"#172033":"#FFFFFF",
                    ["TextPrimaryBrush"]=dark?"#F8FAFC":"#1F1F1F",["TextSecondaryBrush"]=dark?"#AAB4C4":"#5D6470",
                    ["LineBrush"]=dark?"#2D374B":"#DADDE3",["AccentBrush"]="#2563EB",["SecondaryButtonBackgroundBrush"]=dark?"#111827":"#F8F8F8" })
                    window.Resources[pair.Key]=(SolidColorBrush)new BrushConverter().ConvertFromString(pair.Value)!;
                window.Resources["UiBodyFontSize"]=14d;window.Show();window.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                var root=(DockPanel)window.Content;Assert.IsGreaterThan(700d,root.ActualHeight);
                Assert.IsTrue(root.Children.OfType<ScrollViewer>().Single().ActualHeight>200);
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output!,language+(dark?"-dark":"-light")+".png"));png.Save(stream);
                if(language=="ru" && dark) {
                    var definitions=ImageLoraCatalog.For("z-image").Where(d=>d.Supported).ToArray();
                    var allCount=0;var partialCount=0;Exception? failure=null;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
                        var picker=window.OwnedWindows.Cast<Window>().Single();
                        try {
                            IEnumerable<DependencyObject> Walk(DependencyObject root) {
                                yield return root;
                                foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var node in Walk(child))yield return node;
                            }
                            var boxes=Walk(picker).OfType<System.Windows.Controls.CheckBox>().ToArray();
                            boxes[0].IsChecked=true;allCount=boxes.Skip(1).Count(b=>b.IsChecked==true);
                            boxes[^1].IsChecked=false;partialCount=boxes.Skip(1).Count(b=>b.IsChecked==true);
                        } catch(Exception e) { failure=e; }
                        finally { picker.Close(); }
                    }));
                    typeof(ImageLoraWindow).GetMethod("ChooseSet",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,[definitions]);
                    if(failure is not null) throw failure;
                    Assert.AreEqual(definitions.Length,allCount);Assert.AreEqual(definitions.Length-1,partialCount);
                }
            } finally { window.Close(); }
        }
    });
}
