using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Services;

static class JellyUiProbe
{
    public static void Run(string output)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var source = XDocument.Load(Path.Combine(AppDataPaths.ProjectRoot!, "Исходники/AIHub/MainWindow.xaml"));
                XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = new XElement(ns + "ResourceDictionary", source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration), source.Root.Element(ns + "Window.Resources")!.Nodes());
                var owner = new Window { Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString()), Width = 1000, Height = 900, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
                owner.Show();
                foreach (var language in new[] { "ru", "en" })
                {
                    var dark = language == "ru";
                    foreach (var pair in new[] { ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#9CA3AF" : "#6B7280") }) owner.Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
                    owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#111827" : "#F8F8F8"));
                    owner.Resources["LineBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#2D374B" : "#DADDE3"));
                    var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
                    string L(string key) => words[key];
                    const string text = "Лиза передала ключ Томскому.";
                    var facts = Enumerable.Range(0, 2).Select(_ => new LiteraryJellyReviewItem(new() { Subject = "Лиза", Relation = "передала", Value = "ключ Томскому", Evidence = text }, "001", text)).ToArray();
                    var attempts = 0; var phase = 0;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                    timer.Tick += (_, _) =>
                    {
                        try
                        {
                            var window = app.Windows.Cast<Window>().FirstOrDefault(w => w.Title == L("Literary.Jelly.Title"));
                            if (window is null) return;
                            var boxes = Descendants<TextBox>(window).ToArray();
                            var button = Descendants<Button>(window).Single(b => (string?)b.Content == L("Literary.Jelly.Confirm"));
                            if (phase == 0)
                            {
                                window.UpdateLayout(); Capture((FrameworkElement)window.Content, Path.Combine(output, language + "-review.png"));
                                boxes[2].Text = "ключ Германну";
                                Descendants<CheckBox>(window).Last().IsChecked = false;
                                boxes[3].Text = "не цитата"; phase++; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }
                            else if (phase == 1)
                            {
                                if (attempts != 0 || boxes[2].Text != "ключ Германну") throw new Exception("Validation lost edits.");
                                boxes[3].Text = text; phase++; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }
                            else if (phase == 2 && attempts == 1 && button.IsEnabled)
                            {
                                if (boxes[2].Text != "ключ Германну") throw new Exception("I/O error lost edits.");
                                phase++; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }
                        }
                        catch (Exception ex) { failure = ex; timer.Stop(); foreach (var w in app.Windows.Cast<Window>().Where(w => w != owner).ToArray()) w.Close(); }
                    };
                    timer.Start();
                    var result = LiteraryJellyReviewDialog.Show(owner, L, facts, decisions =>
                    {
                        attempts++;
                        if (attempts == 1) throw new IOException("Simulated disk failure");
                        if (decisions[0].Value != "ключ Германну" || decisions[1].Accepted) throw new Exception("Wrong UI decisions.");
                        return Task.CompletedTask;
                    });
                    timer.Stop();
                    if (!result || attempts != 2 || failure is not null) throw failure ?? new Exception("UI did not complete");
                    Console.WriteLine("PASS " + language + " modal: all facts, edit/exclude, validation/I/O failure preservation, confirm");
                }
                owner.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(35))) throw new TimeoutException("UI probe timeout");
        if (failure is not null) throw failure;
    }
    static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    static void Capture(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
