using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

internal static class NavigationProbe
{
    internal static void Run(string output, Func<string, string> l)
    {
        var root = Program.Create(Path.Combine(output, "navigation"));
        var store = new LiteraryChapterStore(root); store.Open();
        var first = store.Active.Id;
        store.Continue(["Вторая часть главы."]);
        var second = store.Active.Id;
        store.Finish();
        var last = store.Active.Id;
        var count = store.Index.Parts.Count;
        var draft = new LiteraryDraftControl(root, l);
        var saves = 0; draft.PartSaved += _ => saves++;
        Program.Check(draft.ViewedId == last && draft.IsLatestViewed,"newest working file is open initially");
        Program.Check(((Button)Program.Field(draft,"_nextPart")!).Visibility == System.Windows.Visibility.Hidden,
            "next arrow is hidden at the end");
        Program.Check(draft.Navigate(-1) && draft.ViewedId == second && draft.Text == "Вторая часть главы.",
            "previous arrow opens the preceding existing part");
        var snapshot = draft.Capture("probe", root);
        var reader = new LiteraryProjectReader(snapshot);
        var result = reader.Execute(new LiteraryReadAction("read", snapshot.Active.Number), CancellationToken.None);
        Program.Check(result.Json.Contains("Вторая часть главы."),"reader accepts the browsed part without changing the persisted active part");
        var box = (TextBox)Program.Field(draft,"_editor")!;
        box.Text = "Вторая часть с правкой.";
        Program.Check(draft.Navigate(-1) && draft.ViewedId == first,"editing an older part can be followed by navigation");
        Program.Check(store.LoadPart(second) == "Вторая часть с правкой." && store.LoadPart(last).Length == 0,
            "old-part save touches its own file and leaves the newest part alone");
        Program.Check(draft.Navigate(1) && draft.Navigate(-1) && draft.NavigateToLast(),"A-B-A browsing and return to latest work");
        Program.Check(store.Index.ActiveId == last && store.Index.Parts.Count == count && saves == 1,
            "browsing creates neither files nor duplicate saves");
        Program.Check(draft.IsSnapshotCurrent(snapshot) == false,"an edited part invalidates its prior request snapshot");
        Program.Check(draft.Navigate(-1),"return to edited part");
        snapshot = draft.Capture("probe",root);
        Program.Check(draft.NavigateToLast() && draft.IsSnapshotCurrent(snapshot),"view-only navigation does not stale an accepted request");
        draft.Navigate(-1);
        box.Text = "Несохранённая локальная правка";
        File.WriteAllText(store.PartPath(second),"Внешнее изменение");
        Program.Check(!draft.Save() && File.ReadAllText(store.PartPath(second)) == "Внешнее изменение",
            "external conflict keeps both versions without overwriting either");
        VerifySendChoice(output, l);
    }

    private static void VerifySendChoice(string output, Func<string, string> l)
    {
        var root = Program.Create(Path.Combine(output, "choice"));
        var store = new LiteraryChapterStore(root); store.Open();
        var oldId = store.Active.Id;
        store.Finish(); var lastId = store.Active.Id;
        using var runtime = new LiteraryChatRuntime(root);
        var draft = new LiteraryDraftControl(root, l);
        var requests = new Requests();
        var studio = new LiteraryStudioControl(root,draft,new ContentControl { Content = draft },runtime,()=>false,l,"ru",
            new TextBlock(),new TextBlock(),requests);
        var window = new Window { Width = 1100, Height = 740, Content = studio, Resources = Application.Current.Resources,
            ShowInTaskbar = false };
        window.Show(); Program.Pump();
        Program.Check(draft.Navigate(-1) && draft.ViewedId == oldId,"historical part is open before sending");
        var input = (TextBox)Program.Field(studio,"_input")!;

        void Submit(int choice)
        {
            var picked = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.Owner == window && w.Title == l("Studio.PartChoice.Title"));
                if (dialog is null) return;
                timer.Stop(); picked = true;
                if (choice < 0) dialog.Close();
                else Program.All<Button>(dialog).Single(b => Equals(b.Content,
                    l(choice == 0 ? "Studio.PartChoice.Open" : "Studio.PartChoice.Last")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            var task = (Task)typeof(LiteraryStudioControl).GetMethod("SendAsync",BindingFlags.Instance|BindingFlags.NonPublic)!
                .Invoke(studio,[false])!;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) Program.Pump();
            timer.Stop();
            Program.Check(picked && task.IsCompleted,"part choice resolves without a stuck request");
            task.GetAwaiter().GetResult();
        }

        input.Text = "Отменённый запрос"; Submit(-1);
        Program.Check(input.Text == "Отменённый запрос" && requests.Calls.Count == 0 && draft.ViewedId == oldId,
            "closing choice preserves input and source without sending");
        input.Text = "О старой части"; Submit(0);
        Program.Check(requests.Calls.Count == 1 && requests.Calls[0].Base.Editor.ActiveId == oldId
            && draft.ViewedId == oldId,"send-open uses the historical part once");
        input.Text = "О последней части"; Submit(1);
        Program.Check(requests.Calls.Count == 2 && requests.Calls[1].Base.Editor.ActiveId == lastId
            && draft.ViewedId == lastId,"return-and-send actually switches to the latest part");
        window.Close(); Program.Pump();
    }

    private sealed class Requests : ILiteraryStudioRequests
    {
        public bool IsBusy => false;
        public int ContextCapacity => 8192;
        public List<StudioRequest> Calls { get; } = [];
        public Task<ParagraphReply> StudioAsync(StudioRequest request, Func<string,string> localize,
            Action<ParagraphReceipt> receipt, Action<int> budget, IProgress<ModelStreamChunk> progress,
            CancellationToken cancellation, Action? attemptStarting = null, Action? preparationStarted = null)
        {
            Calls.Add(request); preparationStarted?.Invoke(); budget(100);
            return Task.FromResult(new ParagraphReply("Готово",[],new([],[])));
        }
    }
}
