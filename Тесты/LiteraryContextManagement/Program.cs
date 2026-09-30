using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { _checks++; if (!value) throw new Exception(name); }
    private static IEnumerable<T> All<T>(DependencyObject root)
    { if (root is T item) yield return item; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Pump(int milliseconds = 150)
    { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static Button Button(Window window, string id) => All<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == id);
    private static void Shot(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
        { context.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static string Create(string output)
    {
        var root = Path.Combine(output, "project"); Directory.CreateDirectory(root);
        var project = new LiteraryProject { ProjectName = "Context probe", Genres = ["adventure"] };
        File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(project)); new LiteraryProjectLayout(root).Initialize();
        var chapters = new LiteraryChapterStore(root); chapters.Open(); chapters.Save("Авторский текст не должен измениться."); return root;
    }
    private static LiteraryStudioState State()
    {
        var state = new LiteraryStudioState { Input = "Неотправленный вопрос", Quotes = [new("книга", "Выбранная цитата")] };
        for (var i = 0; i < 16; i++) state.Add(i % 2 == 0 ? "User" : "Advisor", $"Реплика {i + 1}. " + string.Concat(Enumerable.Repeat("Обсуждаем решение автора, ограничения и варианты продолжения. ", 5)));
        return state;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath("Тесты/LiteraryContextManagement/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(3), null, args.Contains("--model") ? 300000 : 90000, Timeout.Infinite);
        try
        {
            var root = Create(output); var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var xml = XDocument.Load("Исходники/AIHub/MainWindow.xaml"); XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration));
            foreach (var element in xml.Root.Element(ns + "Window.Resources")!.Elements())
                if (element.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" || new[] { "PrimaryButtonStyle", "SecondaryButtonStyle" }.Contains((string?)element.Attribute(x + "Key"))) dictionary.Add(new XElement(element));
            foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { true, false })
            {
                var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                foreach (var pair in new[] { ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8"), ("WindowBackgroundBrush", dark ? "#111827" : "#F3F3F3"), ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F3F4F6" : "#172033"), ("TextSecondaryBrush", dark ? "#AAB8CD" : "#5C687A") }) resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
                var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"Исходники/AIHub/Localization/{language}.json"))!;
                string L(string key) => translations.GetValueOrDefault(key, key);
                var owner = new Window { Width = 900, Height = 720, Resources = resources, ShowInTaskbar = false }; app.MainWindow = owner;
                var ring = new LiteraryContextIndicator(); ring.Update(new(7600, 8512), "95%", "Context"); owner.Content = ring; owner.Show(); Pump();
                Check(ring.IsTabStop && ring.Width == ring.Height && ring.Template is not null, "ring keyboard and shape");
                var state = State(); var plan = StudioContextPlan.Capture(state); var stamp = StudioContextPlan.Stamp(state);
                var measuredIds = 0; var applied = false;
                Task<StudioContextMeter?> Measure(IReadOnlySet<string> ids, string? summary, CancellationToken token)
                { token.ThrowIfCancellationRequested(); measuredIds = ids.Count; return Task.FromResult<StudioContextMeter?>(new(7600 - ids.Count * 300, 8512)); }
                Task<bool> Apply(IReadOnlySet<string> ids, string? summary, CancellationToken token) { applied = true; return Task.FromResult(true); }
                foreach (var pair in new[] { (false, 1000, StudioContextMethod.Manual), (false, 6000, StudioContextMethod.Retelling), (true, 1000, StudioContextMethod.Smart) })
                {
                    var manager = new LiteraryContextManagerWindow(owner, L, plan, new(pair.Item2, 8512), pair.Item1, Measure,
                        (_, _, _, _) => Task.FromResult(new StudioCompactionResult("Пересказ", plan.Items.Take(12).Select(i => i.Id).ToHashSet(), 8000, 3000, .5)),
                        (_, _, _, _) => Task.FromResult(false));
                    manager.Show(); Pump();
                    Check(Button(manager, "Studio.Context.Choose." + pair.Item3).Style == resources["PrimaryButtonStyle"], "recommended method " + pair.Item3);
                    Check(!All<TextBlock>(manager).Any(t => t.Text.StartsWith("Studio.Context.")), "localized manager");
                    foreach (var button in All<Button>(manager)) Check(button.ActualHeight >= ((FrameworkElement)button.Content).DesiredSize.Height + button.Padding.Top + button.Padding.Bottom, "choice content not clipped");
                    foreach (var width in new[] { 480d, 820d })
                    {
                        manager.Width = width;
                        foreach (var scale in new[] { 1d, 1.25d })
                        {
                            manager.Resources["UiBodyFontSize"] = 14d * scale; manager.Resources["UiCardTitleFontSize"] = 20d * scale;
                            Pump();
                            var starts = All<Button>(manager).Select(button =>
                            {
                                var label = All<TextBlock>((DependencyObject)button.Content).First();
                                var left = label.TransformToAncestor(button).Transform(new Point(0, 0)).X;
                                Check(Math.Abs(left - button.Padding.Left - button.BorderThickness.Left) < 1, "choice text follows padding");
                                Check(((FrameworkElement)button.Content).ActualWidth <= button.ActualWidth - button.Padding.Left - button.Padding.Right + 1, "choice wraps within card");
                                return left;
                            }).ToArray();
                            Check(starts.Max() - starts.Min() < 1, "all three choice labels aligned across widths scales and recommendations");
                        }
                    }
                    if (pair.Item3 == StudioContextMethod.Smart) Shot(manager, Path.Combine(output, $"manager-{language}-{dark}.png")); manager.Close();
                }
                var manual = new LiteraryContextManualWindow(owner, L, plan, Measure, (ids, ct) => Apply(ids, null, ct)); manual.Show(); Pump();
                var checks = All<CheckBox>(manual).ToArray(); checks[0].IsChecked = true; checks[1].IsChecked = true; Pump(400);
                Check(measuredIds == 2 && Button(manual, "Studio.Context.ManualAccept").IsEnabled, "selection updates counter");
                var counter = Field<Border>(manual, "_floating"); var overlay = Field<Canvas>(manual, "_overlay"); var oldY = Canvas.GetTop(counter);
                All<ScrollViewer>(manual).First().ScrollToEnd(); Pump(); Check(Canvas.GetTop(counter) == oldY, "counter doesn't scroll");
                All<Thumb>(counter).Single().RaiseEvent(new DragDeltaEventArgs(10000, 10000)); Pump();
                Check(Canvas.GetLeft(counter) + counter.ActualWidth <= overlay.ActualWidth + 1 && Canvas.GetTop(counter) + counter.ActualHeight <= overlay.ActualHeight + 1, "drag counter clamped");
                Shot(manual, Path.Combine(output, $"manual-{language}-{dark}.png")); manual.Close();
                Check(!applied && StudioContextPlan.Stamp(state) == stamp, "manual cancel preserves original");
                manual = new LiteraryContextManualWindow(owner, L, plan, Measure, (ids, ct) => Apply(ids, null, ct));
                var driver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                driver.Tick += (_, _) => { driver.Stop(); All<CheckBox>(manual).First().IsChecked = true; Button(manual, "Studio.Context.ManualAccept").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); };
                driver.Start(); Check(manual.ShowDialog() == true && applied, "manual confirm closes dialog");
                var ratios = new List<double>();
                Task<StudioCompactionResult> Generate(double ratio, IProgress<StudioCompactionProgress> progress, CancellationToken token)
                { ratios.Add(ratio); return Task.FromResult(new StudioCompactionResult("Решение автора: войти в мастерскую.\nОграничение: герой пока не знает тайны.\nОткрытое: кто находится за дверью?", plan.Items.Take(12).Select(i => i.Id).ToHashSet(), 8000, 3000, ratio)); }
                var preview = new LiteraryContextPreviewWindow(owner, L, StudioContextMethod.Smart, Generate, Measure, (_, _, _) => Task.FromResult(false)); preview.Show(); Pump();
                Check(Button(preview, "Studio.Context.PreviewAccept").IsEnabled, "draft acceptance ready");
                Button(preview, "Studio.Context.Stronger").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                Check(ratios.SequenceEqual(new[] { .5, .35 }), "stronger starts from original plan with lower target");
                Shot(preview, Path.Combine(output, $"preview-{language}-{dark}.png")); preview.Close();
                Check(StudioContextPlan.Stamp(state) == stamp, "preview cancel preserves original");
                if (language == "ru" && dark)
                {
                    var cancelled = false;
                    async Task<StudioCompactionResult> Slow(double ratio, IProgress<StudioCompactionProgress> p, CancellationToken ct)
                    { try { await Task.Delay(10000, ct); } catch (OperationCanceledException) { cancelled = true; throw; } return new("unreachable", new HashSet<string>(), 1, 1, ratio); }
                    var cancelWindow = new LiteraryContextPreviewWindow(owner, L, StudioContextMethod.Retelling, Slow, Measure, (_, _, _) => throw new Exception("Must not apply"));
                    cancelWindow.Show(); Pump(); cancelWindow.Close(); cancelWindow.Close(); Pump();
                    Check(cancelled && !cancelWindow.IsVisible && StudioContextPlan.Stamp(state) == stamp, "close cancels generation before releasing window");
                }
                if (language == "ru" && dark) Integration(owner, root, L, output);
                owner.Close();
            }
            if (args.Contains("--model")) ModelSmoke(root, output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { checks = _checks, passed = true }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} checks; {output}"); app.Shutdown(); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Console.WriteLine(ex); return 1; }
    }
    private static void Integration(Window owner, string root, Func<string, string> l, string output)
    {
        using var runtime = new LiteraryChatRuntime(root); var draft = new LiteraryDraftControl(root, l); var fake = new FakeTools();
        var studio = new LiteraryStudioControl(root, draft, new ContentControl { Content = draft }, runtime, () => false, l, "ru", new TextBlock(), new TextBlock(), fake, fake);
        owner.Width = 1460; owner.Height = 880; owner.Content = studio;
        studio.State.Add("User", "AUTHOR_DECISION"); studio.State.Add("Advisor", "UNACCEPTED_PROPOSAL"); studio.State.Input = "UNSENT_QUESTION";
        typeof(LiteraryStudioControl).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(studio, null); studio.Save(); Pump();
        Field<LiteraryContextIndicator>(studio, "_contextButton").Update(new(7000, 8512), "88%", "Context");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; var phase = 0;
        var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        previewTimer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<LiteraryContextPreviewWindow>().FirstOrDefault() is { } preview && Button(preview, "Studio.Context.PreviewAccept").IsEnabled)
            { previewTimer.Stop(); phase = 2; Button(preview, "Studio.Context.PreviewAccept").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
        };
        timer.Tick += (_, _) =>
        {
            if (phase == 0 && Application.Current.Windows.OfType<LiteraryContextManagerWindow>().FirstOrDefault() is { } manager)
            { phase++; timer.Stop(); Check(studio.IsWorking && !studio.CanLeave(), "manager blocks concurrent leave"); previewTimer.Start(); Button(manager, "Studio.Context.Choose.Retelling").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
        };
        timer.Start(); Field<LiteraryContextIndicator>(studio, "_contextButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); timer.Stop();
        Check(phase == 2 && !studio.IsWorking, "studio applies and unblocks"); Check(studio.State.Input == "UNSENT_QUESTION", "studio preserves pending input");
        var saved = new LiteraryStudioStore(new(root)).Load(); Check(saved.Messages.Count == 4 && saved.Messages.Count(m => m.Role == "ContextEvent") == 1, "studio saved originals summary and operation");
        Check(StudioContextPlan.Conversation(saved).Single().Text == "ACCEPTED_SUMMARY", "studio request contains summary only");
        Shot(owner, Path.Combine(output, "studio-context-ring.png"));
        var send = (Task)typeof(LiteraryStudioControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(studio, [false])!;
        while (!send.IsCompleted) Pump(); send.GetAwaiter().GetResult();
        Check(fake.Last is not null && fake.Last.Conversation.Single().Text == "ACCEPTED_SUMMARY", "normal request sees compacted conversation");
        studio.State.Transfer("LONG_WRITER_TASK"); studio.State.Action = "Rewrite"; studio.State.Result = "WRITER_TARGET";
        var plan = StudioContextPlan.Capture(studio.State);
        var candidate = plan.Apply(studio.State, new HashSet<string> { "writer/task" }, "SHORT_WRITER_TASK", StudioContextMethod.Smart, 20, 10, "event");
        studio.State.Messages = candidate.Messages; studio.State.WriterContext = candidate.WriterContext; studio.State.Input = "CHANGE_TONE";
        typeof(LiteraryStudioControl).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(studio, null);
        send = (Task)typeof(LiteraryStudioControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(studio, [false])!;
        while (!send.IsCompleted) Pump(); send.GetAwaiter().GetResult();
        Check(fake.Last?.PreviousTask == "SHORT_WRITER_TASK", "writer uses compacted task");
        Check(StudioContextPlan.Writer(studio.State).Task == "SHORT_WRITER_TASK" && StudioContextPlan.Writer(studio.State).Target == "FAKE_REPLY", "writer retains compression after new result");
        owner.Content = null;
    }
    private sealed class FakeTools : ILiteraryStudioRequests, ILiteraryContextTools
    {
        public bool IsBusy => false; public int ContextCapacity => 8512; public StudioRequest? Last;
        public Task<StudioContextMeter?> MeasureStudioContextAsync(StudioRequest request, Func<string, string> l, CancellationToken ct) => Task.FromResult<StudioContextMeter?>(new(request.Conversation.Count * 300 + 800, ContextCapacity));
        public Task<StudioCompactionResult> CompactStudioContextAsync(StudioContextPlan plan, StudioContextMethod method, double ratio, IProgress<StudioCompactionProgress> progress, CancellationToken ct)
            => Task.FromResult(new StudioCompactionResult("ACCEPTED_SUMMARY", plan.Items.Select(i => i.Id).ToHashSet(), 800, 200, ratio));
        public Task<ParagraphReply> StudioAsync(StudioRequest request, Func<string, string> l, Action<ParagraphReceipt> receipt, Action<int> budget, IProgress<ModelStreamChunk> progress, CancellationToken ct, Action? starting = null, Action? preparing = null)
        { Last = request; return Task.FromResult(new ParagraphReply("FAKE_REPLY", [], new([], []))); }
    }
    private static void ModelSmoke(string root, string output)
    {
        using var runtime = new LiteraryChatRuntime(root); var state = State(); var plan = StudioContextPlan.Capture(state);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)); var time = Stopwatch.StartNew();
        var result = Task.Run(() => runtime.CompactStudioContextAsync(plan, StudioContextMethod.Smart, .5, new LoggingProgress(), timeout.Token)).GetAwaiter().GetResult();
        time.Stop(); Check(result.Text.Length > 0 && result.AfterTokens < result.BeforeTokens, "real GPU retelling reduces input");
        Check(result.ReplacedIds.Count == 14, "real smart keeps recent pair");
        File.WriteAllText(Path.Combine(output, "model-result.json"), JsonSerializer.Serialize(new { seconds = time.Elapsed.TotalSeconds, capacity = runtime.ContextCapacity, result }, ParagraphJson.Options));
        var project = LiteraryProjectStore.ReadProject(root); var chapters = new LiteraryChapterStore(root); chapters.Open();
        var editor = LiteraryEditorSnapshot.Capture(project.Id, root, chapters.Index, chapters.Load(), false);
        var candidate = plan.Apply(state, result.ReplacedIds, result.Text, StudioContextMethod.Smart, result.BeforeTokens, result.AfterTokens, "ARCHIVE_ONLY");
        var request = new StudioRequest(new(LiteraryChatProfile.Advisor, state.Input, editor, [], new Dictionary<string, ParagraphSelection>(), "", state.Session, true), "Discuss", StudioContextPlan.Conversation(candidate), state.Quotes, "", "", [], false);
        var meter = Task.Run(() => runtime.MeasureStudioContextAsync(request, k => k, timeout.Token)).GetAwaiter().GetResult();
        Check(meter is { Input: > 0, Estimate: true }, "real tokenizer estimates complete request");
        File.WriteAllText(Path.Combine(output, "meter.json"), JsonSerializer.Serialize(meter));
    }
    private sealed class LoggingProgress : IProgress<StudioCompactionProgress>
    { public void Report(StudioCompactionProgress value) => Console.WriteLine($"{value.Stage} {value.Done}/{value.Total}"); }
}
