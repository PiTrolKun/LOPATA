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

static class ModesUi
{
    public static void Run(string output)
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
                var owner = new Window { Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString()), Width = 1400, Height = 900, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                owner.Show();
                foreach (var lang in new[] { "ru", "en" })
                {
                    var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", lang + ".json")))!;
                    string L(string key) => words[key];
                    var create = new LiteraryProjectCreateControl(L, lang, output, new(Path.Combine(output, "ui-index.json")));
                    owner.Content = create; owner.UpdateLayout();
                    object Field(string name) => typeof(LiteraryProjectCreateControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
                    var modes = (ComboBox)Field("_jellyMode");
                    if (modes.Items.Count != 3 || modes.SelectedIndex != 0) throw new Exception("Mode selection");
                    modes.SelectedIndex = 2;
                    ((TextBox)Field("_writerPlan")).Text = "Лиза должна получить письмо.";
                    ((TextBox)Field("_writerAvoid")).Text = "Не раскрывать тайну.";
                    ((TextBox)Field("_advisorPlan")).Text = "Проверять порядок событий.";
                    ((TextBox)Field("_advisorAvoid")).Text = "Не дописывать сцену.";
                    var init = (Action<string>)typeof(LiteraryProjectCreateControl).GetMethod("InitialAnchors", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(create, null)!;
                    var entry = new LiteraryProjectStore(Path.Combine(output, "ui-index.json")).Create(output,
                        new() { ProjectName = "UI-" + lang, Genres = ["fantasy"], JellyExecutor = (string)((ComboBoxItem)modes.SelectedItem).Tag }, [], initializeProject: init);
                    var layout = new LiteraryProjectLayout(entry.ProjectPath);
                    if (new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Writer).Load().Avoid != "Не раскрывать тайну."
                        || new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Advisor).Load().Avoid != "Не дописывать сцену.") throw new Exception("UI role values");
                    modes.BringIntoView(); owner.UpdateLayout(); Capture(owner, Path.Combine(output, lang + "-creation.png"));
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop(); var dialog = app.Windows.Cast<Window>().Last();
                        try
                        {
                            var fields = Descendants(dialog).OfType<TextBox>().ToArray();
                            if (fields.Length != 2 || fields[1].Text != "Не раскрывать тайну.") throw new Exception("Anchor dialog fields");
                            Capture(dialog, Path.Combine(output, lang + "-anchor.png"));
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { dialog.Close(); }
                    };
                    timer.Start();
                    typeof(LiteraryWorkspaceControl).Assembly.GetType("AIHub.Controls.LiteraryPlotAnchorDialog")!.GetMethod("Open")!
                        .Invoke(null, [owner, (Func<string, string>)L, new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Writer), LiteraryChatProfile.Writer]);
                    if (failure is not null) throw failure;
                }
                owner.Close(); app.Shutdown();
                Console.WriteLine("PASS UI RU/EN: three modes, independent fields, anchor dialog");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
