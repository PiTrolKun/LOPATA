using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Text.Json;
using AIHub;
using AIHub.Services;

internal static class WpfProbe
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
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var dictionary = new XElement(presentation + "ResourceDictionary", source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration), source.Root.Element(presentation + "Window.Resources")!.Nodes());
                var owner = new Window { Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()), FontSize = 14 };
                owner.Show();
                foreach (var language in new[] { "ru", "en" })
                foreach (var dark in new[] { true, false })
                {
                    var localization = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
                    string L(string key) => localization[key];
                    foreach (var pair in new[] { ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8") })
                        owner.Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
                    var window = new ProcessDiagnosticsWindow(owner, L);
                    window.Show(); window.UpdateLayout();
                    if (QdrantRuntime.Shared.Pid is not null) throw new Exception("Opening diagnostics started Qdrant.");
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var file = File.Create(Path.Combine(output, $"{language}-{dark}.png"))) encoder.Save(file);
                    if (language == "ru" && dark)
                    {
                        var buttons = FindButtons(window).ToArray();
                        buttons.Single(b => Equals(b.Content, L("Processes.Start"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(() => QdrantRuntime.Shared.IsReady, TimeSpan.FromSeconds(35));
                        Pump(() => buttons.Single(b => Equals(b.Content, L("Processes.Stop"))).IsEnabled, TimeSpan.FromSeconds(3));
                        buttons.Single(b => Equals(b.Content, L("Processes.Stop"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(() => QdrantRuntime.Shared.State == "Stopped", TimeSpan.FromSeconds(15));
                        if (!QdrantRuntime.Shared.LastStopGraceful) throw new Exception("UI stop was not graceful.");
                    }
                    window.Close();
                }
                owner.Close(); app.Shutdown();
                File.WriteAllText(Path.Combine(output, "ui-result.txt"), "PASS: RU/EN, dark/light, no auto-start, Start/Stop via WPF, graceful shutdown.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        QdrantRuntime.Shared.ShutdownAsync().GetAwaiter().GetResult(); OwnedProcessRegistry.Shared.Dispose();
        if (failure is not null) throw failure;
    }
    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindButtons(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Pump(Func<bool> complete, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!complete())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF operation did not finish.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
