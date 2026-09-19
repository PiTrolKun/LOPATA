using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

internal static partial class Program
{
    private static void Selection(Window owner, Func<string,string> l, string prefix)
    {
        var store = new LiteraryPromptSetStore(Path.Combine(_output,prefix + "-sets.json")); store.Load();
        var preset = LiteraryPromptSets.Defaults("Selected"); preset.Actions["Discuss"] = new("CHOSEN ROLE","CHOSEN ACTION"); store.Save([preset]);
        var legacy = new PromptPairStore(Path.Combine(_output,prefix + "-old.json")); legacy.Load();
        var state = new LiteraryStudioState(); var saves = 0;
        var window = new LiteraryStudioPromptWindow(owner,state,l,() => { saves++; return true; },legacy,store);
        Inspect(() =>
        {
            Check(!Get<ComboBox>(window,"PromptSetChoice").IsVisible, "standard mode hides custom controls");
            Check(saves == 0 && state.PromptSettings is null, "opening does not write project");
            Click(window,"CustomMode"); Check(state.PromptSettings is { Custom: true, Selected: null }, "empty custom mode persisted explicitly");
            Check(All<TextBlock>(window).Any(t => t.Text == l("PromptPairs.Empty")), "empty selection explained");
            Inspect(() =>
            {
                var editor = _app.Windows.OfType<LiteraryPromptSetEditorWindow>().Single();
                Check(All<Expander>(editor).All(e => !e.IsExpanded), "new set created with collapsed groups");
                Check(Get<TextBox>(editor,"Discuss.Action").Text == LiteraryStudioPrompts.Get("Discuss").Prompt, "new set starts from built-in defaults");
                Get<TextBox>(editor,"SetName").Text = "Created"; Click(editor,"SaveSet");
            });
            Click(window,"CreateSet");
            Check(state.PromptSettings!.Selected?.Name == "Created" && store.Load().Count == 2, "new saved set selected immediately");
            Get<ComboBox>(window,"PromptSetChoice").SelectedIndex = 0; Pump();
            Check(LiteraryPromptSets.Resolve(state,"Discuss").Action == "CHOSEN ACTION", "choosing set applies it immediately");
            Capture(window,prefix + "-selection");
            Click(window,"StandardMode"); Check(LiteraryPromptSets.Resolve(state,"Discuss").Action == "", "standard mode bypasses custom set");
            Click(window,"CustomMode"); Check(LiteraryPromptSets.Resolve(state,"Discuss").Action == "CHOSEN ACTION", "toggle retains selected custom copy");
            Inspect(() =>
            {
                var manager = _app.Windows.OfType<LiteraryPromptSetManagerWindow>().Single();
                Inspect(() =>
                {
                    var editor = _app.Windows.OfType<LiteraryPromptSetEditorWindow>().Single();
                    Get<TextBox>(editor,"Discuss.Action").Text = "EDITED ACTION"; Click(editor,"SaveSet");
                });
                Click(manager,"EditSet"); Click(manager,"CloseManager");
            });
            Click(window,"ManageSets");
            Check(LiteraryPromptSets.Resolve(state,"Discuss").Action == "EDITED ACTION", "management edit updates selected project copy");
            Click(window,"ClosePrompts");
        }); window.ShowDialog();
        store.Save([]);
        var reopened = new LiteraryStudioPromptWindow(owner,state,l,() => true,legacy,store);
        Inspect(() =>
        {
            Check(state.PromptSettings!.Custom && LiteraryPromptSets.Resolve(state,"Discuss").Action == "EDITED ACTION", "deleted library entry cannot silently reset project");
            Check(Get<ComboBox>(reopened,"PromptSetChoice").SelectedItem!.ToString()!.Contains(l("Studio.PromptSet.ProjectCopy")), "orphan marked as project copy");
            Inspect(() => { var editor = _app.Windows.OfType<LiteraryPromptSetEditorWindow>().Single(); Click(editor,"SaveSet"); });
            Click(reopened,"SaveProjectCopy"); Check(store.Load().Count == 1, "orphan can be saved back to library");
            Check(LiteraryPromptSets.Resolve(state,"Discuss").Action == "EDITED ACTION", "saving snapshot does not alter texts");
            Click(reopened,"ClosePrompts");
        }); reopened.ShowDialog();
        var failed = new LiteraryStudioPromptWindow(owner,state,l,() => false,legacy,store);
        Inspect(() =>
        {
            Click(failed,"StandardMode"); Check(state.PromptSettings!.Custom, "failed save restores custom mode");
            Check(All<TextBlock>(failed).Any(t => t.Text == l("Paragraph.SaveError")), "save failure shown");
            Click(failed,"ClosePrompts");
        }); failed.ShowDialog();
        var brokenPath = Path.Combine(_output,prefix + "-broken.json"); File.WriteAllText(brokenPath,"broken");
        var broken = new LiteraryStudioPromptWindow(owner,state,l,() => true,legacy,new(brokenPath));
        Inspect(() =>
        {
            Click(broken,"StandardMode"); Click(broken,"CustomMode");
            Check(!Get<Button>(broken,"CreateSet").IsEnabled && !Get<Button>(broken,"ManageSets").IsEnabled, "broken library cannot be overwritten");
            Check(All<TextBlock>(broken).Any(t => t.Text == l("PromptPairs.StorageError")), "library error survives mode toggles");
            Click(broken,"ClosePrompts");
        }); broken.ShowDialog();
    }

    private static void Manager(Window owner, Func<string,string> l, string prefix)
    {
        var one = LiteraryPromptSets.Defaults("One"); var two = LiteraryPromptSets.Defaults("Two");
        IReadOnlyList<LiteraryPromptSet> stored = [one,two];
        var window = new LiteraryPromptSetManagerWindow(owner, stored,l, next => { stored = next; return null; }, selectedId: one.Id);
        Inspect(() =>
        {
            Check(!Get<Button>(window,"MoveUp").IsEnabled && Get<Button>(window,"MoveDown").IsEnabled, "move availability");
            Click(window,"MoveDown"); Check(stored[1].Id == one.Id, "move down saved");
            Click(window,"MoveUp"); Check(stored[0].Id == one.Id, "move up saved");
            Inspect(() =>
            {
                var rename = _app.Windows.OfType<Window>().Single(w => w.Owner == window);
                All<TextBox>(rename).Single().Text = "Renamed";
                All<Button>(rename).Single(b => Equals(b.Content,l("PromptPairs.Save"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }); Click(window,"RenameSet"); Check(stored[0].Name == "Renamed", "rename saved");
            ConfirmNext(false); Click(window,"DeleteSet"); Check(stored.Count == 2, "declined delete retains set");
            ConfirmNext(true); Click(window,"DeleteSet"); Check(stored.Count == 1 && stored[0].Id == two.Id, "confirmed delete removes only selected set");
            Check(!Get<Button>(window,"EditSet").IsEnabled, "no selection disables edit");
            Capture(window,prefix + "-manager"); Click(window,"CloseManager");
        }); window.ShowDialog();
    }

    // Dismiss only a native confirmation on this synthetic probe's own UI thread.
    private static void ConfirmNext(bool yes)
    {
        var thread = GetCurrentThreadId(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_,_) =>
        {
            EnumThreadWindows(thread,(handle,_) =>
            {
                var name = new StringBuilder(64); GetClassName(handle,name,name.Capacity);
                if (name.ToString() != "#32770") return true;
                timer.Stop(); PostMessage(handle,0x0111,(IntPtr)(yes ? 6 : 7),IntPtr.Zero); return false;
            },IntPtr.Zero);
            if (watch.Elapsed > TimeSpan.FromSeconds(4)) { timer.Stop(); Console.Error.WriteLine("Confirmation not found in probe process."); Environment.Exit(1); }
        };
        timer.Start();
    }
    private delegate bool EnumWindow(IntPtr handle,IntPtr value);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,EnumWindow callback,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle,StringBuilder value,int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam);
}
