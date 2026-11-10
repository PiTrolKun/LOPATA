using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OneC;
using AIHub.Models.OneC;
using AIHub.Services;
using AIHub.Services.OneC;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class OneCUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task EditorProjectsDraftsAndResponsiveGeneration(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_ONEC_UI") != "1") Assert.Inconclusive("Explicit WPF scenario test required.");
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-UI-" + Guid.NewGuid().ToString("N"));
        var store = new OneCProjectStore(root); var project = store.Create("Учебный проект / Learning".Replace('/', '·'));
        var own = store.CreateFile(project, "Module.bsl", "Функция Сумма(А, Б) Экспорт\n    Возврат А - Б;\nКонецФункции\n");
        project.OpenFileIds.Add(own.Id); project.ModelId = "fixture"; OneCProtocol.Bind(project.Task, own); store.Save(project);
        var l = new LocalizationService(); l.Load(language); var owner = Window(dark);
        var model = new DelayedModel(); var control = new OneCControl(owner, l.T, store, () => model, new StaticFixture()); owner.Content = control; owner.Show();
        try
        {
            await control.EnterAsync(project.Id); await Pump();
            Click(control, "OneC.FixTemplate"); await Pump();
            var prompt = Find<TextBox>(control, "OneC.Prompt"); Assert.IsTrue(prompt.Text.Length > 10);
            prompt.Text = language == "ru" ? "Исправь вычитание на сложение." : "Replace subtraction with addition.";
            Assert.IsTrue(control.Checkpoint()); Assert.AreEqual(prompt.Text, store.Load(project.Id).Task.Prompt);
            var run = control.GenerateAsync(); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A posted dispatcher action and editing must remain available while inference is pending.
            var responsive = false; await Dispatcher.CurrentDispatcher.InvokeAsync(() => responsive = true); Assert.IsTrue(responsive);
            owner.Left += 50; owner.Top += 25; owner.Width = 1024; await Pump();
            await control.CancelProcessingAsync(); await run; await Pump();
            Assert.IsNull(store.Load(project.Id).Pending, "Explicit cancel must allow a fresh task, while pause remains resumable.");
            run = control.GenerateAsync(); await Pump();
            model.Reply.TrySetResult(); await run; await Pump();
            Assert.HasCount(1, store.Load(project.Id).Proposals);
            var result = Find<FrameworkElement>(control, "OneC.Proposal." + control.Project!.ActiveProposalId);
            Assert.IsNotNull(result);
            // Rename/open project through the store while the control saves only current edit fields.
            var p = store.Update(project.Id, p => p.Name = "Renamed"); Assert.IsTrue(control.Checkpoint()); Assert.AreEqual("Renamed", store.Load(p.Id).Name);
            Click(control, "OneC.ProjectsToggle"); await Pump(); Assert.IsFalse(store.Load(p.Id).SidebarVisible);
            Click(control, "OneC.ProjectsToggle"); await Pump(); Assert.IsTrue(store.Load(p.Id).SidebarVisible);
            Assert.IsFalse(l.T("OneC.PrepareProposal").StartsWith("OneC.", StringComparison.Ordinal));
            var evidence = Environment.GetEnvironmentVariable("LOPATA_ONEC_UI_OUTPUT") ?? Path.Combine(AppDataPaths.ProjectRoot!, "_tmp", "onec-ui"); Directory.CreateDirectory(evidence);
            SaveImage(owner, Path.Combine(evidence, language + (dark ? "-dark" : "-light") + ".png"));
            Assert.IsTrue(control.Checkpoint()); Assert.AreEqual(own.Code, store.Load(project.Id).Files.Single(f => f.Id == own.Id).Code);
        }
        finally { owner.Close(); await Pump(); Directory.Delete(root, true); }
    });
    [TestMethod] public Task CodeSelectionUndoAndReadOnlyCommands() => Sta(async () =>
    {
        var l = new LocalizationService(); l.Load("ru"); var owner = Window(true);
        var editor = new OneCCodeEditor(l.T, "Test.Editor"); owner.Content = editor; owner.Show();
        try
        {
            editor.SetText("А = 1;\r\nБ = 2;\r\n"); editor.Editor.Document.Replace(4, 1, "3"); editor.Editor.Undo(); Assert.IsTrue(editor.Text.StartsWith("А = 1;", StringComparison.Ordinal));
            editor.Restore(8, 6, 0); Assert.AreEqual("Б = 2;", editor.SelectedText);
            editor.ReadOnly = true; Assert.IsTrue(editor.Editor.IsReadOnly);
            var menu = editor.Editor.ContextMenu; Assert.IsNotNull(menu); Assert.IsTrue(menu.Items.Count >= 9);
            await Pump();
        }
        finally { owner.Close(); }
    });
    private static Window Window(bool dark)
    {
        var owner = new Window { Width = 1400, Height = 900 };
        foreach (var (key, value) in new[] { ("WindowBackgroundBrush", dark ? "#111827" : "#F3F3F3"), ("PanelBrush", dark ? "#172033" : "#FFFFFF"),
            ("InputBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#202735"), ("TextSecondaryBrush", dark ? "#AAB4C4" : "#516076"),
            ("LineBrush", dark ? "#2D374B" : "#CCD3DF"), ("AccentBrush", "#2563EB"), ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8") })
            owner.Resources[key] = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        owner.Resources["UiBodyFontSize"] = 14d; owner.SetResourceReference(System.Windows.Window.BackgroundProperty, "WindowBackgroundBrush"); return owner;
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
        => Children(root).OfType<T>().FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == id) ?? throw new InvalidOperationException(id + " missing; visible IDs: " + string.Join(",", Children(root).Select(x => AutomationProperties.GetAutomationId(x)).Where(x => x.Length > 0)));
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Children(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Click(DependencyObject root, string id) => Find<Button>(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Pump() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(90); }
    private static void SaveImage(Window owner, string path)
    {
        owner.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(owner);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
    private sealed class DelayedModel : IOneCModelSession
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OneCModelResponse> GenerateAsync(OneCJob job, CancellationToken token)
        {
            Entered.TrySetResult(); await Reply.Task.WaitAsync(token);
            return new(JsonSerializer.Serialize(new { Explanation = "В функции меняем вычитание на сложение.", Code = "Функция Сумма(А, Б) Экспорт\n    Возврат А + Б;\nКонецФункции\n", SuggestedName = "Module.bsl", InsertLocation = "Общий модуль", MissingContext = Array.Empty<string>() }), []);
        }
        public void Dispose() { }
    }
    private sealed class StaticFixture : IOneCCodeChecker
    {
        public string Id => "fixture";
        public Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token)
            => Task.FromResult(new OneCCheck(OneCProjectStore.Hash(code), Id, "1", "clear", [], DateTimeOffset.UtcNow));
    }
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); ready.SetResult(dispatcher); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Value.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } }); return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
