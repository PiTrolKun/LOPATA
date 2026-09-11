using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

static class AutosaveProbe
{
    public static void Run(Window owner, string output)
    {
        var registry = new LiteraryProjectStore(Path.Combine(output, "registry.json"));
        var entry = registry.Create(output, new() { ProjectName = "Autosave", Genres = ["fantasy"] }, []);
        var store = new LiteraryChapterStore(entry.ProjectPath); store.Open(); store.SetAutosave(10);
        var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", "ru.json")))!;
        var draft = new LiteraryDraftControl(entry.ProjectPath, key => words[key]);
        owner.Content = draft; owner.UpdateLayout();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var editor = (TextBox)typeof(LiteraryDraftControl).GetField("_editor", flags)!.GetValue(draft)!;
        var status = (TextBlock)typeof(LiteraryDraftControl).GetField("_status", flags)!.GetValue(draft)!;
        var timer = (DispatcherTimer)typeof(LiteraryDraftControl).GetField("_timer", flags)!.GetValue(draft)!;
        Wait(() => draft.IsLoaded && timer.IsEnabled, 5);
        editor.Text = "Первая версия";
        var dirty = status.Text;
        Wait(() => File.ReadAllText(store.FilePath) == editor.Text && status.Text.StartsWith("Сохранено:"), 15);
        var saved = status.Text; var savedAt = File.GetLastWriteTimeUtc(store.FilePath);
        var until = DateTime.UtcNow.AddSeconds(11); Wait(() => DateTime.UtcNow >= until, 15);
        if (File.GetLastWriteTimeUtc(store.FilePath) != savedAt || status.Text != saved)
            throw new Exception("Unchanged draft was rewritten or its save time invented.");
        editor.AppendText(". Вторая версия");
        Wait(() => File.GetLastWriteTimeUtc(store.FilePath) > savedAt && File.ReadAllText(store.FilePath) == editor.Text && status.Text.StartsWith("Сохранено:"), 15);
        if (status.Text == saved) throw new Exception("Saved file changed, status did not update.");
        File.WriteAllText(Path.Combine(output, "autosave.json"), JsonSerializer.Serialize(new { dirty, firstSaved = saved, afterIdle = saved, secondSaved = status.Text, timer.IsEnabled, seconds = timer.Interval.TotalSeconds, text = File.ReadAllText(store.FilePath) }));
        owner.Content = null; owner.UpdateLayout();
    }
    private static void Wait(Func<bool> done, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Autosave probe timeout.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(20);
        }
    }
}
