using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

/// <summary>Real WPF controls and WebView2, without global keyboard input.
/// This probe drives the partial save/close and capture paths missed by service-only tests.</summary>
[TestClass, DoNotParallelize]
public sealed class OfficeTextHostNativeTests
{
    [TestMethod, TestCategory("Native")]
    public Task PartialSaveKeepsWorkInViewAndCloseAsksAgain()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_OFFICE_TEXT_HOST_PROBE") != "1")
            Assert.Inconclusive("Set LOPATA_OFFICE_TEXT_HOST_PROBE=1 for real WebView2.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await Probe(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromMinutes(4));
    }

    private static async Task Probe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LOPATA-OfficeHostProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder,"report.txt");
        await File.WriteAllTextAsync(original,"Бытовой отчёт. Привте, мир!");
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_,_) => Task.CompletedTask;
        var owner = new Window { ShowInTaskbar = false, ShowActivated = false, Width = 1000, Height = 770 };
        var loc = new LocalizationService(); loc.Load("ru");
        var view = new OfficeTextEditorView(owner,loc.T,true);
        owner.Content = view; owner.Show();
        try
        {
            await view.InitializeAsync(original,CancellationToken.None);
            var browser = Field<OfficeTextBrowser>(view,"_browser");
            var work = Field<OfficeTextWork>(view,"_work");
            async Task<JsonElement> Request(object command) => await browser.RequestAsync(command,CancellationToken.None);
            var project = work.TemporaryProjectPath;
            Assert.IsTrue(File.Exists(project));
            // Drive the real editor's painted range and correction menu; suggestions come from Windows.
            var deadline=DateTime.UtcNow.AddSeconds(12);
            string painted;
            do
            {
                painted=await browser.ExecuteScriptAsync("document.querySelectorAll('.lopata-misspelled').length");
                if(painted!="0")break;
                await Task.Delay(100);
            } while(DateTime.UtcNow<deadline);
            Assert.AreNotEqual("0",painted,"A misspelled Russian word should be underlined on the document page.");
            var correction=await browser.ExecuteScriptAsync("""
                (()=> {
                  for (const mark of document.querySelectorAll('.lopata-misspelled')) {
                    const rect=mark.getBoundingClientRect();
                    const clientX=rect.x+rect.width/2,clientY=rect.y+rect.height/2;
                    mark.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX,clientY}));
                    const option=Array.from(document.querySelectorAll('.lopata-spelling-menu button'))
                      .find(x=>x.textContent.toLowerCase()==='привет');
                    if(option){option.click();return true;}
                  }
                  return false;
                })()
                """);
            Assert.AreEqual("true",correction);
            StringAssert.Contains((await Request(new {action="text",selection=false})).GetString()!.ToLowerInvariant(),"привет");
            Click(view,"Office.Editor.Undo");
            StringAssert.Contains((await Request(new {action="text",selection=false})).GetString()!,"Привте");
            // These are the same WPF buttons a user clicks. Commands act on a WebView2 document.
            await Request(new {action="command",name=".uno:SelectAll"});
            Click(view,"Office.Editor.Bold");
            await Until(() => work.NeedsProject,10);
            StringAssert.Contains(work.FormatReason!,loc.T("OfficeEditor.Loss.emphasis"));
            Assert.IsTrue(((FrameworkElement)Find(view,"Office.Editor.ProjectNotice")!).IsVisible);
            Click(view,"Office.Editor.Undo");
            await Until(() => !work.NeedsProject,10);
            Click(view,"Office.Editor.Capture");
            Assert.AreEqual(WindowState.Minimized,owner.WindowState);
            await view.ReceiveSelectionAsync(new CapturedSelection("Сбор: цвет",null));
            await view.ReceiveSelectionAsync(new CapturedSelection("Сбор: цвет",null));
            var collected=(await Request(new {action="text",selection=false})).GetString();
            StringAssert.Contains(collected!,"Сбор: цвет {x2}");
            Assert.AreEqual(project,work.TemporaryProjectPath);
            var external=Environment.GetEnvironmentVariable("LOPATA_OFFICE_HOST_EXTERNAL")=="1";
            if(external)
            {
                await CheckExternalSelection(view,owner,browser,folder);
                collected=(await Request(new {action="text",selection=false})).GetString();
            }
            owner.WindowState=WindowState.Normal;
            Click(view,"Office.Editor.Capture");
            var attachment=Path.Combine(folder,"sheet.csv");
            await File.WriteAllTextAsync(attachment,"One,Two");
            await work.AddMaterialAsync(attachment,CancellationToken.None);
            await view.RecordAiActionAsync("Проверка модели",new OutputModelUse("fixture","Test narrator","1","speech"));
            Assert.IsTrue(work.NeedsProject);
            Assert.IsTrue(view.ProjectPromptNeeded);
            var keepWork=Choose(owner,"Office.Editor.Reader.ProjectCancel");
            await view.OfferProjectSaveAsync(); await keepWork;
            var approve=Choose(owner,"Office.Editor.Partial.Save");
            Click(view,"Office.Editor.Save");
            await approve;
            await Until(() => File.ReadAllText(original).Contains("Сбор: цвет {x2}",StringComparison.Ordinal),45);
            await Until(() => !Field<bool>(view,"_saving"),10);
            Assert.IsTrue(work.IsDirty,"Partial save must not resolve the unsaved project.");
            Assert.AreEqual(1,work.Materials.Count);
            // A canceled close retains the entire session including markup, material and undo.
            var cancel=Choose(owner,"Office.Editor.Close.Cancel");
            Assert.IsFalse(await view.ConfirmCloseAsync()); await cancel;
            Assert.AreEqual(collected,(await Request(new {action="text",selection=false})).GetString());
            var discard=Choose(owner,"Office.Editor.Close.Discard");
            Assert.IsTrue(await view.ConfirmCloseAsync()); await discard;
            await view.DisposeAsync();
            Assert.IsFalse(File.Exists(project));
            Assert.IsTrue(File.Exists(original));
            await CheckBlankWindows(loc);
            await CheckRichRecovery(loc,folder);
            var proof=OfficeTextSpelling.Check("Привет привте world helo!","ru-RU",CancellationToken.None);
            Assert.IsTrue(proof.Issues.Any(i=>i.offset==7 && i.suggestions.Contains("привет")));
            Assert.IsTrue(proof.Issues.Any(i=>i.offset==20 && i.suggestions.Contains("hello")));
            var evidence = Environment.GetEnvironmentVariable("LOPATA_OFFICE_HOST_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence,"host-result.json"),JsonSerializer.Serialize(new {
                    capture=collected, sameDraft=true, previouslySavedFileExists=true, selectedBoldRaisedNotice=true,
                    undoClearedFormatReason=true, partialSave=true, closeCancel=true, closeDiscard=true,
                    deletedTemporaryProject=true, spelling=proof.Issues, missingDictionaries=proof.Missing,
                    paintedSpelling=true, correctedWithNativeSuggestion=true, correctionUndo=true, externalUiaSelection=external,
                    twoBlankWindowsGetDistinctProjects=true, untouchedBlankCloseWithoutQuestion=true,
                    recoveredTxtRetainsFormatWarning=true, recoveredTxtCanClearFormatting=true,
                    namedFormattingLossShown=true, canContinueAfterReaderProjectOffer=true,
                    output=await File.ReadAllTextAsync(original)
                },new JsonSerializerOptions{WriteIndented=true}));
            }
        }
        finally
        {
            await view.DisposeAsync();
            owner.Close();
            ComponentLicenseGate.ConfirmAsync=previous;
        }
    }

    private static async Task CheckBlankWindows(LocalizationService loc)
    {
        var group=new ReaderWindowGroup(null,loc.T,false);
        async Task<ReaderWindow> OpenTextProject()
        {
            var window = group.OpenEditor();
            await Until(() => window.OwnedWindows.Cast<Window>()
                .Any(dialog => Find(dialog, "Office.Editor.SheetType.Text") != null), 15);
            var picker = window.OwnedWindows.Cast<Window>().Single(dialog => Find(dialog, "Office.Editor.SheetType.Text") != null);
            Click(picker, "Office.Editor.SheetType.Text");
            return window;
        }
        var first = await OpenTextProject();
        var second = await OpenTextProject();
        async Task<OfficeTextWork> Ready(Window window)
        {
            await Until(()=>Find(window,"Office.Editor.View") is OfficeTextEditorView v && Field<bool>(v,"_ready"),30);
            return Field<OfficeTextWork>((OfficeTextEditorView)Find(window,"Office.Editor.View")!,"_work");
        }
        var a=await Ready(first);var b=await Ready(second);
        Assert.AreNotEqual(a.TemporaryProjectPath,b.TemporaryProjectPath);
        Assert.IsTrue(File.Exists(a.TemporaryProjectPath));
        Assert.IsTrue(File.Exists(b.TemporaryProjectPath));
        first.Close();
        await Until(()=>!first.IsVisible && !File.Exists(a.TemporaryProjectPath),10);
        Assert.IsTrue(second.IsVisible && File.Exists(b.TemporaryProjectPath));
        second.Close();
        await Until(()=>!second.IsVisible && !File.Exists(b.TemporaryProjectPath),10);
    }

    private static async Task CheckRichRecovery(LocalizationService loc,string folder)
    {
        var source=Path.Combine(folder,"recover.txt"); await File.WriteAllTextAsync(source,"Исходный текст");
        var owner=new Window { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=620 };
        var view=new OfficeTextEditorView(owner,loc.T,false); owner.Content=view; owner.Show();
        try
        {
            await view.InitializeAsync(source,CancellationToken.None);
            var browser=Field<OfficeTextBrowser>(view,"_browser");
            var work=Field<OfficeTextWork>(view,"_work");
            await browser.RequestAsync(new {action="command",name="SelectAll"},CancellationToken.None);
            Click(view,"Office.Editor.Bold");
            await Until(()=>work.FormatReason!=null && Field<long>(view,"_revision")==Field<long>(view,"_checkpoint"),10);
            var draft=work.DraftId;
            await view.DisposeAsync();
            Assert.IsTrue(File.Exists(work.TemporaryProjectPath),"Closing without a user decision must retain recovery.");
            view=new OfficeTextEditorView(owner,loc.T,false);owner.Content=view;
            await view.InitializeAsync(source,CancellationToken.None,draft);
            browser=Field<OfficeTextBrowser>(view,"_browser");
            work=Field<OfficeTextWork>(view,"_work");
            Assert.IsFalse(work.CompatibilityBaselineUnavailable);
            Assert.IsTrue(work.NeedsProject);
            // The user explicitly refuses partial Save. No byte of the original may have changed.
            var cancel=Choose(owner,"Office.Editor.Partial.Cancel");Click(view,"Office.Editor.Save");await cancel;
            await Until(()=>!Field<bool>(view,"_saving"),10);
            Assert.AreEqual("Исходный текст",await File.ReadAllTextAsync(source));
            Assert.IsTrue(work.NeedsProject);
            await browser.RequestAsync(new {action="command",name="SelectAll"},CancellationToken.None);
            Click(view,"Office.Editor.Bold");
            await Until(()=>!work.NeedsProject,10);
            var discard=Choose(owner,"Office.Editor.Close.Discard");
            Assert.IsTrue(await view.ConfirmCloseAsync());await discard;
        }
        finally{await view.DisposeAsync();owner.Close();}
    }

    private static async Task CheckExternalSelection(OfficeTextEditorView view,Window owner,OfficeTextBrowser browser,string folder)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        { UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden };
        foreach(var arg in new[] {"-NoProfile","-STA","-File",Path.Combine(AppDataPaths.ProjectRoot!,"Тесты","OfficeEditor_20261108","selection-source.ps1"),"-Folder",folder})
            start.ArgumentList.Add(arg);
        using var source=System.Diagnostics.Process.Start(start)!;
        try
        {
            await Until(()=>File.Exists(Path.Combine(folder,"ready")),15);
            source.Refresh();
            var hwnd=source.MainWindowHandle;
            await Task.Run(()=>
            {
                var field=AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                field.SetFocus();
            }).WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(folder,"reselect"),"");await Task.Delay(300);
            var actual=await Task.Run(SelectionCapture.ReadSelectedText).WaitAsync(TimeSpan.FromSeconds(5));
            StringAssert.Contains(actual!,"Чужое выделение.");
            Assert.AreEqual(hwnd,GetForegroundWindow());
            var collector=Field<SelectionCapture>(view,"_collector");
            // The production hook deliberately rejects synthetic keystrokes. Exercise its delivery
            // path against an actual external UIA provider after verifying foreground ownership.
            await (Task)typeof(SelectionCapture).GetMethod("CaptureAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(collector,null)!;
            var text=(await browser.RequestAsync(new {action="text",selection=false},CancellationToken.None)).GetString();
            StringAssert.Contains(text!,actual!);
            Assert.AreEqual(hwnd,GetForegroundWindow());
            Assert.AreEqual(WindowState.Minimized,owner.WindowState);
            File.WriteAllText(Path.Combine(folder,"clear"),"");await Task.Delay(250);
            await (Task)typeof(SelectionCapture).GetMethod("CaptureAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(collector,null)!;
            Assert.AreEqual(text,(await browser.RequestAsync(new {action="text",selection=false},CancellationToken.None)).GetString());
        }
        finally
        {
            File.WriteAllText(Path.Combine(folder,"close"),"");
            if(!source.WaitForExit(4000))source.Kill(true);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    private static T Field<T>(object obj,string key) => (T)obj.GetType()
        .GetField(key,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;

    private static DependencyObject? Find(DependencyObject root,string id)
    {
        if (AutomationProperties.GetAutomationId(root)==id) return root;
        for (var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if (Find(VisualTreeHelper.GetChild(root,i),id) is { } found) return found;
        return null;
    }
    private static void Click(DependencyObject owner,string id)
    {
        var button=Find(owner,id) as Button;
        Assert.IsNotNull(button,id); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static Task Choose(Window owner,string id)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop=DateTime.UtcNow.AddSeconds(25);
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        timer.Tick+=(_,_) =>
        {
            foreach (Window dialog in owner.OwnedWindows)
            {
                if(Find(dialog,id) is not Button button) continue;
                timer.Stop();button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));done.TrySetResult();return;
            }
            if(DateTime.UtcNow < stop) return;
            timer.Stop();
            foreach(Window dialog in owner.OwnedWindows) dialog.Close();
            done.TrySetException(new TimeoutException(id));
        };
        timer.Start();return done.Task;
    }
    private static async Task Until(Func<bool> ready,int seconds)
    {
        var stop=DateTime.UtcNow.AddSeconds(seconds);
        while(!ready()) { if(DateTime.UtcNow>=stop)throw new TimeoutException();await Task.Delay(100); }
    }
}
