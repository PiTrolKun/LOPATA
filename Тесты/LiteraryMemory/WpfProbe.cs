using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Services;

static class WpfProbe
{
    public static void Run(string output, bool autosaveOnly = false)
    {
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
                if (autosaveOnly)
                {
                    AutosaveProbe.Run(owner, output);
                    owner.Close(); app.Shutdown(); return;
                }
                foreach (var language in new[] { "ru", "en" })
                foreach (var dark in new[] { true, false })
                {
                    var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
                    string L(string key) => words[key];
                    foreach (var pair in new[] { ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#9CA3AF" : "#6B7280"), ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8") })
                        owner.Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
                    owner.Background = (Brush)owner.Resources["PanelBrush"];
                    var registry = new LiteraryProjectStore(Path.Combine(output, language + dark + "-registry.json"));
                    var entry = registry.Create(output, new() { ProjectName = language + dark, Genres = ["fantasy"] }, []);
                    var project = LiteraryProjectStore.ReadProject(entry.ProjectPath);
                    var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var workspace = new LiteraryWorkspaceControl(entry, project, L, (_, _) => waiting.Task);
                    owner.Content = workspace; owner.UpdateLayout();
                    var writer = (LiteraryChatControl)Field(workspace, "_writer");
                    var advisor = (LiteraryChatControl)Field(workspace, "_advisor");
                    Pump(() => writer.ActionsBlocked);
                    if (workspace.EditorHost.IsEnabled) throw new Exception("Editor is not locked during indexing.");
                    foreach (var chat in new[] { writer, advisor })
                    {
                        var input = (TextBox)Field(chat, "_input"); input.Text = "Вопрос во время индексации";
                        if (input.IsReadOnly || !input.IsEnabled || ((Button)Field(chat, "_send")).IsEnabled) throw new Exception("Chat input/send lock incorrect.");
                        var task = (Task)typeof(LiteraryChatControl).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chat, null)!;
                        if (!task.IsCompleted || input.Text.Length == 0) throw new Exception("Keyboard path bypassed indexing lock.");
                    }
                    Render(owner, Path.Combine(output, $"indexing-{language}-{dark}.png"));
                    waiting.SetException(new IOException("Injected index failure"));
                    Pump(() => !writer.ActionsBlocked);
                    if (!workspace.EditorHost.IsEnabled || ((Button)Field(workspace, "_memoryRetry")).Visibility != Visibility.Visible) throw new Exception("Failure did not unlock workspace.");
                    if (!workspace.CanLeave()) throw new Exception("Cannot leave after failure.");
                    owner.Content = null; owner.UpdateLayout();
                    var restored = new LiteraryWorkspaceControl(entry, project, L, (_, _) => Task.CompletedTask);
                    owner.Content = restored; owner.UpdateLayout();
                    var restoredWriter = (LiteraryChatControl)Field(restored, "_writer");
                    Pump(() => !restoredWriter.ActionsBlocked);
                    if (((TextBox)Field(restoredWriter, "_input")).Text != "Вопрос во время индексации") throw new Exception("Unsent input did not restore.");
                    var unavailable = entry.ProjectPath + "-offline";
                    Directory.Move(entry.ProjectPath, unavailable);
                    Pump(() => restoredWriter.ActionsBlocked);
                    if (Directory.Exists(entry.ProjectPath)) throw new Exception("Missing open project resurrected.");
                    Directory.Move(unavailable, entry.ProjectPath);
                    restored.CanLeave(); owner.Content = null; owner.UpdateLayout();
                }
                owner.Close(); app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("WPF probe failed", failure);
    }
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("WPF timeout.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
    }
    private static void Render(Window window, string path)
    {
        window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
}
