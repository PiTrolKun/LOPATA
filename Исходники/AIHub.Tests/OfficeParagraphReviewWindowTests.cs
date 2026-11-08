using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class OfficeParagraphReviewWindowTests
{
    [TestMethod]
    public Task TranslationToolbarOpensReaderScopesAndRunsInstalledModel()
    {
        if(Environment.GetEnvironmentVariable("LOPATA_REVIEW_LIVE_UI")!="1") Assert.Inconclusive("Opt in to the installed model and UI.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var dir=Directory.CreateTempSubdirectory("lopata-review-live-");
            var source=Path.Combine(dir.FullName,"input.docx");var output=Path.Combine(dir.FullName,"translation.docx");
            var initial=OfficeTextFormats.PlainDocument("The meeting starts at 10.\nSecond paragraph.");File.WriteAllBytes(source,initial);
            var loc=new LocalizationService();loc.Load("ru");
            var window=new ReaderWindowGroup(null,loc.T,false).OpenEditor(source);
            Button? Button(string id)=>Find<Button>(window).FirstOrDefault(b=>AutomationProperties.GetAutomationId(b)==id && b.IsVisible && b.IsEnabled);
            Window? Modal(string id)=>window.OwnedWindows.Cast<Window>().FirstOrDefault(w=>w.IsVisible &&
                Find<Button>(w).Any(b=>AutomationProperties.GetAutomationId(b)==id));
            async Task Press(string id,bool modal=false)
            {
                await Wait(()=>modal?Modal(id)!=null:Button(id)!=null);
                var b=modal?Find<Button>(Modal(id)!).Single(b=>AutomationProperties.GetAutomationId(b)==id):Button(id)!;
                _=window.Dispatcher.BeginInvoke(()=>b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)));
            }
            try
            {
                await Wait(()=>Button("Office.Editor.Translation")!=null);
                var browser=Find<OfficeTextBrowser>(window).First(b=>b.IsVisible);
                await browser.RequestAsync(new {action="command",name="SelectAll"},CancellationToken.None);
                await Press("Office.Editor.Translation");await Press("Office.Editor.Translate.Reader",true);
                await Wait(()=>Modal("Office.Reader.Translation.Start")!=null);
                var prompt=Modal("Office.Reader.Translation.Start")!;
                Assert.IsTrue(Find<RadioButton>(prompt).Any(b=>AutomationProperties.GetAutomationId(b)=="Office.Reader.Translation.Selection" && b.IsChecked==true));
                Assert.IsTrue(Find<RadioButton>(prompt).Any(b=>AutomationProperties.GetAutomationId(b)=="Office.Reader.Translation.Pages"));
                prompt.DialogResult=false;
                await Wait(()=>Find<Button>(window).Any(b=>AutomationProperties.GetAutomationId(b)=="Office.Reader.Editor" && b.IsEnabled));
                await window.SwitchToEditingAsync();
                await Press("Office.Editor.Translation");await Press("Office.Editor.Translate.Paragraph",true);
                await Press("Office.Reader.Translation.Swap",true); await Press("Office.Reader.Translation.Start",true);
                await Wait(()=>Button("Office.Editor.ReviewNext")!=null);
                var right=Find<TextBox>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.ReviewParagraph.0");
                Assert.IsTrue(right.Text.Contains("10") && right.Text.Any(c=>c is >= 'А' and <= 'я'));
                Assert.IsFalse(Find<TextBox>(window).Any(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.ReviewParagraph.1"));
                right.Text="Начало встречи в 10.";
                await Press("Office.Editor.ReviewFinish");
                var picker=OfficeSheetWindowTests.CommonDialog(window,output);
                await Press("Office.Editor.ReviewOutput.Separate",true); await picker;
                await Wait(()=>File.Exists(output) && !Find<OfficeTranslationReview>(window).Any());
                CollectionAssert.AreEqual(new[]{"Начало встречи в 10.","Second paragraph."},new OfficeParagraphTranslation(File.ReadAllBytes(output)).Original.ToArray());
                CollectionAssert.AreEqual(initial,File.ReadAllBytes(source));
            }
            finally
            {
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};
                timer.Tick+=(_,_)=>{foreach(Window dialog in window.OwnedWindows)
                    Find<Button>(dialog).FirstOrDefault(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));};
                timer.Start();
                try { if(!Find<OfficeTranslationReview>(window).Any()) await window.CloseEditorAsync(); }
                finally {timer.Stop();dir.Delete(true);}
            }
        });
    }

    [TestMethod,DataRow("Replace"),DataRow("Separate"),DataRow("Tab")]
    public Task StepRetryAndFinishInEditor(string destination)
    {
        if(Environment.GetEnvironmentVariable("LOPATA_REVIEW_WINDOW")!="1") Assert.Inconclusive("Requires the desktop editor.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var directory=Directory.CreateTempSubdirectory("lopata-translation-ui-");
            var path=Path.Combine(directory.FullName,"source.docx");
            var output=destination=="Replace"?path:Path.Combine(directory.FullName,"translated"+(destination=="Tab"?OfficePackage.Extension:".docx"));
            var baseline=OfficeTextFormats.PlainDocument("One\nTwo\nThree");File.WriteAllBytes(path,baseline);
            var loc=new LocalizationService();loc.Load("ru");
            var window=new ReaderWindowGroup(null,loc.T,false).OpenEditor(path);
            try
            {
                Button? Btn(string id)=>Find<Button>(window).SingleOrDefault(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor."+id);
                void Click(string id)=>Btn(id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TextBox Box(int i)=>Find<TextBox>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.ReviewParagraph."+i);
                await Wait(()=>Find<Button>(window).Any(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.Translation" && b.IsEnabled));
                var editor=Find<OfficeTextEditorView>(window).Single();
                var calls=new List<int>(); var doc=new OfficeParagraphTranslation(baseline);
                editor.OpenReview(doc,editor.CurrentSheetId!,baseline,async (i,ct)=>
                {calls.Add(i);await Task.Delay(20,ct);return $"Перевод {i+1}, попытка {calls.Count}";});
                await Wait(()=>Btn("ReviewNext")?.IsEnabled==true);
                Box(0).Text="Правка первого абзаца";
                Click("ReviewNext");await Wait(()=>Btn("ReviewRetry")?.IsEnabled==true);
                Click("ReviewRetry");await Wait(()=>Btn("ReviewRetry")?.IsEnabled==true);
                Assert.AreEqual("Перевод 2, попытка 3",Box(1).Text);
                Box(1).Text="Правка второго абзаца";
                Assert.AreEqual("Правка первого абзаца",Box(0).Text);
                CollectionAssert.AreEqual(new[]{0,1,1},calls);
                _=window.Dispatcher.BeginInvoke(()=>window.OwnedWindows.Cast<Window>().Single(w=>w.IsVisible).DialogResult=false,
                    DispatcherPriority.ApplicationIdle);
                Click("ReviewFinish");
                await Wait(()=>Btn("ReviewFinish")?.IsEnabled==true);
                Assert.AreEqual("Правка второго абзаца",Box(1).Text);
                var picker=destination=="Replace"?Task.CompletedTask:OfficeSheetWindowTests.CommonDialog(window,output);
                _ = window.Dispatcher.BeginInvoke(()=>
                {
                    var dialog=window.OwnedWindows.Cast<Window>().Single(w=>w.IsVisible);
                    Find<Button>(dialog).Single(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.ReviewOutput."+destination)
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                },DispatcherPriority.ApplicationIdle);
                Click("ReviewFinish");await picker;
                await Wait(()=>!Find<OfficeTranslationReview>(window).Any());
                Assert.IsTrue(window.IsEditing);
                byte[] result;
                if(destination=="Tab")
                {
                    var project=await OfficeTextWork.OpenAsync(output,CancellationToken.None);
                    try
                    {
                        Assert.AreEqual("One",new OfficeParagraphTranslation(project.Document).Original[0]);
                        result=await project.ReadSheetAsync(editor.CurrentSheetId!,CancellationToken.None);
                    }
                    finally {project.Close(true);}
                }
                else result=File.ReadAllBytes(output);
                if (destination!="Separate")
                {
                    var browser=Find<OfficeTextBrowser>(window).First(b=>b.IsVisible);
                    var surface=await browser.RequestAsync(new {action="text",selection=false},CancellationToken.None);
                    StringAssert.Contains(surface.GetString()!,"Правка первого абзаца");
                }
                CollectionAssert.AreEqual(new[]{"Правка первого абзаца","Правка второго абзаца","Three"},
                    new OfficeParagraphTranslation(result).Original.ToArray());
                if(destination!="Replace") CollectionAssert.AreEqual(baseline,File.ReadAllBytes(path));
            }
            finally
            {
                var discard=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                discard.Tick+=(_,_)=>{foreach(Window dialog in window.OwnedWindows)
                    Find<Button>(dialog).FirstOrDefault(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
                discard.Start();
                try { if(!Find<OfficeTranslationReview>(window).Any()) await window.CloseEditorAsync(); }
                finally {discard.Stop();directory.Delete(true);}
            }
        });
    }
    private static async Task Wait(Func<bool> check)
    {
        var until=DateTime.UtcNow.AddSeconds(40);
        while(!check()) { if(DateTime.UtcNow>until) Assert.Fail("Review did not reach the expected state."); await Task.Delay(60); }
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match) yield return match;
            foreach(var sub in Find<T>(child)) yield return sub;
        }
    }
}
