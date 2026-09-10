using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Services;

if (args.Length != 1) throw new ArgumentException("New output folder required.");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Output must be new.");
Directory.CreateDirectory(output);
var runtime = new QdrantRuntime(new QdrantOptions { DataDirectory = Path.Combine(output, "qdrant") });
var id = Guid.NewGuid().ToString("N");
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50)))
{
    try
    {
        await runtime.CreateLiteraryIndexAsync(id, deadline.Token);
        var vector = new float[1024]; vector[0] = 1;
        var points = new[] { JsonSerializer.SerializeToElement(new { id = 1, vector, payload = new { text = "Первоисточник", kind = "reference" } }) };
        await runtime.WriteLiteraryPointsAsync(id, points, deadline.Token);
        if (await runtime.LiteraryPointCountAsync(id, deadline.Token) != 1) throw new Exception("Write failed.");
        await runtime.StopAsync(); await runtime.StartAsync(deadline.Token);
        if (await runtime.LiteraryPointCountAsync(id, deadline.Token) != 1) throw new Exception("Persistence failed.");
        await runtime.DeleteLiteraryIndexAsync(id, deadline.Token);
        await runtime.DeleteLiteraryIndexAsync(id, deadline.Token);
        var sourcePath = Path.Combine(output, "original.txt");
        File.WriteAllText(sourcePath, "Первая сцена.\nНовая строка — русский текст.");
        var stage = Path.Combine(output, "staging");
        var prepared = new LiterarySourceIndex(runtime, new FixtureEmbedding(), stage);
        await prepared.PrepareAsync([sourcePath], new Progress<LiteraryPreparationProgress>(), deadline.Token);
        if (!prepared.Ready || prepared.PointCount != 1) throw new Exception("Index was not marked ready.");
        File.WriteAllText(sourcePath, "Changed original after indexing");
        var projects = new LiteraryProjectStore(Path.Combine(output, "index.json"));
        var project = new AIHub.Models.LiteraryProject { ProjectName = "Indexed", Genres = ["fantasy"], BasedOnExistingWorld = true };
        prepared.SetDestination(Path.Combine(output, project.ProjectName));
        var entry = projects.Create(output, project, prepared.Sources, prepared.CopyInto);
        prepared.Commit(); await prepared.DisposeAsync();
        if (Directory.Exists(prepared.DirectoryPath)) throw new Exception("Committed staging retained.");
        if (File.ReadAllText(Path.Combine(entry.ProjectPath, "Materials", "0001_original.txt")).Contains("Changed")) throw new Exception("Unindexed source changes entered project.");
        projects.Remove(entry, LiteraryProjectRemoval.KeepFiles);
        if (!File.Exists(Path.Combine(entry.ProjectPath, "Rag", "Source", "manifest.json"))) throw new Exception("Keep-files lost source index.");
        // Queue the same cleanup used by project deletion and simulate removal of its folder.
        LiterarySourceIndex.QueueProjectDeletion(entry.ProjectPath);
        Directory.Delete(entry.ProjectPath, true);
        await LiterarySourceIndex.RecoverAsync(deadline.Token, runtime);
        if (Directory.Exists(Path.Combine(LiterarySourceIndex.StagingRoot, prepared.Id))) throw new Exception("Deletion marker retained.");
        var abandonedId = Guid.NewGuid().ToString("N");
        await runtime.CreateLiteraryIndexAsync(abandonedId, deadline.Token);
        var abandoned = Path.Combine(stage, abandonedId); Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "pending.json"), JsonSerializer.Serialize(new LiterarySourceIndex.Pending(abandonedId, null)));
        await LiterarySourceIndex.RecoverAsync(deadline.Token, runtime, stage);
        if (Directory.Exists(abandoned)) throw new Exception("Abandoned preparation retained.");
        var book = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты/LiteraryReading/books-local/Пиковая_дама.epub");
        var sections = LiterarySourceReader.Read(book, "Пиковая дама", deadline.Token);
        File.WriteAllText(Path.Combine(output, "book-reader.json"), JsonSerializer.Serialize(new { sections = sections.Count, characters = sections.Sum(s => s.Text.Length), names = sections.Select(s => s.Section) }));
    }
    finally { await runtime.StopAsync(); }
}

Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var source = XDocument.Load(Path.Combine(AppDataPaths.ProjectRoot!, "Исходники/AIHub/MainWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary", source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration), source.Root.Element(ns + "Window.Resources")!.Nodes());
        var owner = new Window { Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString()), Width = 1400, Height = 900, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
        owner.Show();
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
            string L(string key) => words[key];
            foreach (var pair in new[] { ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#9CA3AF" : "#6B7280"), ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8") })
                owner.Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            owner.Background = (Brush)owner.Resources["PanelBrush"];
            var prep = new LiteraryPreparationControl(L); owner.Content = prep; owner.UpdateLayout();
            Pump(() => ((StackPanel)Field(prep, "_rows")).Children.Count > 0, 45);
            if (Buttons(prep).Single(b => Equals(b.Content, L("Literary.Prepare.Next"))).IsEnabled) throw new Exception("Missing Giga incorrectly accepted.");
            Render(owner, Path.Combine(output, $"prepare-{language}-{dark}.png"));
            var acknowledgements = 0;
            ComponentLicenseGate.ConfirmAsync = (_, _) => { acknowledgements++; throw new OperationCanceledException("Test: decline acknowledgement"); };
            Buttons(prep).Single(b => Equals(b.Content, L("Literary.Prepare.Install"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(() => !(bool)Field(prep, "_busy"), 10);
            if (acknowledgements != 1 || Directory.Exists(GigaEmbeddingInstallation.ModelDirectory)) throw new Exception("Download started before licence acknowledgement.");
            ComponentLicenseGate.ConfirmAsync = null;
            prep.Cancel();
            var creator = new LiteraryProjectCreateControl(L, language, output, new LiteraryProjectStore(Path.Combine(output, "projects.json")));
            owner.Content = creator; owner.UpdateLayout();
            var create = (Button)Field(creator, "_create");
            if (!create.IsEnabled) throw new Exception("Empty original project blocked.");
            ((ComboBox)Field(creator, "_type")).SelectedIndex = 1;
            ((List<string>)Field(creator, "_materials")).Add(Path.Combine(output, "missing.epub"));
            typeof(LiteraryProjectCreateControl).GetMethod("RestartIndexing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(creator, null);
            if (create.IsEnabled) throw new Exception("Creation allowed before source indexing.");
            Pump(() => ((Task)Field(creator, "_indexTask")).IsCompleted, 20);
            if (create.IsEnabled) throw new Exception("Creation allowed after source error.");
            ((List<string>)Field(creator, "_materials")).Clear();
            typeof(LiteraryProjectCreateControl).GetMethod("RestartIndexing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(creator, null);
            Pump(() => create.IsEnabled, 20);
            creator.CancelIndexing();
        }
        owner.Close(); app.Shutdown();
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: real Qdrant 1024D write/count/restart/read/delete; RU/EN x dark/light preparation and creation gating. No pretrained model weights loaded/downloaded.");
Console.WriteLine("RAG infrastructure / WPF probe passed.");

static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
static void Pump(Func<bool> done, int seconds)
{
    var start = DateTime.UtcNow;
    while (!done())
    {
        if ((DateTime.UtcNow - start).TotalSeconds > seconds) throw new TimeoutException();
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        Thread.Sleep(20);
    }
}
static IEnumerable<Button> Buttons(DependencyObject root)
{
    if (root is Button button) yield return button;
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Buttons(VisualTreeHelper.GetChild(root, i))) yield return child;
}
static void Render(Window window, string path)
{
    window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
    image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
}

sealed class FixtureEmbedding : ILiterarySourceEmbedding
{
    public async Task EmbedAsync(string inputPath, string outputPath, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        var sections = JsonSerializer.Deserialize<List<LiterarySourceSection>>(await File.ReadAllTextAsync(inputPath, ct))!;
        var vector = new float[1024]; vector[0] = 1;
        var lines = sections.Select((s, i) => JsonSerializer.Serialize(new { id = i + 1, vector, payload = new { text = s.Text, kind = "reference", source = s.Source, section = s.Section } }));
        await File.WriteAllLinesAsync(outputPath, lines, ct);
    }
}
