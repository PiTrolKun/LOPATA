using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using SkiaSharp;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ListBox=System.Windows.Controls.ListBox;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class FileMetadataUiTests
{
    [TestMethod,DataRow("ru",true,760d),DataRow("ru",true,1200d),DataRow("en",false,760d),DataRow("en",false,1200d)]
    public Task MixedFilesAndExplicitBulkEditingHaveIndependentResults(string language,bool dark,double width)=>Sta(async()=>
    {
        var root=MusicOutputTests.Temporary();
        var owner=new Window{Width=1200,Height=850};Theme(owner,dark);owner.Show();
        var l=new LocalizationService();l.Load(language);var prompts=new List<string>();var allow=true;
        MessageBoxResult Confirm(string message,MessageBoxButton buttons,MessageBoxImage icon)
        {prompts.Add(message);return buttons==MessageBoxButton.OKCancel?(allow?MessageBoxResult.OK:MessageBoxResult.Cancel):MessageBoxResult.Yes;}
        var window=new FileMetadataWindow(owner,l.T,Confirm){Width=width,Height=850};
        try
        {
            var one=Path.Combine(root,"01-картинка.png");var two=Path.Combine(root,"02-картинка.png");var other=Path.Combine(root,"03-note.txt");
            File.WriteAllBytes(one,FileMetadataTests.Picture(SKColors.Blue,"PNG"));File.WriteAllBytes(two,FileMetadataTests.Picture(SKColors.Red,"PNG"));File.WriteAllText(other,"Not a supported writable container.");
            PngMetadataWriter.Write(one,new Dictionary<string,string>{{"Title","First"},{"LOPATA","immutable"}});PngMetadataWriter.Write(two,new Dictionary<string,string>{{"Title","Second"}});
            window.Show();await window.AddFilesAsync([one,two,other,one]);window.UpdateLayout();
            var list=Desc<ListBox>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Metadata.Files");Assert.AreEqual(3,list.Items.Count);
            list.SelectedItems.Add(list.Items[1]);window.UpdateLayout();
            var title=Desc<TextBox>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Metadata.Field.title");
            Assert.AreEqual(l.T("Metadata.Different"),title.Text);
            var save=Desc<Button>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Metadata.Save");Assert.IsFalse(save.IsEnabled);
            title.Text="Общее название";Assert.IsTrue(save.IsEnabled);
            var method=typeof(FileMetadataWindow).GetMethod("SaveCurrentAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            allow=false;Assert.IsFalse(await (Task<bool>)method.Invoke(window,null)!);Assert.AreEqual("First",PngTextMetadata.Read(one)["Title"]);
            allow=true;Assert.IsTrue(await (Task<bool>)method.Invoke(window,null)!);Assert.AreEqual("Общее название",PngTextMetadata.Read(one)["Title"]);Assert.AreEqual("Общее название",PngTextMetadata.Read(two)["Title"]);
            Assert.AreEqual("immutable",PngTextMetadata.Read(one)["LOPATA"]);Assert.IsTrue(prompts.Any(p=>p.Contains(l.T("Metadata.SaveWarning"),StringComparison.Ordinal)));
            list.SelectedItems.Add(list.Items[2]);title=Desc<TextBox>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Metadata.Field.title");title.Text="Второй проход";
            Assert.IsFalse(await (Task<bool>)method.Invoke(window,null)!);Assert.AreEqual("Not a supported writable container.",File.ReadAllText(other));
            Assert.AreEqual("Второй проход",PngTextMetadata.Read(one)["Title"]);
            foreach(var apply in Desc<System.Windows.Controls.CheckBox>(window))apply.IsChecked=false;
            foreach(var grid in Desc<DataGrid>(window)){Assert.IsTrue(grid.IsReadOnly);Assert.IsFalse(grid.CanUserDeleteRows);}
            Assert.IsFalse(Desc<Button>(window).Any(b=>AutomationProperties.GetAutomationId(b).Contains("Delete",StringComparison.OrdinalIgnoreCase)));
            foreach(var expander in Desc<Expander>(window).Take(1))expander.IsExpanded=true;
            window.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.Render);
            foreach(var grid in Desc<DataGrid>(window).Where(g=>g.IsVisible))Assert.IsTrue(grid.Columns.All(c=>c.ActualWidth>80));
            if(Environment.GetEnvironmentVariable("LOPATA_METADATA_UI_EVIDENCE") is {Length:>0} evidence)
            {Directory.CreateDirectory(evidence);var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(evidence,$"{language}-{dark}-{width}.png"));encoder.Save(stream);}
        }
        finally{window.Close();owner.Close();Directory.Delete(root,true);}
    });
    [TestMethod]
    public Task TallMetadataRowScrollsInPixelsAndPassesWheelAtItsEdges()=>Sta(async()=>
    {
        var root=MusicOutputTests.Temporary();var owner=new Window();Theme(owner,true);owner.Show();
        var l=new LocalizationService();l.Load("ru");var window=new FileMetadataWindow(owner,l.T){Width=1000,Height=650};
        try
        {
            var path=Path.Combine(root,"long-text.png");File.WriteAllBytes(path,FileMetadataTests.Picture(SKColors.Blue,"PNG"));
            PngMetadataWriter.Write(path,new Dictionary<string,string>{{"lyrics",string.Join("\n",Enumerable.Range(1,80).Select(i=>$"Строка {i}: текст песни"))}});
            window.Show();await window.AddFilesAsync([path]);
            var expander=Desc<Expander>(window).Single(e=>e.Header.ToString()!.StartsWith(l.T("Metadata.Group.Other"),StringComparison.Ordinal));
            expander.IsExpanded=true;await Layout();var grid=(DataGrid)expander.Content;
            var inner=Visual<ScrollViewer>(grid).First();var outer=Visual<ScrollViewer>(window).Single(s=>ReferenceEquals(s.Content,LogicalTreeHelper.GetParent(expander)));
            inner.ScrollToVerticalOffset(40);await Layout();
            Assert.AreEqual(40d,inner.VerticalOffset,.1,"The middle of one tall row must be reachable in pixels.");
            Assert.IsTrue(inner.ScrollableHeight>300);
            outer.ScrollToVerticalOffset(0);await Layout();var outerBefore=outer.VerticalOffset;
            Wheel(inner,-120);await Layout();Assert.IsTrue(inner.VerticalOffset>40&&inner.VerticalOffset<200);
            Assert.AreEqual(outerBefore,outer.VerticalOffset,.1,"The outer view must stay put while the inner view can scroll.");
            Wheel(inner,120);await Layout();Assert.AreEqual(40d,inner.VerticalOffset,.1);
            inner.ScrollToVerticalOffset(inner.ScrollableHeight*.5);await Layout();var middle=inner.VerticalOffset;
            Wheel(inner,-120);await Layout();Assert.IsTrue(inner.VerticalOffset>middle&&inner.VerticalOffset-middle<200);
            inner.ScrollToBottom();outer.ScrollToTop();await Layout();Wheel(inner,-120);await Layout();
            Assert.IsTrue(outer.VerticalOffset>0,"At the table bottom, wheel scrolling must continue in the window.");
            inner.ScrollToTop();outer.ScrollToVerticalOffset(100);await Layout();var before=outer.VerticalOffset;
            Wheel(inner,120);await Layout();Assert.IsTrue(outer.VerticalOffset<before);
        }
        finally{window.Close();owner.Close();Directory.Delete(root,true);}
        async Task Layout(){window.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.Background);window.UpdateLayout();}
    });
    private static void Wheel(UIElement source,int delta)
    {
        var preview=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,delta)
            {RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent};source.RaiseEvent(preview);
        if(!preview.Handled)source.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,delta)
            {RoutedEvent=System.Windows.Input.Mouse.MouseWheelEvent});
    }
    private static IEnumerable<T> Visual<T>(DependencyObject root)where T:DependencyObject
    {if(root is T found)yield return found;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var value in Visual<T>(VisualTreeHelper.GetChild(root,i)))yield return value;}
    [TestMethod]
    public Task CoverSourceRequiresExplicitChoiceAndAcceptsManualOrigin()=>Sta(()=>
    {
        var owner=new Window();Theme(owner,true);owner.Show();var l=new LocalizationService();l.Load("ru");
        var dialog=new FileMetadataCoverWindow(owner,FileMetadataTests.Picture(SKColors.Green,"PNG"),"image/png",l.T);dialog.Show();
        try
        {
            var combo=Desc<System.Windows.Controls.ComboBox>(dialog).Single();var apply=Desc<Button>(dialog).Single(b=>AutomationProperties.GetAutomationId(b)=="Metadata.CoverApply");
            Assert.AreEqual(-1,combo.SelectedIndex);Assert.IsFalse(apply.IsEnabled);Assert.AreEqual(ImageGenerationCatalog.Manifest.Models.Length+1,combo.Items.Count);
            combo.SelectedIndex=0;Assert.IsTrue(apply.IsEnabled);combo.SelectedIndex=combo.Items.Count-1;Assert.IsFalse(apply.IsEnabled);
            Desc<TextBox>(dialog).Single().Text="Нарисовано карандашом";Assert.IsTrue(apply.IsEnabled);
        }
        finally{dialog.Close();owner.Close();}
        return Task.CompletedTask;
    });
    private static IEnumerable<T> Desc<T>(DependencyObject root)where T:DependencyObject
    {if(root is T found)yield return found;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var value in Desc<T>(child))yield return value;}
    private static Task Sta(Func<Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));dispatcher.BeginInvoke(async()=>
            {try{await action();completion.TrySetResult();}catch(Exception error){completion.TrySetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
    private static void Theme(FrameworkElement root,bool dark)
    {
        root.Resources["TextPrimaryBrush"]=dark?Brushes.White:Brushes.Black;root.Resources["TextSecondaryBrush"]=dark?Brushes.LightGray:Brushes.DarkSlateGray;
        root.Resources["PanelBrush"]=new SolidColorBrush(dark?Color.FromRgb(23,32,50):Colors.White);root.Resources["WindowBackgroundBrush"]=new SolidColorBrush(dark?Color.FromRgb(16,24,39):Colors.WhiteSmoke);
        root.Resources["InputBrush"]=root.Resources["PanelBrush"];root.Resources["SecondaryButtonBackgroundBrush"]=root.Resources["PanelBrush"];root.Resources["LineBrush"]=Brushes.Gray;root.Resources["AccentBrush"]=Brushes.RoyalBlue;root.Resources["UiBodyFontSize"]=14d;
    }
}
