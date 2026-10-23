using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.VectorEditor;
using AIHub.Services;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;
using Microsoft.Web.WebView2.Core;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class VectorEditorWindowTests
{
    [TestMethod,DataRow("ru",true),DataRow("en",false)]
    public async Task RealEditorWindowHasBottomSheetsUsablePropertiesAndOneVisiblePrompt(string language,bool dark)
    {
        await VectorEditorBrowserTests.Sta(async()=>{
            using var folder=new VectorEditorProjectTests.Folder();using var audit=ComponentAudit.UseTestStore(folder.Path,false);
            var previous=ComponentLicenseGate.ConfirmAsync;ComponentLicenseGate.ConfirmAsync=(_,_)=>Task.CompletedTask;
            var owner=new Window();var l=new LocalizationService();l.Load(language);
            foreach(var (key,color)in new[]{("WindowBackgroundBrush",dark?"#101923":"#f3f5f8"),("PanelBrush",dark?"#172330":"#ffffff"),("HeaderBackgroundBrush",dark?"#0b1220":"#fff"),
                ("LineBrush",dark?"#304352":"#d7dfe5"),("TextPrimaryBrush",dark?"#f8fafc":"#172330"),("TextSecondaryBrush",dark?"#aab4c4":"#5d6470"),
                ("SecondaryButtonBackgroundBrush",dark?"#111827":"#f8f8f8"),("AccentBrush","#1dbe91")})
                owner.Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            var project=VectorProject.New("SVG studio",l.T("Vector.Editor.Sheet")+" 1");
            project.Sheets[0]=project.Sheets[0] with{Svg=VectorEditorSvg.AsSheet(VectorExamples.Svg(VectorExamples.All.Single(e=>e.Id=="workshop")))};
            project.Sheets.Add(new(Guid.NewGuid().ToString("N"),l.T("Vector.Editor.Sheet")+" 2",VectorEditorSvg.Blank(1200,800)));
            var editor=new VectorEditorWindow(owner,l.T,project,null,"",new(),()=>false,_=>{}){Width=1440,Height=900,ShowInTaskbar=false,ShowActivated=false,Opacity=0};
            try{
                editor.Show();var browser=Find<VectorEditorBrowser>(editor);
                for(var i=0;i<200&&browser.CoreWebView2 is null;i++)await Task.Delay(50);
                var core=browser.CoreWebView2??throw new InvalidOperationException("Editor browser failed to initialize.");
                for(var i=0;i<200;i++){var ready=await core.ExecuteScriptAsync("!!window.lopataEditor?.svg");if(ready=="true")break;await Task.Delay(50);}
                Assert.AreEqual("true",await core.ExecuteScriptAsync("!!window.lopataEditor?.svg"));
                Assert.AreEqual(0,(await browser.CallAsync("get")).GetProperty("selected").GetArrayLength());
                foreach(var key in new[]{"fill","stroke"}){
                    var color=All<System.Windows.Controls.Button>(editor).Single(e=>AutomationProperties.GetAutomationId(e)=="Vector.Editor.Drawing."+key);
                    Assert.IsTrue(color.IsVisible);Assert.IsTrue(color.ActualWidth>80,"The paint button must remain usable without a selection.");
                    var noPaint=All<System.Windows.Controls.CheckBox>(editor).Single(e=>AutomationProperties.GetAutomationId(e)=="Vector.Editor.Drawing.No."+key);
                    Assert.IsTrue(noPaint.IsVisible);Assert.IsFalse(noPaint.IsChecked==true);
                    noPaint.IsChecked=true;noPaint.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Task.Delay(50);Assert.AreEqual("\"none\"",await core.ExecuteScriptAsync("window.lopataEditor.drawPaint()."+key));
                    noPaint.IsChecked=false;noPaint.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Task.Delay(50);Assert.AreNotEqual("\"none\"",await core.ExecuteScriptAsync("window.lopataEditor.drawPaint()."+key));
                }
                var prompt=All<TextBox>(editor).Single(e=>AutomationProperties.GetAutomationId(e)=="Vector.Editor.Prompt");
                Assert.IsFalse(prompt.AcceptsReturn);Assert.AreEqual(TextWrapping.NoWrap,prompt.TextWrapping);Assert.IsTrue(prompt.ActualWidth>350);
                var tabs=Find<TabControl>(editor);Assert.AreEqual(2,tabs.Items.Count);
                var point=tabs.TranslatePoint(new System.Windows.Point(),editor);Assert.IsTrue(point.Y>browser.TranslatePoint(new System.Windows.Point(),editor).Y+browser.ActualHeight);
                var output=Path.Combine(AppContext.BaseDirectory,"EditorUiEvidence");Directory.CreateDirectory(output);
                var bitmap=new RenderTargetBitmap((int)editor.ActualWidth,(int)editor.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render((Visual)editor.Content);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,language+"-window.png")))encoder.Save(file);
                using(var file=File.Create(Path.Combine(output,language+"-canvas.png")))await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                var imported="<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 300'><text x='20' y='50'>new-file-fixture</text></svg>";
                Assert.IsFalse(await VectorEditorFileRouting.TryAddSheetAsync([],null,imported,"no editor"));
                var originals=project.Sheets.Select(s=>s.Svg).ToArray();
                editor.WindowState=WindowState.Minimized;
                Assert.IsTrue(await VectorEditorFileRouting.TryAddSheetAsync([editor],editor,imported,"from Explorer"));
                Assert.AreNotEqual(WindowState.Minimized,editor.WindowState);Assert.AreEqual(3,tabs.Items.Count);
                Assert.AreEqual("from Explorer",project.Sheets[2].Name);StringAssert.Contains(project.Sheets[2].Svg,"new-file-fixture");
                CollectionAssert.AreEqual(originals,project.Sheets.Take(2).Select(s=>s.Svg).ToArray());
                var viewer=new VectorFileViewerWindow(owner,l.T,"fixture.svg",imported,
                    ()=>VectorEditorFileRouting.TryAddSheetAsync([editor],editor,imported,"from viewer")){ShowInTaskbar=false,ShowActivated=false,Opacity=0};
                try{
                    viewer.Show();
                    var promote=All<System.Windows.Controls.Button>(viewer).Single(e=>AutomationProperties.GetAutomationId(e)=="Vector.Viewer.OpenEditor");
                    promote.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    for(var i=0;i<100&&viewer.IsVisible;i++)await Task.Delay(25);
                    Assert.IsFalse(viewer.IsVisible);Assert.AreEqual(4,tabs.Items.Count);
                    Assert.AreEqual("from viewer",project.Sheets[3].Name);
                }finally{viewer.Close();}
                tabs.SelectedIndex=1;await Task.Delay(200);Assert.IsTrue((await browser.CallAsync("get")).GetProperty("svg").GetString()!.Contains("1200 800"));
                await browser.CallAsync("command",new{name="text",value="checkpoint-fixture",x=100,y=100});
                for(var i=0;i<50&&!project.Sheets[1].Svg.Contains("checkpoint-fixture",StringComparison.Ordinal);i++)await Task.Delay(20);
                editor.CheckpointForExit();
                var recovery=VectorProjectArchive.Load(VectorProjectRecovery.PathFor(project.Id));
                StringAssert.Contains(recovery.Sheets[1].Svg,"checkpoint-fixture");
                Console.WriteLine("Editor window evidence: "+output);
            }finally{editor.CloseForApplicationExit();VectorProjectRecovery.Remove(project.Id);owner.Close();ComponentLicenseGate.ConfirmAsync=previous;}
        });
    }
    private static T Find<T>(DependencyObject root)where T:DependencyObject=>All<T>(root).First();
    private static IEnumerable<T> All<T>(DependencyObject root)where T:DependencyObject
    {
        if(root is T found)yield return found;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in All<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
