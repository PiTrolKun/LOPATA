using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

if (args.Length != 1) throw new ArgumentException("Pass a new output directory.");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Use a new directory.");
Directory.CreateDirectory(output);
Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var source = XDocument.Load(Path.Combine(AppDataPaths.ProjectRoot!, "Исходники/AIHub/MainWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration), source.Root.Element(ns + "Window.Resources")!.Nodes());
        var owner = new Window { Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()), Width = 700, Height = 480, FontSize = 14 };
        owner.Show();
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        foreach (var choice in new[] { 2, 0, 1 })
        {
            var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
            string L(string key) => words[key];
            foreach (var pair in new[] { ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#9CA3AF" : "#6B7280"), ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8") })
                owner.Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            var folder = Path.Combine(output, $"{language}-{dark}-{choice}"); Directory.CreateDirectory(folder);
            var store = new LiteraryProjectStore(Path.Combine(folder, "index", "projects.json"));
            var project = store.Create(folder, new LiteraryProject { ProjectName = "Проверка удаления", Genres = ["fantasy"] }, []);
            store.SetActive(project.Id);
            var selection = new LiteraryProjectSelection(); selection.SetProjects([project]); selection.SetActive(project.Id);
            var calls = 0; var sawBusy = false;
            LiteraryProjectDialog? dialog = null;
            dialog = new LiteraryProjectDialog(selection, L, LiteraryProjectDialogMode.Select, store.SetActive, async (entry, mode) =>
            {
                calls++; await Task.Delay(30);
                dialog!.Close(); sawBusy = dialog.IsVisible && !((FrameworkElement)dialog.Content).IsEnabled;
                await Task.Run(() => store.Remove(entry, mode));
            }) { Owner = owner };
            dialog.Show(); dialog.UpdateLayout();
            // The posted callback runs inside the confirmation's nested dispatcher frame.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var confirm = app.Windows.OfType<Window>().Single(w => w.Owner == dialog);
                confirm.UpdateLayout();
                if (choice == 2)
                {
                    var image = new RenderTargetBitmap((int)confirm.ActualWidth, (int)confirm.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(confirm); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, $"{language}-{dark}.png")); png.Save(file);
                }
                var label = L(choice switch { 0 => "Literary.Delete.Keep", 1 => "Literary.Delete.Files", _ => "Literary.Editor.Cancel" });
                Buttons(confirm).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Buttons(dialog).Single(b => Equals(b.Content, "×")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (choice != 2) Pump(() => ((FrameworkElement)dialog.Content).IsEnabled);
            if (calls != (choice == 2 ? 0 : 1)) throw new Exception("Wrong callback count.");
            if (choice != 2 && !sawBusy) throw new Exception("Dialog was closable during deletion.");
            if (store.Load().Projects.Count != (choice == 2 ? 1 : 0)) throw new Exception("Registration mismatch.");
            if (Directory.Exists(project.ProjectPath) != (choice != 1)) throw new Exception("File retention mismatch.");
            if (choice != 2 && selection.ActiveId is not null) throw new Exception("Active selection was retained.");
            dialog.Close();
        }
        owner.Close(); app.Shutdown();
        File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: 12 WPF flows; RU/EN, dark/light; cancel/keep/delete; actual disposable projects; busy close rejected; active selection cleared.");
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
if (failure is not null) throw failure;
Console.WriteLine("WPF deletion probe passed.");

static IEnumerable<Button> Buttons(DependencyObject root)
{
    if (root is Button button) yield return button;
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        foreach (var child in Buttons(VisualTreeHelper.GetChild(root, i))) yield return child;
}
static void Pump(Func<bool> complete)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!complete())
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); Thread.Sleep(10);
    }
}
