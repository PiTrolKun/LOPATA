using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;

Exception? failure = null;
var thread = new Thread(() =>
{
    var root = Path.Combine(Path.GetTempPath(), "lopata-editor-ui-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var ru = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Исходники/AIHub/Localization/ru.json"))!;
        var en = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Исходники/AIHub/Localization/en.json"))!;
        string L(string key) => ru.GetValueOrDefault(key, key);
        var window = new Window { Width = 1180, Height = 730, Left = -4000, Top = 0, ShowInTaskbar = false, ShowActivated = false };
        Theme(window, true);
        var control = new LiteraryDraftControl(root, L); window.Content = control; window.Show();
        var editor = Field<TextBox>(control, "_editor"); var status = Field<TextBlock>(control, "_status"); var counts = Field<TextBlock>(control, "_counts");
        if (args.Contains("--repro-disabled-dispatcher"))
        {
            Task insertion;
            using (Dispatcher.CurrentDispatcher.DisableProcessing()) insertion = control.InsertTextAsync(new string('я', 8000));
            Console.WriteLine("Insertion status: " + insertion.Status);
            Console.WriteLine(insertion.Exception?.ToString());
            foreach (var orphan in app.Windows.Cast<Window>().Where(w => w != window).ToArray())
            {
                Console.WriteLine("Orphan dialog visible: " + orphan.IsVisible);
                try { Click(orphan,L("Literary.Editor.Cancel")); } catch(Exception ex) { Console.WriteLine("Click: " + ex.Message); }
                orphan.Close();
            }
            window.Close(); app.Shutdown(); return;
        }
        Check(status.Text.StartsWith("Сохранено:"), "Actual initial file timestamp missing");
        var firstSnapshot = control.Capture("ui-project", root);
        editor.Text = "Ручная правка до автосохранения";
        var changedSnapshot = control.Capture("ui-project", root);
        Check(changedSnapshot.Text == editor.Text && changedSnapshot.Unsaved && changedSnapshot.Revision != firstSnapshot.Revision,
            "The request snapshot missed an unsaved editor change");
        Check(control.Store.Load() == "", "Capturing model input unexpectedly saved the draft");
        editor.Clear();
        // Reproduce the native Paste change-block restriction, not merely a synthetic routed event.
        using (var clicks = DialogActions(w => Click(w,L("Literary.Editor.Cancel"))))
        {
            Task insertion;
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                insertion = control.InsertTextAsync(new string('я',8000));
                Check(!insertion.IsCompleted && app.Windows.Count==1,"Modal started inside native paste transaction");
            }
            Pump(insertion);
        }
        Check(control.Text=="" && app.Windows.Count==1,"Cancelled deferred paste left text or orphan window");
        foreach(var key in new[] {"Literary.Editor.Cancel","Literary.Editor.EditPaste","Literary.Editor.Continue"})
        {
            var actions = new List<Action<Window>> { w => Click(w,L(key)) };
            if(key=="Literary.Editor.EditPaste") actions.Add(w=> { Descendants<TextBox>(w).Single().Text="Изменённая вставка"; Click(w,L("Literary.Editor.Apply")); });
            if(key=="Literary.Editor.Continue") actions.Add(w=>Click(w,L("Literary.Editor.Split")));
            using(var clicks=DialogActions(actions.ToArray()))
            {
                Task insertion;
                using(Dispatcher.CurrentDispatcher.DisableProcessing()) insertion=control.InsertTextAsync(new string('а',7000)+"\n"+new string('б',7000));
                Pump(insertion);
            }
            Check(app.Windows.Count==1,"Deferred paste left an orphan dialog");
        }
        Check(control.Store.Active.Part==3 && control.Text==new string('б',7000),"Deferred native paste split failed");
        // Use a fresh project for the original baseline assertions.
        control = new LiteraryDraftControl(Path.Combine(root,"baseline"),L); window.Content=control; window.UpdateLayout();
        editor=Field<TextBox>(control,"_editor"); status=Field<TextBlock>(control,"_status"); counts=Field<TextBlock>(control,"_counts");
        var pasteEvent = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText,"Через событие вставки"),false,DataFormats.UnicodeText) { RoutedEvent=DataObject.PastingEvent };
        using(Dispatcher.CurrentDispatcher.DisableProcessing()) editor.RaiseEvent(pasteEvent);
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background).Task);
        Check(pasteEvent.CommandCancelled && control.Text=="Через событие вставки", "Common Ctrl+V/context-menu paste handler"); editor.SelectAll();
        Pump(control.InsertTextAsync("Привет\nВторая строка"));
        Check(control.Text == "Привет\r\nВторая строка", "Pasted newlines/count representation");
        Check(counts.Text.Contains(control.Text.Length.ToString()), "Counter");
        Pump(control.SaveAsync()); Check(control.Store.Load() == control.Text, "Saved text differs from editor");
        editor.SelectAll(); Pump(control.InsertTextAsync(new string('я', 7500)));
        var composition = new TextComposition(InputManager.Current, editor, "x");
        var input = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        editor.Select(7500,0); editor.RaiseEvent(input); Check(input.Handled && control.Text.Length == 7500, "Manual limit");
        editor.Select(0,2); Pump(control.InsertTextAsync("да")); Check(control.Text.Length == 7500 && control.Text.StartsWith("да"), "Selection replacement at cap");
        editor.Undo(); Check(control.Text.StartsWith("яя"), "Undo valid replacement");
        editor.Select(7500,0);
        using (var click = DialogActions((w) => Click(w, L("Literary.Editor.Cancel"))))
            Pump(control.InsertTextAsync("НЕ ВСТАВЛЯТЬ"));
        Check(control.Text.Length == 7500, "Cancelled overflow changed original");
        editor.Select(0,2);
        using (var click = DialogActions(w => Click(w, L("Literary.Editor.EditPaste")), w => { Descendants<TextBox>(w).Single().Text = "ок"; Click(w,L("Literary.Editor.Apply")); }))
            Pump(control.InsertTextAsync("Слишком длинная замена"));
        Check(control.Text.StartsWith("ок"), "Edit pending paste");
        var before = control.Text; var firstPath = control.Store.FilePath;
        editor.Select(0,1);
        using (var click = DialogActions(w => Click(w, L("Literary.Editor.Continue"))))
            Pump(control.InsertTextAsync("Новое продолжение"));
        Check(control.Text == "Новое продолжение" && control.Store.Active.Part == 2, "Whole paste must move to next part");
        Check(File.ReadAllText(firstPath) == before, "Continuation deleted selected original text");
        Check(!editor.CanUndo, "Undo may cross chapter boundaries");
        editor.Select(editor.Text.Length,0);
        var huge = new string('а', 7000) + "\r\n" + new string('б', 7000);
        using (var click = DialogActions(w => Click(w,L("Literary.Editor.Continue")), w => Click(w,L("Literary.Editor.Split"))))
            Pump(control.InsertTextAsync(huge));
        Check(control.Store.Active.Part == 4 && control.Text == new string('б',7000), "Large paste parts");
        Pump(control.FinishAsync()); Check(control.Store.Active.Chapter == 2 && control.Text == "", "Finish resets active editor");
        editor.Text = "Автосохранение";
        control.Store.SetAutosave(10);
        typeof(LiteraryDraftControl).GetMethod("SetTimer", BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(control,null);
        var saveTimer = Field<DispatcherTimer>(control,"_timer");
        Check(saveTimer.Interval == TimeSpan.FromSeconds(10), "Actual timer setting");
        // Accelerate the timer only in the harness; keep changing text faster than the interval.
        saveTimer.Interval = TimeSpan.FromMilliseconds(80);
        var typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        int ticks = 0; typing.Tick += (_,_) => { editor.AppendText("я"); ticks++; }; typing.Start();
        Pump(Task.Delay(380)); typing.Stop(); saveTimer.Stop();
        var saveDeadline = DateTime.UtcNow.AddSeconds(5);
        while (Field<bool>(control,"_busy")) { Check(DateTime.UtcNow < saveDeadline,"Autosave did not settle"); Pump(Task.Delay(10)); }
        Check(ticks > 5 && control.Store.Load().StartsWith("Автосохранениея"), "Continuous typing postponed periodic autosave");
        Pump(control.SaveAsync());
        editor.AppendText("несохранённое");
        using (var locked = new FileStream(control.Store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var prior = control.Text; var time = control.Store.LastSaved;
            Check(!control.Save(), "Locked primary must fail");
            Check(control.Text == prior && control.Store.LastSaved == time, "Failed save changed text or timestamp");
            using (var click = DialogActions(w => Click(w,L("Literary.Editor.Stay")))) Check(!control.CanLeave(),"Stay must cancel navigation");
            using (var click = DialogActions(w => Click(w,L("Literary.Editor.Discard")))) Check(control.CanLeave(),"Explicit discard must allow exit");
        }
        Check(control.Save(), "Explicitly discarded edits should not be saved on unload");
        Check(!control.Store.Load().Contains("несохранённое"), "Discard was undone by unload save");
        editor.AppendText(" новая правка"); Pump(control.SaveAsync());
        // Check both dictionaries and render both themes without invoking any model.
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            Theme(window,dark); var dictionary = language == "ru" ? ru : en;
            control.ApplyLocalization(k => dictionary.GetValueOrDefault(k,k));
            Check(!status.Text.Contains("Literary."), "Unlocalized status");
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1160,690,96,96,PixelFormats.Pbgra32); bitmap.Render(control);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create($"Тесты/LiteraryEditor/{language}-{(dark ? "dark" : "light")}.png"); png.Save(file);
        }
        // The full workspace owns the draft and reattaches it when chapter title or theme changes.
        var entry = new LiteraryProjectEntry("ui-test", "Тестовый проект", root);
        var workspace = new LiteraryWorkspaceControl(entry, new LiteraryProject { WorkTitle = "Ночной вокзал" }, L);
        window.Content = workspace; window.UpdateLayout();
        var workspaceDraft = Field<LiteraryDraftControl>(workspace,"_draft");
        Pump(workspaceDraft.InsertTextAsync("Лера остановилась у края платформы.\nЧасы показывали 03:17."));
        Pump(workspaceDraft.SaveAsync());
        AIHub.Services.LiteraryDocxExporter.Export(workspaceDraft.Store.Snapshot(),"Тесты/LiteraryEditor/sample.docx");
        foreach (var dark in new[] {true,false})
        {
            Theme(window,dark); workspace.ApplyLocalization(L); window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1160,690,96,96,PixelFormats.Pbgra32); bitmap.Render(workspace);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create($"Тесты/LiteraryEditor/workspace-{(dark ? "dark" : "light")}.png"); png.Save(file);
        }
        Check(workspace.CanLeave(),"Workspace leave");
        // Format selection precedes file access; cancelling it must not create/migrate a project.
        var exportDialog = typeof(LiteraryDraftControl).Assembly.GetType("AIHub.Controls.LiteraryExportDialog")!;
        var cancelledProject = Path.Combine(root,"cancelled-export");
        using (var click = DialogActions(w => { var formats=Descendants<ComboBox>(w).Single(); Check(formats.Items.Count==1 && formats.SelectedIndex==0,"DOCX-only format selection"); w.Close(); }))
            Pump((Task)exportDialog.GetMethod("ShowAsync")!.Invoke(null,[workspace,(Func<string,string>)L,cancelledProject,"Test",null])!);
        Check(!Directory.Exists(cancelledProject),"Cancelled export modified project");
        window.Close(); app.Shutdown();
        Console.WriteLine("PASS: WPF limits, selection/undo, pending paste cancel/edit/continue/split, finish, continuous autosave, save error/navigation/discard, RU/EN and both themes. No model requests.");
    }
    catch (Exception e) { failure = e; }
    finally
    {
        try { Directory.Delete(root,true); }
        catch (IOException cleanup) { Console.WriteLine("Temporary stand cleanup deferred: " + root + ": " + cleanup.Message); }
    }
});
thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

static T Field<T>(object target, string name) => (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
static void Pump(Task task)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!task.IsCompleted)
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF harness timed out");
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => frame.Continue=false)); Dispatcher.PushFrame(frame);
    }
    task.GetAwaiter().GetResult();
}
static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
{
    for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
    {
        var child = VisualTreeHelper.GetChild(parent,i); if(child is T found) yield return found;
        foreach(var item in Descendants<T>(child)) yield return item;
    }
}
static void Click(Window window,string text) => Descendants<Button>(window).Single(b => Equals(b.Content,text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
static IDisposable DialogActions(params Action<Window>[] actions) => new DialogClicker(actions);
static void Theme(Window window,bool dark)
{
    foreach(var (key,value) in new[] { ("PanelBrush",dark?"#172033":"#FFFFFF"),("TextPrimaryBrush",dark?"#F8FAFC":"#1F1F1F"),("TextSecondaryBrush",dark?"#A5B4CB":"#475569"),("LineBrush",dark?"#2D374B":"#CBD5E1"),("SecondaryButtonBackgroundBrush",dark?"#101827":"#F8FAFC"),("AccentBrush","#2563EB") })
        window.Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    window.Resources["UiBodyFontSize"] = 16d; window.Resources["UiCardTitleFontSize"] = 22d;
    foreach(var key in new[] {"PrimaryButtonStyle","SecondaryButtonStyle"})
    {
        var style=new Style(typeof(Button)); style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,8,12,8)));
        style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(dark ? Color.FromRgb(16,24,39):Colors.White)));
        style.Setters.Add(new Setter(Control.ForegroundProperty,new SolidColorBrush(dark ? Colors.White:Colors.Black)));
        window.Resources[key]=style;
    }
}
sealed class DialogClicker : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval=TimeSpan.FromMilliseconds(25) };
    private readonly Queue<Action<Window>> _actions;
    private readonly HashSet<Window> _visited=[];
    public DialogClicker(IEnumerable<Action<Window>> actions)
    {
        _actions=new(actions); _timer.Tick+=(_,_)=>
        {
            var window=Application.Current.Windows.Cast<Window>().LastOrDefault(w=>w.Owner is not null && w.IsVisible && !_visited.Contains(w));
            if(window is null || _actions.Count==0)return;
            _visited.Add(window); window.UpdateLayout(); _actions.Dequeue()(window);
        }; _timer.Start();
    }
    public void Dispose() { _timer.Stop(); if(_actions.Count>0)throw new Exception("Expected dialog did not open"); }
}
