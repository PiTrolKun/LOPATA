using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using Image = System.Windows.Controls.Image;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public class OfficeSlidePreviewTests
{
    [TestMethod, TestCategory("Native"), DataRow(".pptx"), DataRow(".odp")]
    public async Task InlinePreviewIncludesHiddenSlideRefreshesAndReleasesItsWorkspace(string extension)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_OFFICE_SLIDE_PREVIEW") != "1")
            Assert.Inconclusive("Set LOPATA_OFFICE_SLIDE_PREVIEW=1 to run live presentation rendering.");
        await OfficeProjectUiThread.RunAsync(() => ExercisePreview(extension));
    }

    private static async Task ExercisePreview(string extension)
    {
        var source = OfficeNativeDocument.Create(extension, [new() { Title="HiddenStart" }, new() { Title="SecondSlide" }]);
        var package = new OfficeNativePackage(source, false);
        if (extension == ".pptx") package.Xml("ppt/slides/slide1.xml").Root!.SetAttributeValue("show", "0");
        else
        {
            XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
            XNamespace pres = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
            XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
            XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
            var content = package.Xml("content.xml");
            content.Descendants(office+"automatic-styles").First().Add(
                new XElement(style+"style", new XAttribute(style+"name","HiddenPage"),
                    new XAttribute(style+"family","drawing-page"),
                    new XElement(style+"drawing-page-properties",new XAttribute(pres+"visibility","hidden"))));
            content.Descendants(draw+"page").First().SetAttributeValue(draw+"style-name","HiddenPage");
        }
        package.EndRead();
        var hidden = package.Snapshot()();
        var model = OfficeNativeDocument.Open(hidden,extension);
        var loc = new LocalizationService(); loc.Load("ru");
        var preview = new OfficeNativeSlidePreview(model,loc.T);
        var window = new Window { Title="LOPATA preview test",Content=preview,Width=780,Height=480,ShowInTaskbar=false };
        var prior = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_,_) => Task.CompletedTask;
        var image = preview.Children.OfType<Image>().Single();
        try
        {
            window.Show();
            Assert.AreEqual(Visibility.Collapsed,preview.Visibility);
            preview.ShowPage(0); preview.SetActive(true);
            await Ready(preview,image,"HiddenStart",0);
            var first = (BitmapSource)image.Source!;
            preview.ShowPage(1); await Ready(preview,image,"SecondSlide",1);
            Assert.AreNotSame(first,image.Source);
            model.Pages[1].Blocks[0].SetText("IntermediateContent"); preview.InvalidateContent();
            model.Pages[1].Blocks[0].SetText("FinalPreviewContent"); preview.InvalidateContent();
            preview.ShowPage(0); preview.ShowPage(1);
            await Ready(preview,image,"FinalPreviewContent",1);
            var dir = Environment.GetEnvironmentVariable("LOPATA_OFFICE_SLIDE_PREVIEW_EVIDENCE");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image.Source!));
                using var target = File.Create(Path.Combine(dir,extension.TrimStart('.')+"-slide-2.png"));
                encoder.Save(target);
            }
            var workspace = Reader(preview)!.Workspace;
            preview.SetActive(false);
            await Wait(() => !Directory.Exists(workspace),preview);
            Assert.IsNull(image.Source);
            var final = new OfficeNativePackage(model.Save(),false);
            try
            {
                if (extension == ".pptx") Assert.AreEqual("0",(string?)final.Xml("ppt/slides/slide1.xml").Root!.Attribute("show"));
                else Assert.IsTrue(final.Xml("content.xml").Descendants().Any(e => e.Attributes().Any(a => a.Name.LocalName=="visibility" && a.Value=="hidden")));
            }
            finally { final.EndRead(); }
        }
        finally
        {
            try { await preview.DisposeAsync(); }
            finally { window.Close(); ComponentLicenseGate.ConfirmAsync=prior; }
        }
    }

    private static ReaderDocument? Reader(OfficeNativeSlidePreview preview) =>
        (ReaderDocument?)typeof(OfficeNativeSlidePreview).GetField("_reader",BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview);

    private static async Task Ready(OfficeNativeSlidePreview preview,Image image,string marker,int page)
    {
        await Wait(() => image.Source != null, preview);
        var reader = Reader(preview)!;
        Assert.AreEqual(2,reader.Pages.Count);
        var result = await reader.FindAsync(marker,page,CancellationToken.None);
        Assert.AreEqual(page,result.Page);
    }

    private static async Task Wait(Func<bool> condition,OfficeNativeSlidePreview preview)
    {
        var time = Stopwatch.StartNew();
        while (!condition())
        {
            if (time.Elapsed > TimeSpan.FromSeconds(90))
                throw new TimeoutException(preview.Children.OfType<TextBlock>().Single().Text);
            await Task.Delay(75);
        }
    }
}
