using System.IO;
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

internal static class Program
{
    private static int _checks;
    [STAThread] private static void Main()
    {
        var output = Path.GetFullPath("Тесты/LiteraryJellyValidation/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load("Исходники/AIHub/MainWindow.xaml");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration));
        foreach (var element in xml.Root.Element(ns + "Window.Resources")!.Elements())
            if (element.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" ||
                new[] { "PrimaryButtonStyle", "SecondaryButtonStyle" }.Contains((string?)element.Attribute(x + "Key"))) dictionary.Add(new XElement(element));
        foreach (var dark in new[] { true, false }) foreach (var lang in new[] { "ru", "en" })
        {
            var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            foreach (var pair in new[] { ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8"),
                ("PanelBrush", dark ? "#172033" : "#FFFFFF"), ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"),
                ("TextSecondaryBrush", dark ? "#AAB4C4" : "#5D6470"), ("LineBrush", dark ? "#2D374B" : "#DADDE3") })
                resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            var owner = new Window { Resources = resources, ShowInTaskbar = false, Width = 400, Height = 300 };
            owner.Show();
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
            string L(string key) => translations.GetValueOrDefault(key, key);
            const string source = "Мальчик не оправдывался, не читал лекций о физике и не жаловался на судьбу";
            var facts = Enumerable.Range(0, 5).Select(i => new LiteraryJellyFact { Subject = "Мальчик", Relation = "не оправдывался",
                Evidence = i is >= 1 and <= 3 ? source.Replace("лекций", "лекции") : source }).ToArray();
            facts[4].Subject = ""; facts[4].Kind = "unknown"; facts[4].Value = new string('x', 601);
            IReadOnlyList<LiteraryJellyFact>? saved = null;
            Exception? failure = null;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var window = app.Windows.OfType<Window>().Single(w => w.Title == L("Literary.Jelly.Title"));
                try
                {
                    var fields = All<TextBox>(window).ToArray();
                    var kinds = All<ComboBox>(window).ToArray();
                    var includes = All<CheckBox>(window).ToArray();
                    var confirm = All<Button>(window).Single(b => Equals(b.Content, L("Literary.Jelly.Confirm")));
                    void Submit() { confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
                    bool Red(Control control) => control.Foreground is SolidColorBrush b && b.Color ==
                        (dark ? Color.FromRgb(248, 113, 113) : Color.FromRgb(185, 28, 28));
                    Check(fields.Length == 20, "all fields rendered");
                    Submit();
                    Check(saved is null && window.IsVisible, "invalid submission stays open and cannot save");
                    Check(new[] { 7, 11, 15 }.All(i => Red(fields[i])), "all three inaccurate quotes turn red on first submit");
                    Check(!Red(fields[4]) && !Red(fields[5]) && !Red(fields[6]), "valid fields of failed record keep their normal color");
                    Check(All<TextBlock>(window).Count(t => t.Text == L("Literary.Jelly.Error.ExactQuote") && t.Visibility == Visibility.Visible) == 3,
                        "each bad quote has its own explanation");
                    Check(Red(fields[16]) && Red(fields[18]) && Red(kinds[4]), "empty subject, overlong value and invalid kind are highlighted");
                    Check(Keyboard.FocusedElement == fields[7], "focus moves to first invalid field rather than confirm button");
                    var viewport = All<ScrollViewer>(window).First(s => s.Content is StackPanel panel && panel.Children.OfType<Button>().Contains(confirm));
                    var bounds = fields[7].TransformToAncestor(viewport).TransformBounds(new Rect(fields[7].RenderSize));
                    Check(bounds.Bottom > 0 && bounds.Top < viewport.ActualHeight, "first bad quote is in scroll viewport");
                    var firstHint = All<TextBlock>(window).First(t => t.Text == L("Literary.Jelly.Error.ExactQuote"));
                    var hintBounds = firstHint.TransformToAncestor(viewport).TransformBounds(new Rect(firstHint.RenderSize));
                    Check(hintBounds.Top >= 0 && hintBounds.Bottom <= viewport.ActualHeight, "explanation is visible together with first error");
                    Capture(window, Path.Combine(output, $"{lang}-{dark}-invalid.png"));
                    fields[4].Text = "ГГ";
                    Check(Red(fields[7]), "editing subject does not clear quote error");
                    fields[7].Text = source;
                    Check(!Red(fields[7]) && Red(fields[11]), "correcting one quote clears only its error immediately");
                    includes[2].IsChecked = false;
                    Check(!Red(fields[11]), "excluding record clears its errors");
                    includes[2].IsChecked = true;
                    Check(Red(fields[11]), "reinclusion restores remaining error");
                    fields[11].Text = source; fields[15].Text = source;
                    fields[16].Text = "Мальчик"; fields[18].Text = ""; kinds[4].SelectedIndex = 0;
                    Check(!fields.Any(Red) && !kinds.Any(Red), "all colors restored after correction");
                    Check(!All<TextBlock>(window).Any(t => t.Visibility == Visibility.Visible && t.Text == L("Literary.Jelly.Error.ExactQuote")), "stale explanations disappear");
                    Submit();
                    Check(saved?.Count == 5, "corrected records reach save together");
                    Check(saved!.All(f => f.Evidence == source), "saved quotes remain exact, no normalization bypass");
                }
                catch (Exception ex) { failure = ex; window.Close(); }
            }));
            var result = LiteraryJellyReviewDialog.Show(owner, L, facts.Select(f => new LiteraryJellyReviewItem(f, "001", source)).ToArray(),
                decisions => { foreach (var fact in decisions) LiteraryJellyContract.Validate(fact, source); saved = decisions; return Task.CompletedTask; }, language: lang);
            owner.Close();
            if (failure is not null) throw failure;
            Check(result, "dialog succeeds after corrections");
        }
        var summary = $"PASS: {_checks} checks; RU/EN, dark/light, per-field errors, all invalid records, focus/scroll, live correction, exclude/reinclude, save. No model or user-project writes.";
        File.WriteAllText(Path.Combine(output, "result.txt"), summary);
        Console.WriteLine(summary + "\n" + output); app.Shutdown();
    }
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static IEnumerable<T> All<T>(DependencyObject value)
    { if (value is T item) yield return item; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(value, i))) yield return child; }
    private static void Pump()
    { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static void Capture(FrameworkElement element, string path)
    { var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file); }
}
